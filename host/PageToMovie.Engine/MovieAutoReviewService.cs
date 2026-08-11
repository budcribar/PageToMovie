using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Engine;

/// <summary>
/// Full-movie AI review service. Evaluates the complete film narrative and visual continuity.
/// Grok path: Act/Scene-group chunking (10-12 images/request) + master synthesis call.
/// Gemini path: Video-native evaluation.
/// </summary>
public sealed class MovieAutoReviewService
{
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;

    private readonly ProjectStore _projects;
    private readonly IVisionClient _vision;
    private readonly IChatClient? _chat;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<MovieAutoReviewService> _log;

    public MovieAutoReviewService(
        ProjectStore projects,
        IVisionClient vision,
        ProjectTelemetryService telemetry,
        IChatClient? chat = null,
        ILogger<MovieAutoReviewService>? log = null)
    {
        _projects = projects;
        _vision = vision;
        _telemetry = telemetry;
        _chat = chat;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MovieAutoReviewService>.Instance;
    }

    public string ReportPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "assets", "review", "movie_review.json");

    public async Task<MovieAutoReviewReport?> LoadReportAsync(string projectId, CancellationToken ct = default)
    {
        var path = ReportPath(projectId);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MovieAutoReviewReport>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveReportAsync(MovieAutoReviewReport report, CancellationToken ct = default)
    {
        var projectDir = await _projects.GetProjectDirAsync(report.ProjectId, ct).ConfigureAwait(false);
        var path = Path.Combine(projectDir, "assets", "review", "movie_review.json");
        var issues = StructuredOperationArtifacts.RequireJsonProperties(report, "projectId");
        if (issues.Any(i => i.Severity == ModelValidationSeverity.Error))
            throw new InvalidOperationException(string.Join(" ", issues.Select(i => i.Message)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOpts) + "\n", ct).ConfigureAwait(false);
        await StructuredOperationArtifacts.WriteAsync(
            projectDir, "movie_multimodal_review", null,
            new { report.ProjectId }, report, issues, ct).ConfigureAwait(false);
        _projects.TriggerAutoGitCommit(report.ProjectId, $"Update full movie AI review report (Score: {report.OverallScore}/10)");
    }

    /// <summary>
    /// Evaluates the full movie using scene-chunked vision requests + master synthesis.
    /// </summary>
    public async Task<MovieAutoReviewReport> ReviewMovieAsync(
        string projectId,
        IReadOnlyList<MovieAutoReviewKeyframe> keyframes,
        Action<int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_vision.IsConfigured)
            throw new InvalidOperationException("AI service key required for full movie review.");

        using var _telScope = _telemetry.UseProject(projectId);
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);

        onProgress?.Invoke(10, "Organizing scene keyframes for full movie review…");

        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        var reviewModel = ProjectModelSelection.RequireVideoReview(cfg, "Full movie review");
        var report = new MovieAutoReviewReport
        {
            ProjectId = projectId,
            ProviderUsed = SupportedModelCatalog.CatalogProviderId(reviewModel, "vision")
                           ?? SupportedModelCatalog.CatalogProviderId(reviewModel, "chat")
                           ?? "",
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
        };

        // Group keyframes by scene groups of 4-5 scenes (~8-10 keyframes per Grok call)
        var scenesMap = keyframes
            .GroupBy(k => k.SceneNumber)
            .OrderBy(g => g.Key)
            .ToList();

        var validFramesCount = keyframes.Count(k => !string.IsNullOrWhiteSpace(k.Base64));
        if (scenesMap.Count == 0 || validFramesCount == 0)
        {
            throw new InvalidOperationException("No valid visual keyframe images were provided for movie review. Generate scenes and sample keyframes first.");
        }

        const int scenesPerChunk = 4;
        var chunks = scenesMap
            .Select((s, idx) => new { SceneGroup = s, ChunkIndex = idx / scenesPerChunk })
            .GroupBy(x => x.ChunkIndex)
            .ToList();

        var totalChunks = chunks.Count;
        var groupFeedbacks = new List<MovieSceneGroupFeedback>();

        for (var i = 0; i < totalChunks; i++)
        {
            var chunk = chunks[i];
            var sceneList = chunk.Select(c => c.SceneGroup.Key).ToList();
            var chunkFrames = chunk.SelectMany(c => c.SceneGroup).ToList();
            var rangeStr = sceneList.Count == 1 ? $"Scene {sceneList[0]}" : $"Scenes {sceneList.Min()}-{sceneList.Max()}";

            var stepPct = 20 + (int)((double)(i + 1) / totalChunks * 60);
            onProgress?.Invoke(stepPct, $"AI reviewing {rangeStr} ({i + 1}/{totalChunks})…");

            var feedback = await EvaluateSceneChunkAsync(projectId, rangeStr, sceneList, chunkFrames, reviewModel, ct).ConfigureAwait(false);
            groupFeedbacks.Add(feedback);
        }

        onProgress?.Invoke(85, "Synthesizing master full-movie review report…");

        // Master synthesis call
        report.GroupFeedback = groupFeedbacks;
        report.FlaggedScenes = groupFeedbacks.SelectMany(f => f.SceneNumbers.Where(_ => f.Score < 7)).Distinct().OrderBy(s => s).ToList();

        var avgScore = (int)Math.Round(groupFeedbacks.Average(g => g.Score));
        var avgContinuity = (int)Math.Round(groupFeedbacks.Average(g => g.ContinuityScore));
        var avgCharacter = (int)Math.Round(groupFeedbacks.Average(g => g.CharacterScore));
        var avgLighting = (int)Math.Round(groupFeedbacks.Average(g => g.LightingScore));
        var avgPacing = (int)Math.Round(groupFeedbacks.Average(g => g.PacingScore));
        var avgDialogue = (int)Math.Round(groupFeedbacks.Average(g => g.DialogueScore));
        var avgMusic = (int)Math.Round(groupFeedbacks.Average(g => g.MusicScore));

        report.OverallScore = Math.Clamp(avgScore, 1, 10);
        report.Verdict = report.OverallScore >= 8 ? "Pass — Strong Continuity" : report.OverallScore >= 6 ? "Needs Polish" : "Continuity Fixes Needed";

        report.CategoryScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Continuity & Transitions"] = Math.Clamp(avgContinuity, 1, 10),
            ["Character Consistency"] = Math.Clamp(avgCharacter, 1, 10),
            ["Lighting & Color Grade"] = Math.Clamp(avgLighting, 1, 10),
            ["Pacing & Editing"] = Math.Clamp(avgPacing, 1, 10),
            ["Dialogue & Script Fidelity"] = Math.Clamp(avgDialogue, 1, 10),
            ["Background Music & Audio Score"] = Math.Clamp(avgMusic, 1, 10),
        };

        var summarySb = new System.Text.StringBuilder();
        summarySb.AppendLine($"Full executive movie review completed across {scenesMap.Count} scenes ({groupFeedbacks.Count} act sequences).");
        summarySb.AppendLine($"• Continuity & Transitions: {avgContinuity}/10");
        summarySb.AppendLine($"• Character Consistency: {avgCharacter}/10");
        summarySb.AppendLine($"• Lighting & Color Grade: {avgLighting}/10");
        summarySb.AppendLine($"• Pacing & Editing: {avgPacing}/10");
        summarySb.AppendLine($"• Dialogue & Script Fidelity: {avgDialogue}/10");
        summarySb.AppendLine($"• Background Music & Audio Score: {avgMusic}/10");
        if (report.FlaggedScenes.Count > 0)
        {
            summarySb.AppendLine($"Recommend touching up Scene(s): {string.Join(", ", report.FlaggedScenes)}.");
        }
        else
        {
            summarySb.AppendLine("Excellent visual continuity and character consistency across all scene transitions.");
        }
        report.SummaryNotes = summarySb.ToString().Trim();

        // Master AI Executive Director Synthesis Pass
        report.ExecutiveSummary = await SynthesizeExecutiveSummaryAsync(report, groupFeedbacks, reviewModel, ct).ConfigureAwait(false);

        await SaveReportAsync(report, ct).ConfigureAwait(false);
        onProgress?.Invoke(100, "Full movie review ready!");
        return report;
    }

    private async Task<MovieSceneGroupFeedback> EvaluateSceneChunkAsync(
        string projectId,
        string rangeStr,
        List<int> sceneNumbers,
        List<MovieAutoReviewKeyframe> frames,
        string reviewModel,
        CancellationToken ct)
    {
        var feedback = new MovieSceneGroupFeedback
        {
            SceneRange = rangeStr,
            SceneNumbers = sceneNumbers,
            Score = 8,
            ContinuityScore = 8,
            CharacterScore = 8,
            LightingScore = 8,
            PacingScore = 8,
            DialogueScore = 8,
            MusicScore = 8,
            ContinuityNotes = "Visual flow matches screenplay setting.",
            VisualConsistencyNotes = "Character locks consistent across cuts.",
            LightingNotes = "Atmospheric exposure and palette match mood.",
            DialogueNotes = "Character dialogue delivery and lip movement align with screenplay lines.",
            AudioNotes = "Background music transitions smoothly and fades cleanly without abrupt cuts.",
        };

        var tempWorkDir = Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "assets", "review", $"_chunk_{rangeStr.Replace(' ', '_')}");
        try
        {
            Directory.CreateDirectory(tempWorkDir);
            var imageFiles = new List<(string Path, string Label)>();
            var idx = 0;
            foreach (var f in frames.Take(12))
            {
                if (string.IsNullOrWhiteSpace(f.Base64)) continue;
                var b64 = f.Base64.Trim();
                var comma = b64.IndexOf(',');
                if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                    b64 = b64[(comma + 1)..];

                byte[] bytes;
                try { bytes = Convert.FromBase64String(b64); } catch { continue; }
                if (bytes.Length < 32) continue;

                idx++;
                var ext = f.Mime.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
                var p = Path.Combine(tempWorkDir, $"f{idx:D2}_S{f.SceneNumber:D2}.{ext}");
                await File.WriteAllBytesAsync(p, bytes, ct).ConfigureAwait(false);
                imageFiles.Add((p, $"SCENE_{f.SceneNumber:D2}"));
            }

            if (imageFiles.Count > 0 && _vision.IsConfigured)
            {
                var prompt = $@"You are a professional film director reviewing visual keyframe sequence {rangeStr} of a movie cut.
Critically evaluate these 6 key filmmaking categories and assign an independent score (1-10) for each:
1. Continuity & Transitions (shot-to-shot spatial alignment, character position, camera movement flow)
2. Character Consistency & Wardrobe (facial structure lock, outfit drift, visual identity retention)
3. Lighting & Color Grading (exposure consistency, palette stability, shadow direction across cuts)
4. Pacing & Editing (visual narrative rhythm, shot length variety, tone matching beat intensity)
5. Dialogue & Script Fidelity (speaking posture, mouth/lip movement alignment, character line execution matching prompt beats)
6. Background Music & Audio Score (audio transition smoothness, music cues fading/ending gracefully vs abrupt cutoffs, score volume balance)

Return valid JSON with non-generic, specific observations:
{{
  ""overallScore"": 8,
  ""continuityScore"": 8,
  ""characterScore"": 8,
  ""lightingScore"": 8,
  ""pacingScore"": 8,
  ""dialogueScore"": 8,
  ""musicScore"": 8,
  ""continuityNotes"": ""Specific observations on visual transitions and spatial alignment"",
  ""visualConsistencyNotes"": ""Specific observations on character lock and costume drift"",
  ""lightingNotes"": ""Specific observations on color palette and lighting continuity"",
  ""dialogueNotes"": ""Specific observations on spoken dialogue delivery and lip movement alignment"",
  ""audioNotes"": ""Specific observations on music cue transitions, fade-outs, and audio ending smoothness""
}}";
                var imagePaths = imageFiles.Select(x => x.Path).ToList();
                var operation = new MultimodalReviewOperation<MovieSceneGroupFeedback>(
                    _vision, imagePaths, reviewModel, "movie_scene_group_review", "movie-scene-review.v1",
                    raw => ParseSceneGroupFeedback(raw, rangeStr, sceneNumbers), ValidateSceneGroupFeedback);
                var observation = new MultimodalReviewObservation(
                    rangeStr, imageFiles.Select(x => x.Label).ToArray(), prompt);
                var execution = await operation.ExecuteAsync(observation, ct).ConfigureAwait(false);
                if (!execution.Success || execution.Value is null)
                    throw new InvalidOperationException(execution.Error ?? string.Join(" ", execution.ValidationIssues.Select(i => i.Message)));
                feedback = execution.Value;
                await SaveExecutionManifestAsync(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), $"movie_scene_group_review_{sceneNumbers.Min():D2}_{sceneNumbers.Max():D2}", execution, ct).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException($"{rangeStr} has no valid visual observations to review.");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error evaluating scene chunk {Range}", rangeStr);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(tempWorkDir)) Directory.Delete(tempWorkDir, true); } catch { /* */ }
        }

        return feedback;
    }

    private async Task<string> SynthesizeExecutiveSummaryAsync(
        MovieAutoReviewReport report,
        IReadOnlyList<MovieSceneGroupFeedback> groupFeedbacks,
        string reviewModel,
        CancellationToken ct)
    {
        var sysPrompt = "You are an Executive Film Director and Post-Production Supervisor writing a high-level Executive Director Summary Report for a complete movie. " +
                        "Do NOT list or repeat each scene block-by-block. Instead, synthesize a unified, insightful executive overview (3-5 well-structured sections) evaluating overall visual narrative continuity, character lock consistency, lighting mood, dialogue & lip-sync delivery, background music transitions, and final recommendations.";

        var promptSb = new System.Text.StringBuilder();
        promptSb.AppendLine($"Project ID: {report.ProjectId}");
        promptSb.AppendLine($"Overall Score: {report.OverallScore}/10 — Verdict: {report.Verdict}");
        promptSb.AppendLine("\nCategory Scores:");
        foreach (var (cat, score) in report.CategoryScores)
        {
            promptSb.AppendLine($"- {cat}: {score}/10");
        }
        promptSb.AppendLine("\nEvaluated Sequence Feedbacks (for synthesis input):");
        foreach (var gf in groupFeedbacks)
        {
            promptSb.AppendLine($"[{gf.SceneRange} - Score {gf.Score}/10]");
            if (!string.IsNullOrWhiteSpace(gf.ContinuityNotes)) promptSb.AppendLine($"  Continuity: {gf.ContinuityNotes}");
            if (!string.IsNullOrWhiteSpace(gf.VisualConsistencyNotes)) promptSb.AppendLine($"  Character Lock: {gf.VisualConsistencyNotes}");
            if (!string.IsNullOrWhiteSpace(gf.LightingNotes)) promptSb.AppendLine($"  Lighting/Tone: {gf.LightingNotes}");
            if (!string.IsNullOrWhiteSpace(gf.DialogueNotes)) promptSb.AppendLine($"  Dialogue: {gf.DialogueNotes}");
            if (!string.IsNullOrWhiteSpace(gf.AudioNotes)) promptSb.AppendLine($"  Audio/Music: {gf.AudioNotes}");
        }

        var fullPrompt = $"{sysPrompt}\n\nReview Data:\n{promptSb}";
        var operation = new MultimodalReviewOperation<ExecutiveReviewSummary>(
            _vision, Array.Empty<string>(), reviewModel, "movie_review_synthesis", "movie-review-synthesis.v1",
            raw => string.IsNullOrWhiteSpace(raw)
                ? ModelParseResult<ExecutiveReviewSummary>.Failure(new ModelValidationIssue("missing_summary", "Executive summary is empty.", "$"))
                : ModelParseResult<ExecutiveReviewSummary>.Success(new(raw.Trim())),
            summary => string.IsNullOrWhiteSpace(summary.Text)
                ? [new ModelValidationIssue("missing_summary", "Executive summary is empty.", "$.text")]
                : Array.Empty<ModelValidationIssue>());
        var execution = await operation.ExecuteAsync(
            new MultimodalReviewObservation("Full movie synthesis", Array.Empty<string>(), fullPrompt), ct).ConfigureAwait(false);
        if (execution.Success && execution.Value is not null)
        {
            await SaveExecutionManifestAsync(await _projects.GetProjectDirAsync(report.ProjectId, ct).ConfigureAwait(false), "movie_review_synthesis", execution, ct).ConfigureAwait(false);
            return execution.Value.Text;
        }

        _log.LogWarning("Executive summary lifecycle failed; using structured deterministic summary. {Error}", execution.Error);
        return BuildFallbackExecutiveSummary(report, groupFeedbacks);
    }

    private sealed record ExecutiveReviewSummary(string Text);

    internal static ModelParseResult<MovieSceneGroupFeedback> ParseSceneGroupFeedback(
        string raw, string rangeStr, IReadOnlyList<int> sceneNumbers)
    {
        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
                return ModelParseResult<MovieSceneGroupFeedback>.Failure(
                    new ModelValidationIssue("invalid_json", "Scene review did not contain a JSON object."));

            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            int Score(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var score)
                ? score
                : 0;
            string Note(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

            return ModelParseResult<MovieSceneGroupFeedback>.Success(new MovieSceneGroupFeedback
            {
                SceneRange = rangeStr,
                SceneNumbers = sceneNumbers.ToList(),
                Score = Score("overallScore") is var overall && overall > 0 ? overall : Score("score"),
                ContinuityScore = Score("continuityScore"),
                CharacterScore = Score("characterScore"),
                LightingScore = Score("lightingScore"),
                PacingScore = Score("pacingScore"),
                DialogueScore = Score("dialogueScore"),
                MusicScore = Score("musicScore"),
                ContinuityNotes = Note("continuityNotes"),
                VisualConsistencyNotes = Note("visualConsistencyNotes"),
                LightingNotes = Note("lightingNotes"),
                DialogueNotes = Note("dialogueNotes"),
                AudioNotes = Note("audioNotes"),
            });
        }
        catch (Exception ex)
        {
            return ModelParseResult<MovieSceneGroupFeedback>.Failure(
                new ModelValidationIssue("invalid_json", $"Scene review JSON could not be parsed: {ex.Message}"));
        }
    }

    internal static IReadOnlyList<ModelValidationIssue> ValidateSceneGroupFeedback(MovieSceneGroupFeedback feedback)
    {
        var issues = new List<ModelValidationIssue>();
        var scores = new Dictionary<string, int>
        {
            ["overallScore"] = feedback.Score,
            ["continuityScore"] = feedback.ContinuityScore,
            ["characterScore"] = feedback.CharacterScore,
            ["lightingScore"] = feedback.LightingScore,
            ["pacingScore"] = feedback.PacingScore,
            ["dialogueScore"] = feedback.DialogueScore,
            ["musicScore"] = feedback.MusicScore,
        };
        foreach (var (name, score) in scores.Where(pair => pair.Value is < 1 or > 10))
            issues.Add(new("invalid_score", $"{name} must be between 1 and 10.", "$." + name));

        if (string.IsNullOrWhiteSpace(feedback.ContinuityNotes))
            issues.Add(new("missing_observation", "Continuity observations are required.", "$.continuityNotes"));
        if (string.IsNullOrWhiteSpace(feedback.VisualConsistencyNotes))
            issues.Add(new("missing_observation", "Character consistency observations are required.", "$.visualConsistencyNotes"));
        if (string.IsNullOrWhiteSpace(feedback.LightingNotes))
            issues.Add(new("missing_observation", "Lighting observations are required.", "$.lightingNotes"));
        if (string.IsNullOrWhiteSpace(feedback.DialogueNotes))
            issues.Add(new("missing_observation", "Dialogue observations are required.", "$.dialogueNotes"));
        if (string.IsNullOrWhiteSpace(feedback.AudioNotes))
            issues.Add(new("missing_observation", "Audio observations are required.", "$.audioNotes"));
        return issues;
    }

    private static async Task SaveExecutionManifestAsync<TResult>(
        string projectDir, string operationName, ValidatedModelResult<TResult> execution, CancellationToken ct = default)
        where TResult : class
    {
        var dir = Path.Combine(projectDir, "artifacts", "model_operations");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, operationName + ".lifecycle.json"),
            ModelExecutionManifest.Serialize(execution), ct).ConfigureAwait(false);
    }

    private static string BuildFallbackExecutiveSummary(
        MovieAutoReviewReport report,
        IReadOnlyList<MovieSceneGroupFeedback> groupFeedbacks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Executive Director Report — {report.Verdict} (Overall Score: {report.OverallScore}/10)\n");
        sb.AppendLine("## Category Scores");
        foreach (var (cat, score) in report.CategoryScores)
        {
            var badge = score >= 8 ? "PASSED" : score >= 6 ? "POLISH" : "ACTION REQUIRED";
            sb.AppendLine($"- **{cat}**: {score}/10 [{badge}]");
        }
        sb.AppendLine("\n## Synthesis & Key Notes");
        var posNotes = groupFeedbacks.Select(g => g.ContinuityNotes).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(3);
        foreach (var note in posNotes)
        {
            sb.AppendLine($"- {note}");
        }
        if (report.FlaggedScenes.Count > 0)
        {
            sb.AppendLine($"\n**Recommended Priority Touch-ups**: Scene(s) {string.Join(", ", report.FlaggedScenes)}");
        }
        return sb.ToString().Trim();
    }
}
