using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Vision ranks generated portrait / set-plate variants and returns the best 1-based index.
/// Falls back to the first existing variant when vision is unavailable or parsing fails.
/// </summary>
public static class LookVariantPicker
{
    public static async Task<int> PickBestIndexAsync(
        IVisionClient vision,
        ILogger log,
        string subjectKind,
        string subjectKey,
        string description,
        string visualLock,
        IReadOnlyList<(int Index, string Path)> variants,
        CancellationToken ct = default)
    {
        if (variants.Count == 0)
            throw new InvalidOperationException($"No variants to pick for {subjectKey}");
        if (variants.Count == 1)
            return variants[0].Index;

        if (!vision.IsConfigured)
        {
            log.LogInformation("Look pick: vision not configured — using first variant for {Key}", subjectKey);
            return variants[0].Index;
        }

        var paths = variants.Select(v => v.Path).ToList();
        var labels = string.Join(", ", variants.Select(v => $"#{v.Index}"));
        var prompt =
            "You are choosing the best " + subjectKind + " reference image for a film production.\n" +
            "Subject key: " + subjectKey + "\n" +
            "Description: " + Trunc(description, 400) + "\n" +
            "Visual lock: " + Trunc(visualLock, 300) + "\n\n" +
            "Images are labeled in order as: " + labels + "\n" +
            "(Image 1 in the request = #" + variants[0].Index + ", etc.)\n\n" +
            "Pick the single image that best matches the description and visual lock:\n" +
            "- clear identity / place readability\n" +
            "- consistent era and medium (photoreal live-action unless described otherwise)\n" +
            "- usable as a locked reference plate (face for characters; architecture/set for locations)\n" +
            "- avoid text overlays, watermarks, extra limbs, extreme crop\n\n" +
            "Return JSON only:\n" +
            "{\"best\":1,\"reason\":\"short reason\"}\n" +
            "where best is the 1-based position in this request (1.." + variants.Count +
            "), not the variant filename number.\n";

        try
        {
            var raw = await vision.CompleteWithImagesAsync(prompt, paths, model: "", detail: "low", ct)
                .ConfigureAwait(false);
            var pos = ParseBestPosition(raw, variants.Count);
            if (pos is >= 1 and int p && p <= variants.Count)
            {
                var chosen = variants[p - 1].Index;
                log.LogInformation("Look pick: {Key} → variant {Idx} (pos {Pos}): {Raw}",
                    subjectKey, chosen, p, Trunc(raw, 120));
                return chosen;
            }
            log.LogWarning("Look pick: unparsed vision response for {Key}: {Raw}", subjectKey, Trunc(raw, 200));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Look pick failed for {Key} — using first variant", subjectKey);
        }

        return variants[0].Index;
    }

    public static int? ParseBestPosition(string raw, int count)
    {
        if (string.IsNullOrWhiteSpace(raw) || count < 1) return null;
        raw = StripCodeFences(raw.Trim());
        if (TryReadPositionFromJson(raw, count, out var fromJson))
            return fromJson;
        return TryRegexBest(raw, count);
    }

    private static string StripCodeFences(string raw)
    {
        if (!raw.StartsWith("```")) return raw;
        var i = raw.IndexOf('\n');
        if (i > 0) raw = raw[(i + 1)..];
        var end = raw.LastIndexOf("```", StringComparison.Ordinal);
        if (end > 0) raw = raw[..end];
        return raw.Trim();
    }

    /// <summary>
    /// True when JSON yielded a conclusive <c>best</c>/<c>index</c> answer (including out-of-range → null).
    /// False means fall through to the regex.
    /// </summary>
    private static bool TryReadPositionFromJson(string raw, int count, out int? position)
    {
        position = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (TryParseBestProperty(root, count, out position))
                return true;
            if (root.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i2))
            {
                position = InRange(i2, count);
                return true;
            }
        }
        catch { /* fall through to regex */ }
        return false;
    }

    private static bool TryParseBestProperty(JsonElement root, int count, out int? position)
    {
        position = null;
        if (!root.TryGetProperty("best", out var b)) return false;
        if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var n))
        {
            position = InRange(n, count);
            return true;
        }
        if (b.ValueKind == JsonValueKind.String && int.TryParse(b.GetString(), out n))
        {
            position = InRange(n, count);
            return true;
        }
        return false;
    }

    private static int? InRange(int n, int count) => n is >= 1 && n <= count ? n : null;

    private static int? TryRegexBest(string raw, int count)
    {
        var m = CommonRegex.Match(raw, @"""best""\s*:\s*(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var g))
            return InRange(g, count);
        return null;
    }

    private static string Trunc(string? s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        return s[..max] + "…";
    }
}
