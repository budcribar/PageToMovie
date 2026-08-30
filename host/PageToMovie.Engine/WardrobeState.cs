using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// One wardrobe list per character: seed <c>wardrobe_always</c>, scene sticky, beat
/// put_on/remove, then classifier layers merged in. Classifier attire is a delta, never a
/// replacement list.
/// </summary>
internal static class WardrobeState
{
    /// <summary>
    /// Identity garments for one character: seed <c>wardrobe_always</c> then scene
    /// <c>wardrobe_by_character</c>. Same order <see cref="Stage2PlannerService"/> starts from.
    /// </summary>
    public static List<string> IdentityItems(
        string key,
        Dictionary<string, object?>? charSeeds,
        Dictionary<string, object?>? scene)
    {
        var items = new List<string>();
        if (charSeeds is not null &&
            charSeeds.TryGetValue(key, out var s) &&
            s is Dictionary<string, object?> seed)
        {
            items.AddRange(Stage1Normalizer.CoerceStringList(
                seed.TryGetValue("wardrobe_always", out var wa) ? wa : null));
        }

        if (scene is not null &&
            scene.TryGetValue("wardrobe_by_character", out var wbc) &&
            wbc is Dictionary<string, object?> map &&
            map.TryGetValue(key, out var itemsObj))
        {
            items.AddRange(Stage1Normalizer.CoerceStringList(itemsObj));
        }

        return Stage2PlannerService.PrioritizeWardrobeItems(items).ToList();
    }

    /// <summary>
    /// Union / prepend classifier attire onto the identity list. Never replaces the list.
    /// </summary>
    public static void MergeOverrides(
        Dictionary<string, List<string>> wardrobe,
        Dictionary<string, string>? aiWardrobe)
    {
        if (aiWardrobe is null || aiWardrobe.Count == 0)
            return;
        foreach (var (k, v) in aiWardrobe)
        {
            if (string.IsNullOrWhiteSpace(v))
                continue;
            if (!wardrobe.TryGetValue(k, out var list) || list is null)
            {
                list = new List<string>();
                wardrobe[k] = list;
            }

            PrependLayers(list, SplitAttire(v));
            wardrobe[k] = Stage2PlannerService.PrioritizeWardrobeItems(list).ToList();
        }
    }

    /// <summary>Newest layers first; skip anything already on the list (contains match).</summary>
    public static void PrependLayers(List<string> list, IEnumerable<string> additions)
    {
        var incoming = additions
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
        for (var i = incoming.Count - 1; i >= 0; i--)
        {
            var p = incoming[i];
            if (AlreadyHas(list, p))
                continue;
            list.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, p);
        }
    }

    public static void RemoveItems(List<string> list, IEnumerable<string> removals)
    {
        foreach (var r in removals)
        {
            if (string.IsNullOrWhiteSpace(r))
                continue;
            list.RemoveAll(x =>
                x.Contains(r, StringComparison.OrdinalIgnoreCase) ||
                r.Contains(x, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static bool AlreadyHas(IReadOnlyList<string> list, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return false;
        return list.Any(x =>
            x.Equals(item, StringComparison.OrdinalIgnoreCase) ||
            x.Contains(item, StringComparison.OrdinalIgnoreCase) ||
            item.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Split a classifier attire string the same way Stage 1 coerces wardrobe lists.</summary>
    public static List<string> SplitAttire(string? attire) =>
        Stage1Normalizer.CoerceStringList(attire);

    /// <summary>
    /// Parse <c>{key} still wears a, b; {key2} still wears c</c> (optionally inside a
    /// <c>&lt;Wardrobe&gt;</c> tag) into per-character item lists.
    /// </summary>
    public static Dictionary<string, List<string>> ParseStillWears(string? text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var body = text;
        var tagged = CommonRegex.Match(
            text,
            @"<Wardrobe>(.*?)</Wardrobe>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (tagged.Success)
            body = tagged.Groups[1].Value;

        foreach (var groups in CommonRegex.Matches(
                     body,
                     @"(Character_[A-Za-z0-9_]+)\s+still wears\s+([^;]+)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                     .Select(m => m.Groups))
        {
            var key = groups[1].Value;
            var items = SplitAttire(groups[2].Value);
            if (items.Count == 0)
                continue;
            if (!result.TryGetValue(key, out var list))
            {
                list = new List<string>();
                result[key] = list;
            }

            foreach (var item in items.Where(item => !AlreadyHas(list, item)))
                list.Add(item);
        }

        return result;
    }
}
