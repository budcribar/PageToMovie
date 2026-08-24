using System.Globalization;
using System.Text.Json;
using PageToMovie.Engine;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--details"))
{
    Console.Error.WriteLine("Usage: dotnet run --project host/tools/ClipDurationCalibration -- <project-or-corpus-root> [--details]");
    return 2;
}

var corpusRoot = Path.GetFullPath(args[0]);
if (!Directory.Exists(corpusRoot))
{
    Console.Error.WriteLine($"Corpus root does not exist: {corpusRoot}");
    return 2;
}

var showDetails = args.Length == 2;
var blueprints = Directory.EnumerateFiles(corpusRoot, "blueprint.clips.grok.json", SearchOption.AllDirectories)
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (blueprints.Length == 0)
{
    Console.Error.WriteLine($"No blueprint.clips.grok.json files found under {corpusRoot}");
    return 1;
}

var projects = new List<ProjectResult>();
foreach (var blueprintPath in blueprints)
    projects.Add(AnalyzeProject(blueprintPath));

Console.WriteLine($"Clip-duration calibration corpus: {corpusRoot}");
Console.WriteLine($"Discovered {projects.Count} project snapshot(s). No network or paid API calls were made.\n");

foreach (var project in projects)
    PrintProject(project, showDetails);

var usable = projects.SelectMany(project => project.Rows).ToArray();
if (usable.Length == 0)
{
    Console.WriteLine("No blueprint-matched per-clip duration sidecars were found; no accuracy score can be calculated.");
    return 1;
}

