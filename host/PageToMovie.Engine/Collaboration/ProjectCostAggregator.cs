using System.Text.Json;

namespace PageToMovie.Engine;

/// <summary>
/// Builds a cost summary for a project: always splits <see cref="CostSummaryDto.AdaptationEstimateUsd"/>
/// vs <see cref="CostSummaryDto.VideoEstimateUsd"/> (and matching actuals when a ledger is present).
/// </summary>
public static class ProjectCostAggregator
{
    // Conservative planning rates — catalog-backed pricing can replace these later.
    private const double AdaptationUsdPerScene = 0.15;
    private const double VideoUsdPerClip = 0.40;
    private const double AudioUsdPerClip = 0.05;

    public static async Task<CostSummaryDto> BuildSummaryAsync(
        string projectId,
        string projectsRoot,
        CostLedgerService? ledger = null,
        CancellationToken ct = default)
    {
        var summary = new CostSummaryDto { ProjectId = projectId };
        var counts = await CountProjectMediaAsync(projectId, projectsRoot, ct).ConfigureAwait(false);
        ApplyEstimates(summary, counts);
        ApplyActuals(summary, projectId, ledger);
        ApplyLines(summary);
        return summary;
    }

    private sealed class MediaCounts
    {
        public int SceneCount;
        public int ClipCount;
        public int ClipsWithVideo;
        public int ClipsWithAudio;
    }

