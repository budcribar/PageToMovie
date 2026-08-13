using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Utils;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>Parse and gate <c>screenplay.index.v1</c>. No scene-count maximum.</summary>
public static class ScreenplayIndexParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static bool TryParse(string? raw, out ScreenplayIndex? index, out string error)
    {
        index = null;
        error = "";
        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Index response was empty or not JSON.";
            return false;
        }

        try
        {
            index = JsonSerializer.Deserialize<ScreenplayIndex>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            error = "Index JSON is invalid: " + ex.Message;
            return false;
        }

        if (index is null)
        {
            error = "Index JSON deserialized empty.";
            return false;
        }

        return true;
    }

    public static ScreenplayIndexGate Evaluate(ScreenplayIndex? index, string? bookText = null)
    {
        var fails = new List<string>();
        var warns = new List<string>();
        if (index is null)
            return new ScreenplayIndexGate { Ok = false, Failures = ["empty_index"] };

        if (index.Acts.Count == 0)
            fails.Add("no_acts");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cards = EnumerateCards(index).ToList();
        if (cards.Count == 0)
            fails.Add("no_scene_cards");

        foreach (var act in index.Acts)
            RequireId(act.Id, "act", ids, fails);
        foreach (var seq in index.Acts.SelectMany(a => a.Sequences))
            RequireId(seq.Id, "sequence", ids, fails);

        foreach (var card in cards)
        {
            RequireId(card.Id, "scene", ids, fails);
            if (string.IsNullOrWhiteSpace(card.Heading))
                fails.Add($"missing_heading:{card.Id}");
            if (string.IsNullOrWhiteSpace(card.Beat))
                fails.Add($"missing_beat:{card.Id}");
            if (string.IsNullOrWhiteSpace(card.BookAnchorStart))
                fails.Add($"missing_anchor_start:{card.Id}");
            if (string.IsNullOrWhiteSpace(card.BookAnchorEnd))
                fails.Add($"missing_anchor_end:{card.Id}");
        }

        if (cards.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(cards[0].BookAnchorStart))
                fails.Add("source_incomplete:first_anchor");
            if (string.IsNullOrWhiteSpace(cards[^1].BookAnchorEnd))
                fails.Add("source_incomplete:last_anchor");
        }

        AddCollapseWarnings(warns, cards.Count, bookText);
        index.Warnings = warns;

        return new ScreenplayIndexGate
        {
            Ok = fails.Count == 0,
            Failures = fails,
            Warnings = warns,
        };
    }

    public static ScreenplayIndexRollup Rollup(ScreenplayIndex? index)
    {
        if (index is null)
            return new ScreenplayIndexRollup();

        var cards = EnumerateCards(index).ToList();
        var locs = cards
            .Select(c => (c.LocationKey ?? "").Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var cast = cards
            .SelectMany(c => c.SpeakingCast ?? Enumerable.Empty<string>())
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var minutes = cards.Sum(c => c.ApproxMinutes ?? 0);
        return new ScreenplayIndexRollup
        {
            Acts = index.Acts.Count,
            Sequences = index.Acts.Sum(a => a.Sequences.Count),
            SceneCards = cards.Count,
            Locations = locs,
            SpeakingCast = cast,
            ApproxMinutes = minutes,
            Warnings = index.Warnings,
        };
    }

    public static IEnumerable<ScreenplayIndexCard> EnumerateCards(ScreenplayIndex index) =>
        index.Acts.SelectMany(a => a.Sequences).SelectMany(s => s.Scenes);

    public static int CountChapterLikeMarkers(string? bookText)
    {
        if (string.IsNullOrWhiteSpace(bookText)) return 0;
        var n = 0;
        foreach (var raw in bookText.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length < 5 || t.Length > 80) continue;
            if (CommonRegex.IsMatch(t, @"^(CHAPTER|BOOK|CANTO|PART)\b", RegexOptions.IgnoreCase))
                n++;
        }
        return n;
    }

    internal static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t[(nl + 1)..];
            var fence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) t = t[..fence];
            t = t.Trim();
        }
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return t[start..(end + 1)];
    }

    private static void RequireId(string? id, string kind, HashSet<string> ids, List<string> fails)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0)
        {
            fails.Add($"missing_{kind}_id");
            return;
        }
        if (!ids.Add(key))
            fails.Add($"duplicate_id:{key}");
    }

    private static void AddCollapseWarnings(List<string> warns, int cardCount, string? bookText)
    {
        if (cardCount <= 1)
            warns.Add("possible_collapse:only_one_card");
        var chapters = CountChapterLikeMarkers(bookText);
        if (chapters >= 8 && cardCount < Math.Max(3, chapters / 2))
            warns.Add($"possible_collapse:{cardCount}_cards_vs_{chapters}_chapters");
    }
}
