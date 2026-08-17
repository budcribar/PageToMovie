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
        var isCharacter = !string.Equals(subjectKind, "location", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(subjectKind, "place", StringComparison.OrdinalIgnoreCase);

        var prompt =
            $"You are an expert production designer choosing the single best {subjectKind} master reference image for a film production.\n\n" +
            $"Subject Key: {subjectKey}\n" +
            $"Description: {Trunc(description, 400)}\n" +
            $"Visual Lock: {Trunc(visualLock, 300)}\n\n" +
            $"Images in order: {labels}\n" +
            $"(Image 1 in this request = #{variants[0].Index}, Image 2 = #{variants.ElementAtOrDefault(1).Index}, etc.)\n\n" +
            "Evaluate the candidates using these strict quality and fidelity weights:\n" +
            (isCharacter
                ? "1. ANATOMY & PHYSIQUE (Weight: 40%): Zero anatomical defects. Reject extra/missing limbs, malformed hands/fingers, melted or asymmetrical eyes, distorted facial structure, or floating artifacts.\n" +
                  "2. FRAMING & COMPOSITION (Weight: 30%): Clean, well-centered portrait (head-and-shoulders / chest-up). Clear filmable lighting and direct or three-quarter gaze. Reject extreme close-up chops that cut off the head, or distant/tiny framing.\n" +
                  "3. FIDELITY & TRAITS (Weight: 20%): Accurate depiction of described species (human vs animal), approximate age, hair/fur color, facial hair, and distinctive visual lock traits.\n" +
                  "4. MEDIUM & PRODUCTION VALUE (Weight: 10%): Coherent visual medium (photoreal live-action vs stylized per prompt). Clean backdrop/setting without blur or visual noise.\n"
                : "1. ARCHITECTURAL & SET COHERENCE (Weight: 40%): Structurally solid, believable geometry and realistic architectural features matching the setting description. Reject nonsensical layouts or floating structures.\n" +
                  "2. FRAMING & ESTABLISHING VIEW (Weight: 30%): Filmable establishing angle that clearly communicates the space/room layout with good depth and cinematic lighting. Reject extreme zoom-in on blank walls or unintelligible clutter.\n" +
                  "3. FIDELITY & TRAITS (Weight: 20%): Faithful to key location features, time period, and atmospheric elements in the visual lock.\n" +
                  "4. MEDIUM & PRODUCTION VALUE (Weight: 10%): Coherent visual medium without rendering artifacts or visual noise.\n") +
            "\nHARD DISQUALIFICATION RULES: Immediately disqualify any image with visible text/watermarks/numbers, severe anatomy distortion, or wrong species.\n\n" +
            "Return JSON only:\n" +
            "{\"best\": 1, \"reason\": \"short concise reason highlighting anatomy, framing, and fidelity\"}\n" +
            $"where best is the 1-based position in this request (1..{variants.Count}), NOT the variant filename number.\n";

        try
        {
            var raw = await vision.CompleteWithImagesAsync(prompt, paths, model: "", detail: "low", temperature: 0.0, ct: ct)
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