    private static async Task<MediaCounts> CountProjectMediaAsync(
        string projectId, string projectsRoot, CancellationToken ct)
    {
        var counts = new MediaCounts();
        var projectJsonPath = Path.Combine(projectsRoot, projectId, "project.json");
        if (!File.Exists(projectJsonPath)) return counts;
        try
        {
            var json = await File.ReadAllTextAsync(projectJsonPath, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            CountScenes(doc.RootElement, counts);
        }
        catch
        {
            // leave zeros
        }
        return counts;
    }

    private static void CountScenes(JsonElement root, MediaCounts counts)
    {
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
            return;
        counts.SceneCount = scenes.GetArrayLength();
        foreach (var scene in scenes.EnumerateArray())
            CountSceneClips(scene, counts);
    }

    private static void CountSceneClips(JsonElement scene, MediaCounts counts)
    {
        if (!scene.TryGetProperty("clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
        {
            // Scene without explicit clips — treat as 1 planned clip
            counts.ClipCount++;
            return;
        }

        counts.ClipCount += clips.GetArrayLength();
        foreach (var clip in clips.EnumerateArray())
        {
            if (HasMedia(clip, "videoUrl", "video", "hasVideo"))
                counts.ClipsWithVideo++;
            if (HasMedia(clip, "audioUrl", "audio", "hasAudio"))
                counts.ClipsWithAudio++;
        }
    }

    private static void ApplyEstimates(CostSummaryDto summary, MediaCounts counts)
    {
        // Always populate both estimate line items (even when $0) — this is the cost split contract.
        summary.ScenesTotal = counts.SceneCount;
        summary.ClipsTotal = Math.Max(counts.ClipCount, counts.SceneCount);
        summary.ClipsWithVideo = counts.ClipsWithVideo;
        summary.ClipsWithAudio = counts.ClipsWithAudio;
        summary.AdaptationEstimateUsd = Round2(counts.SceneCount * AdaptationUsdPerScene);
        summary.VideoEstimateUsd = Round2(summary.ClipsTotal * VideoUsdPerClip + summary.ClipsTotal * AudioUsdPerClip);
        summary.TotalEstimateUsd = Round2(summary.AdaptationEstimateUsd + summary.VideoEstimateUsd);
    }

    private static void ApplyActuals(CostSummaryDto summary, string projectId, CostLedgerService? ledger)
    {
        if (ledger is null)
        {
            summary.AdaptationActualUsd = 0;
            summary.VideoActualUsd = 0;
            summary.TotalActualUsd = 0;
            summary.LedgerEntries = 0;
            return;
        }

        var actual = ledger.GetProjectActual(projectId);
        summary.AdaptationActualUsd = Round2(actual.AdaptationUsd);
        summary.VideoActualUsd = Round2(actual.VideoUsd);
        summary.TotalActualUsd = Round2(actual.AdaptationUsd + actual.VideoUsd);
        summary.LedgerEntries = actual.EntryCount;
    }

    private static void ApplyLines(CostSummaryDto summary)
    {
        summary.EstimateLines =
        [
            new CostLineDto { Category = "adaptation", Label = "Adaptation (LLM)", Usd = summary.AdaptationEstimateUsd },
            new CostLineDto { Category = "video", Label = "Video + audio generation", Usd = summary.VideoEstimateUsd },
        ];
        summary.ActualLines =
        [
            new CostLineDto { Category = "adaptation", Label = "Adaptation (LLM)", Usd = summary.AdaptationActualUsd },
            new CostLineDto { Category = "video", Label = "Video + audio generation", Usd = summary.VideoActualUsd },
        ];
    }

    private static bool HasMedia(JsonElement clip, params string[] props)
    {
        foreach (var p in props)
        {
            if (!clip.TryGetProperty(p, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return true;
            if (v.ValueKind == JsonValueKind.True)
                return true;
        }
        return false;
    }

    private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

public sealed class CostSummaryDto
{
    public string ProjectId { get; set; } = "";
    public int ScenesTotal { get; set; }
    public int ClipsTotal { get; set; }
    public int ClipsWithVideo { get; set; }
    public int ClipsWithAudio { get; set; }

    /// <summary>Always set (may be 0).</summary>
    public double AdaptationEstimateUsd { get; set; }
    /// <summary>Always set (may be 0).</summary>
    public double VideoEstimateUsd { get; set; }
    public double TotalEstimateUsd { get; set; }

    public double AdaptationActualUsd { get; set; }
    public double VideoActualUsd { get; set; }
    public double TotalActualUsd { get; set; }
    public int LedgerEntries { get; set; }

    public List<CostLineDto> EstimateLines { get; set; } = new();
    public List<CostLineDto> ActualLines { get; set; } = new();
}

public sealed class CostLineDto
{
    public string Category { get; set; } = "";
    public string Label { get; set; } = "";
    public double Usd { get; set; }
}

/// <summary>
/// Append-only per-project cost ledger (JSONL under project dir).
/// Categories: "adaptation" | "video" (everything else rolls into video).
/// </summary>
public sealed class CostLedgerService
{
    private readonly string _projectsRoot;

    public CostLedgerService(string projectsRoot)
    {
        _projectsRoot = projectsRoot ?? throw new ArgumentNullException(nameof(projectsRoot));
    }

    public void Record(string projectId, string category, double usd, string? note = null, string? modelId = null)
    {
        var dir = Path.Combine(_projectsRoot, projectId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cost-ledger.jsonl");
        var entry = JsonSerializer.Serialize(new
        {
            ts = DateTime.UtcNow,
            category = NormalizeCategory(category),
            usd,
            note,
            modelId
        });
        File.AppendAllText(path, entry + "\n");
    }

    public CostLedgerActual GetProjectActual(string projectId)
    {
        var path = Path.Combine(_projectsRoot, projectId, "cost-ledger.jsonl");
        var result = new CostLedgerActual();
        if (!File.Exists(path)) return result;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var usd = root.TryGetProperty("usd", out var u) && u.ValueKind == JsonValueKind.Number
                    ? u.GetDouble() : 0;
                var cat = root.TryGetProperty("category", out var c) ? c.GetString() : "video";
                cat = NormalizeCategory(cat);
                if (cat == "adaptation") result.AdaptationUsd += usd;
                else result.VideoUsd += usd;
                result.EntryCount++;
            }
            catch
            {
                // skip bad lines
            }
        }
        return result;
    }

    private static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "video";
        var c = category.Trim().ToLowerInvariant();
        if (c is "adaptation" or "llm" or "stage1" or "fountain" or "script")
            return "adaptation";
        return "video";
    }
}

public sealed class CostLedgerActual
{
    public double AdaptationUsd { get; set; }
    public double VideoUsd { get; set; }
    public int EntryCount { get; set; }
}