PrintMetrics("ALL ELIGIBLE CLIPS", usable);
foreach (var modelGroup in usable.GroupBy(row => row.ModelId).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
    PrintMetrics($"MODEL {modelGroup.Key}", modelGroup);

Console.WriteLine("\nEligibility rule: only exact scene_NN_clip_NN.mp4.duration.json matches are scored.");
Console.WriteLine("Scene composites, orphan sidecars, and projects without measured per-clip durations are reported but excluded.");
return 0;

static ProjectResult AnalyzeProject(string blueprintPath)
{
    var projectDir = Path.GetDirectoryName(blueprintPath)!;
    var configPath = Path.Combine(projectDir, "pipeline_config.json");
    if (!File.Exists(configPath))
        return ProjectResult.Skipped(projectDir, "pipeline_config.json is missing");

    try
    {
        using var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
        var modelId = ReadString(configDoc.RootElement, "model_name");
        if (string.IsNullOrWhiteSpace(modelId))
            return ProjectResult.Skipped(projectDir, "pipeline_config.json has no model_name");

        var bounds = ClipDurationEstimator.ResolveBoundsForModel(modelId);
        using var blueprintDoc = JsonDocument.Parse(File.ReadAllText(blueprintPath));
        if (!blueprintDoc.RootElement.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
            return ProjectResult.Skipped(projectDir, "blueprint has no scenes array");

        var rows = new List<ClipRow>();
        var plannedClipCount = 0;
        foreach (var scene in scenes.EnumerateArray())
        {
            if (!TryReadInt(scene, "scene_number", out var sceneNumber) ||
                !scene.TryGetProperty("veo_clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var clip in clips.EnumerateArray())
            {
                plannedClipCount++;
                if (!TryReadInt(clip, "clip_number", out var clipNumber))
                    continue;
                var durationPath = Path.Combine(projectDir, "assets", "video", $"scene_{sceneNumber:D2}_clip_{clipNumber:D2}.mp4.duration.json");
                if (!File.Exists(durationPath) || !TryReadActualSeconds(durationPath, out var actualSeconds))
                    continue;

                var plannedSeconds = ReadPositiveDouble(clip, "duration_seconds");
                var estimated = ClipDurationEstimator.EstimateForClip(
                    clip, bounds.MinSeconds, bounds.MaxSeconds, bounds.AbsMaxSeconds);
                var continuation = string.Equals(
                    ReadString(clip, "veo_continuation_source"),
                    "extend_previous",
                    StringComparison.OrdinalIgnoreCase);
                var resolved = ClipDurationEstimator.ResolveActualDurationForModel(modelId, estimated, continuation);
                rows.Add(new ClipRow(
                    projectDir, modelId, sceneNumber, clipNumber, plannedSeconds,
                    resolved, actualSeconds, continuation));
            }
        }

        var videoDir = Path.Combine(projectDir, "assets", "video");
        var perClipSidecars = Directory.Exists(videoDir)
            ? Directory.EnumerateFiles(videoDir, "scene_??_clip_??.mp4.duration.json", SearchOption.TopDirectoryOnly).Count()
            : 0;
        var sceneSidecars = Directory.Exists(videoDir)
            ? Directory.EnumerateFiles(videoDir, "scene_??.mp4.duration.json", SearchOption.TopDirectoryOnly).Count()
            : 0;
        return new ProjectResult(
            projectDir, modelId, plannedClipCount, perClipSidecars, sceneSidecars,
            rows, null);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
    {
        return ProjectResult.Skipped(projectDir, ex.Message);
    }
}

static void PrintProject(ProjectResult project, bool showDetails)
{
    Console.WriteLine(project.ProjectDir);
    if (project.SkipReason is not null)
    {
        Console.WriteLine($"  SKIPPED: {project.SkipReason}\n");
        return;
    }

    Console.WriteLine($"  model={project.ModelId}; blueprint clips={project.PlannedClipCount}; " +
                      $"per-clip sidecars={project.PerClipSidecarCount}; matched={project.Rows.Count}; " +
                      $"scene-only sidecars={project.SceneSidecarCount}");
    if (project.Rows.Count > 0)
        PrintMetrics("  project", project.Rows);
    else
        Console.WriteLine("  Not scored: no exact per-clip measurement matches.");

    if (showDetails)
    {
        foreach (var row in project.Rows.OrderByDescending(row => Math.Abs(row.EstimatedSeconds - row.ActualSeconds)))
        {
            Console.WriteLine(
                $"    S{row.SceneNumber:D2}C{row.ClipNumber:D2} {(row.IsContinuation ? "extend" : "fresh ")} " +
                $"plan={Format(row.PlannedSeconds),5} estimate={row.EstimatedSeconds,5:F1} " +
                $"actual={row.ActualSeconds,6:F2} error={row.EstimatedSeconds - row.ActualSeconds,6:F2}");
        }
    }
    Console.WriteLine();
}

static void PrintMetrics(string label, IEnumerable<ClipRow> source)
{
    var rows = source.ToArray();
    var actual = rows.Sum(row => row.ActualSeconds);
    var estimated = rows.Sum(row => row.EstimatedSeconds);
    var plannedRows = rows.Where(row => row.PlannedSeconds is > 0).ToArray();
    var estimateMae = rows.Average(row => Math.Abs(row.EstimatedSeconds - row.ActualSeconds));
    var estimateRmse = Math.Sqrt(rows.Average(row => Math.Pow(row.EstimatedSeconds - row.ActualSeconds, 2)));
    var plannedText = plannedRows.Length == 0
        ? "stored plan=n/a"
        : $"stored plan={plannedRows.Sum(row => row.PlannedSeconds!.Value):F2}, " +
          $"MAE={plannedRows.Average(row => Math.Abs(row.PlannedSeconds!.Value - row.ActualSeconds)):F2}s";
    Console.WriteLine(
        $"{label}: n={rows.Length}; actual={actual:F2}s; estimate={estimated:F2}s " +
        $"(bias={PercentBias(estimated, actual):+0.0;-0.0;0.0}%); MAE={estimateMae:F2}s; RMSE={estimateRmse:F2}s; {plannedText}");
}

static string PercentBias(double estimated, double actual) =>
    actual > 0 ? ((estimated / actual - 1) * 100).ToString("0.0", CultureInfo.InvariantCulture) : "0.0";

static string Format(double? value) => value is > 0
    ? value.Value.ToString("F1", CultureInfo.InvariantCulture)
    : "n/a";

static string ReadString(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        return "";
    return value.GetString()?.Trim() ?? "";
}

static bool TryReadInt(JsonElement element, string propertyName, out int value)
{
    value = 0;
    return element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
}

static double? ReadPositiveDouble(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var value))
        return null;
    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number > 0)
        return number;
    if (value.ValueKind == JsonValueKind.String &&
        double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number > 0)
        return number;
    return null;
}

static bool TryReadActualSeconds(string durationPath, out double seconds)
{
    seconds = 0;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(durationPath));
        return doc.RootElement.TryGetProperty("seconds", out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out seconds) && seconds > 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
    {
        return false;
    }
}

internal sealed record ClipRow(
    string ProjectDir,
    string ModelId,
    int SceneNumber,
    int ClipNumber,
    double? PlannedSeconds,
    double EstimatedSeconds,
    double ActualSeconds,
    bool IsContinuation);

internal sealed record ProjectResult(
    string ProjectDir,
    string ModelId,
    int PlannedClipCount,
    int PerClipSidecarCount,
    int SceneSidecarCount,
    IReadOnlyList<ClipRow> Rows,
    string? SkipReason)
{
    public static ProjectResult Skipped(string projectDir, string reason) =>
        new(projectDir, "", 0, 0, 0, Array.Empty<ClipRow>(), reason);
}
