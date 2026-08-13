using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Services;

/// <summary>
/// Cross-surface deep links so Script / Cast / Locs / Film share the same entity context.
/// </summary>
public static class StudioDeepLinks
{
    private const string CharacterKeyPrefix = "Character_";
    public static string Characters(string? charKeyOrName = null, int? returnScene = null, int? returnClip = null)
    {
        var path = "characters";
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(charKeyOrName))
            q.Add("char=" + Uri.EscapeDataString(charKeyOrName.Trim()));
        AppendReturn(q, returnScene, returnClip);
        return q.Count == 0 ? path : path + "?" + string.Join("&", q);
    }

    public static string Locations(string? locKeyOrName = null, int? returnScene = null, int? returnClip = null)
    {
        var path = "locations";
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(locKeyOrName))
            q.Add("loc=" + Uri.EscapeDataString(locKeyOrName.Trim()));
        AppendReturn(q, returnScene, returnClip);
        return q.Count == 0 ? path : path + "?" + string.Join("&", q);
    }

    public static string Scenes(int? sceneNumber = null, bool play = false, int? clip = null)
    {
        if (sceneNumber is null or <= 0) return "scenes";
        var q = $"scene={sceneNumber.Value}";
        if (play) q += "&play=1";
        if (clip is > 0) q += $"&clip={clip.Value}";
        return "scenes?" + q;
    }

    public static string Screenplay(int? sceneNumber = null, int? returnScene = null, int? returnClip = null)
    {
        var path = "adaptation/screenplay";
        var q = new List<string>();
        if (sceneNumber is > 0)
            q.Add($"scene={sceneNumber.Value}");
        // returnScene defaults to scene when editing from film
        var ret = returnScene ?? sceneNumber;
        AppendReturn(q, ret, returnClip);
        return q.Count == 0 ? path : path + "?" + string.Join("&", q);
    }

    /// <summary>Estimate DecisionCard (optional phase/focus).</summary>
    public static string Estimate(string? phase = null, string? focus = null)
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(phase))
            q.Add("phase=" + Uri.EscapeDataString(phase.Trim()));
        if (!string.IsNullOrWhiteSpace(focus))
            q.Add("focus=" + Uri.EscapeDataString(focus.Trim()));
        return q.Count == 0 ? "cost" : "cost?" + string.Join("&", q);
    }

    private static void AppendReturn(List<string> q, int? returnScene, int? returnClip)
    {
        if (returnScene is > 0)
            q.Add($"returnScene={returnScene.Value}");
        if (returnClip is > 0)
            q.Add($"returnClip={returnClip.Value}");
    }

    public static string? QueryValue(NavigationManager nav, string key)
    {
        try
        {
            var uri = nav.ToAbsoluteUri(nav.Uri);
            var q = QueryHelpers.ParseQuery(uri.Query);
            if (!q.TryGetValue(key, out var vals)) return null;
            var v = vals.FirstOrDefault();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch
        {
            return null;
        }
    }

    public static int? QueryInt(NavigationManager nav, string key)
    {
        var s = QueryValue(nav, key);
        return int.TryParse(s, out var n) && n > 0 ? n : null;
    }

    /// <summary>Film return target from ?returnScene=&returnClip=.</summary>
    public static string? FilmReturnHref(NavigationManager nav)
    {
        var scene = QueryInt(nav, "returnScene");
        if (scene is null) return null;
        var clip = QueryInt(nav, "returnClip");
        return Scenes(scene, play: false, clip: clip);
    }

    /// <summary>Match Cast list entry from ?char= key, DisplayName, or bare name.</summary>
    public static CharacterSummary? MatchCharacter(IEnumerable<CharacterSummary>? chars, string? query)
    {
        if (chars is null) return null;
        var list = chars as IReadOnlyList<CharacterSummary> ?? chars.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(query))
            return null;
        var q = query.Trim();

        var byKey = list.FirstOrDefault(c =>
            string.Equals(c.Key, q, StringComparison.OrdinalIgnoreCase));
        if (byKey is not null) return byKey;

        var byDisplay = list.FirstOrDefault(c =>
            string.Equals(c.DisplayName, q, StringComparison.OrdinalIgnoreCase));
        if (byDisplay is not null) return byDisplay;

        var bare = q;
        if (bare.StartsWith(CharacterKeyPrefix, StringComparison.OrdinalIgnoreCase))
            bare = bare[CharacterKeyPrefix.Length..];
        bare = bare.Replace('_', ' ').Trim();

        byDisplay = list.FirstOrDefault(c =>
            string.Equals(c.DisplayName, bare, StringComparison.OrdinalIgnoreCase)
            || string.Equals((c.DisplayName ?? "").Replace(" ", ""), bare.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (byDisplay is not null) return byDisplay;

        var wantKey = CharacterKeyPrefix + bare.Replace(' ', '_');
        return list.FirstOrDefault(c =>
            string.Equals(c.Key, wantKey, StringComparison.OrdinalIgnoreCase)
            || c.Key.EndsWith("_" + bare.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Match Locs list entry from ?loc= key, DisplayName, or place name.</summary>
    public static LocationSummary? MatchLocation(IEnumerable<LocationSummary>? locs, string? query)
    {
        if (locs is null) return null;
        var list = locs as IReadOnlyList<LocationSummary> ?? locs.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(query))
            return null;
        var q = query.Trim();

        var byKey = list.FirstOrDefault(l =>
            string.Equals(l.Key, q, StringComparison.OrdinalIgnoreCase));
        if (byKey is not null) return byKey;

        var byDisplay = list.FirstOrDefault(l =>
            string.Equals(l.DisplayName, q, StringComparison.OrdinalIgnoreCase));
        if (byDisplay is not null) return byDisplay;

        var bare = q;
        if (bare.StartsWith("Loc_", StringComparison.OrdinalIgnoreCase))
            bare = bare["Loc_".Length..];
        bare = bare.Replace('_', ' ').Trim();

        byDisplay = list.FirstOrDefault(l =>
            string.Equals(l.DisplayName, bare, StringComparison.OrdinalIgnoreCase)
            || (l.DisplayName ?? "").Contains(bare, StringComparison.OrdinalIgnoreCase)
            || bare.Contains(l.DisplayName ?? "", StringComparison.OrdinalIgnoreCase));
        if (byDisplay is not null) return byDisplay;

        var wantKey = "Loc_" + bare.Replace(' ', '_');
        return list.FirstOrDefault(l =>
            string.Equals(l.Key, wantKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(l.Key.Replace('_', ' '), bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Build Character_* style key guess from a screenplay speaker name.</summary>
    public static string CharacterQueryFromDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var t = name.Trim();
        if (t.StartsWith(CharacterKeyPrefix, StringComparison.OrdinalIgnoreCase))
            return t;
        return t; // Cast page matches DisplayName / fuzzy
    }

    public static string LocationQueryFromDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return name.Trim();
    }
}
