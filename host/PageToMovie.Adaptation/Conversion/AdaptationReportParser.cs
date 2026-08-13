using System.Text.Json;
using PageToMovie.Adaptation.Contracts;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Pure parse of model <c>---ADAPTATION_REPORT---</c> JSON into <see cref="AdaptationReport"/>.
/// Tolerant of single-line or pretty JSON; does not throw on bad input.
/// </summary>
public static class AdaptationReportParser
{
    public const string StartMark = "---ADAPTATION_REPORT---";
    public const string EndMark = "---END_ADAPTATION_REPORT---";

    public static AdaptationReport? ParseModelJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = StripCodeFence(raw.Trim());
        if (t.Length == 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var report = new AdaptationReport { RawJson = t };
            TryApplySourceComplete(root, report);
            TryApplyMetrics(root, report);
            TryApplyIssues(root, report);
            TryApplySpecFeedback(root, report);
            return report;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryApplySourceComplete(JsonElement root, AdaptationReport report)
    {
        if (root.TryGetProperty("source_complete", out var sc) && sc.ValueKind == JsonValueKind.String)
            report.SourceComplete = (sc.GetString() ?? "").Trim().ToLowerInvariant();
    }

    private static void TryApplyMetrics(JsonElement root, AdaptationReport report)
    {
        if (!root.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
            return;
        report.Metrics = new AdaptationReportMetrics
        {
            Scenes = GetInt(metrics, "scenes"),
            SpeakingCast = GetInt(metrics, "speaking_cast"),
            BodyWords = GetInt(metrics, "body_words"),
            EstRuntimeMin = GetDouble(metrics, "est_runtime_min"),
        };
    }

    private static void TryApplyIssues(JsonElement root, AdaptationReport report)
    {
        if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            return;
        foreach (var el in issues.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            report.Issues.Add(new AdaptationReportIssue
            {
                Type = GetString(el, "type"),
                Severity = GetString(el, "severity"),
                Where = GetString(el, "where"),
                Detail = GetString(el, "detail"),
                Resolution = GetString(el, "resolution"),
            });
        }
    }

    private static void TryApplySpecFeedback(JsonElement root, AdaptationReport report)
    {
        if (!root.TryGetProperty("spec_feedback", out var feedback) || feedback.ValueKind != JsonValueKind.Array)
            return;
        foreach (var el in feedback.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var s = (el.GetString() ?? "").Trim();
            if (s.Length > 0) report.SpecFeedback.Add(s);
        }
    }

    private static string StripCodeFence(string t)
    {
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var nl = t.IndexOf('\n');
        if (nl > 0) t = t[(nl + 1)..];
        var fence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (fence >= 0) t = t[..fence];
        return t.Trim();
    }

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? (p.GetString() ?? "").Trim()
            : "";

    private static int GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var j)) return j;
        return 0;
    }

    private static double GetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String &&
            double.TryParse(p.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var e))
            return e;
        return 0;
    }
}
