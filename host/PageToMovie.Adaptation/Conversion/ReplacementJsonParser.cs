using System.Text.Json;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Best-effort parse of model JSON arrays of <c>{ from, to }</c> replacement pairs.
/// Malformed or unexpected payloads yield an empty list (same as the prior inline parsers).
/// </summary>
internal static class ReplacementJsonParser
{
    public static List<T> ParsePairs<T>(string? raw, Func<string, string, T> create)
    {
        var list = new List<T>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;

        try
        {
            using var doc = JsonDocument.Parse(BookToFountainConverter.StripFences(raw));
            AddPairs(list, doc.RootElement, create);
        }
        catch (Exception)
        {
            // Intentionally ignored: model output may be non-JSON or an unexpected shape.
            // Callers treat that as "no replacements" rather than failing the repair pass.
        }

        return list;
    }

    private static void AddPairs<T>(List<T> list, JsonElement root, Func<string, string, T> create)
    {
        if (!TryGetReplacementArray(root, out var arr))
            return;

        foreach (var el in arr.EnumerateArray())
        {
            if (!TryReadPair(el, out var from, out var to))
                continue;
            list.Add(create(from, to));
        }
    }

    private static bool TryGetReplacementArray(JsonElement root, out JsonElement arr)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            arr = root;
            return true;
        }

        if (root.TryGetProperty("replacements", out var reps) && reps.ValueKind == JsonValueKind.Array)
        {
            arr = reps;
            return true;
        }

        arr = default;
        return false;
    }

    private static bool TryReadPair(JsonElement el, out string from, out string to)
    {
        from = el.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
        to = el.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(from)
            || string.IsNullOrWhiteSpace(to)
            || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            from = "";
            to = "";
            return false;
        }

        from = from.Trim();
        to = to.Trim();
        return true;
    }
}
