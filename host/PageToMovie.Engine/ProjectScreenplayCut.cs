using System.Text.Json;
using PageToMovie.Adaptation.Conversion;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>Persist the last index cut (working view). Does not replace the max master.</summary>
public static class ProjectScreenplayCut
{
    public const string RelativePath = "source/screenplay.cut.json";

    public static string GetPath(string projectDir) => Path.Combine(projectDir, RelativePath);

    public static async Task WriteAsync(
        string projectDir, ScreenplayIndexCutter.CutPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var path = GetPath(projectDir);
        if (Path.GetDirectoryName(path) is { } dir)
            Directory.CreateDirectory(dir);
        var payload = new
        {
            schema_version = "screenplay.cut.v1",
            keep_all = plan.KeepAll,
            target_minutes = plan.TargetMinutes,
            total_minutes = plan.TotalMinutes,
            kept_minutes = plan.KeptMinutes,
            kept_sequences = plan.KeptSequenceIds,
            dropped_sequences = plan.DroppedSequenceIds,
            kept_cards = plan.KeptCards.Count,
            reason = plan.Reason,
        };
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Indented);
        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
    }

    public static ScreenplayCutSummary? TryReadSummary(string projectDir)
    {
        var path = GetPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var kept = root.TryGetProperty("kept_sequences", out var ks) && ks.ValueKind == JsonValueKind.Array
                ? ks.GetArrayLength() : 0;
            var dropped = root.TryGetProperty("dropped_sequences", out var ds) && ds.ValueKind == JsonValueKind.Array
                ? ds.GetArrayLength() : 0;
            return new ScreenplayCutSummary
            {
                HasCut = true,
                KeepAll = root.TryGetProperty("keep_all", out var ka) && ka.ValueKind == JsonValueKind.True,
                KeptSequences = kept,
                TotalSequences = kept + dropped,
                KeptCards = root.TryGetProperty("kept_cards", out var kc) && kc.TryGetInt32(out var n) ? n : 0,
                TargetMinutes = root.TryGetProperty("target_minutes", out var tm) && tm.TryGetInt32(out var t) ? t : 0,
            };
        }
        catch
        {
            return null;
        }
    }
}
