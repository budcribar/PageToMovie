namespace PageToMovie.Core.Models;

/// <summary>
/// Classifies cast seeds as individual, group/chorus, or voice-only (policy-driven).
/// Used by ProjectStore listing, Characters UI, and Adaptation cast package checks.
/// </summary>
public static class CastKindClassifier
{
    /// <summary>Stable tokens that represent plural/ensemble cast, not a single face.</summary>
    private static readonly HashSet<string> GroupTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHILDREN", "CHILD", "KIDS", "PUPILS", "STUDENTS", "CLASS", "CLASSMATES",
        "CROWD", "MOB", "GROUP", "CHORUS", "ENSEMBLE", "VILLAGERS", "TOWNSFOLK",
        "PEOPLE", "ONLOOKERS", "SPECTATORS", "SOLDIERS", "GUARDS", "SAILORS",
        "GUESTS", "PARTYGOERS", "WORKERS", "TOWNSPEOPLE", "BYSTANDERS",
    };

    /// <summary>
    /// True when this seed is a plural/ensemble cast member (not a single portrait identity).
    /// Explicit <c>cast_kind</c> of group/chorus/ensemble wins; otherwise key/display heuristics.
    /// </summary>
    public static bool IsGroup(
        string? key,
        string? displayName = null,
        string? castKind = null,
        string? description = null)
    {
        if (!string.IsNullOrWhiteSpace(castKind))
        {
            var k = castKind.Trim();
            if (k.Equals("group", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("chorus", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("ensemble", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("crowd", StringComparison.OrdinalIgnoreCase))
                return true;
            if (k.Equals("individual", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("character", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("person", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (TokenIsGroup(key)) return true;
        if (TokenIsGroup(displayName)) return true;

        // Weak prose signal from cast extract descriptions
        var desc = description ?? "";
        if (desc.Length >= 12 &&
            (desc.Contains("group of", StringComparison.OrdinalIgnoreCase) ||
             desc.Contains("several", StringComparison.OrdinalIgnoreCase) ||
             desc.Contains("mixed boys and girls", StringComparison.OrdinalIgnoreCase) ||
             desc.Contains("small group", StringComparison.OrdinalIgnoreCase)) &&
            (TokenIsGroup(key) || TokenIsGroup(displayName) || LooksPluralToken(key) || LooksPluralToken(displayName)))
        {
            return true;
        }

        return false;
    }

    public static bool IsVoiceOnlyPolicy(string? displayNamePolicy) =>
        !string.IsNullOrWhiteSpace(displayNamePolicy) &&
        displayNamePolicy.Contains("never", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the canonical character prefix ("Character_") from a raw key if present.
    /// </summary>
    public static string StripPrefix(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return "";
        var k = rawKey.Trim();
        return k.StartsWith(PageToMovie.Core.Utils.JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase)
            ? k[PageToMovie.Core.Utils.JsonKeys.CharacterPrefix.Length..]
            : k;
    }

    /// <summary>
    /// True when <paramref name="speaker"/> represents the narrator role/character.
    /// </summary>
    public static bool IsNarratorSpeaker(string? speaker, string? narratorKey = null)
    {
        if (string.IsNullOrWhiteSpace(speaker)) return false;
        if (!string.IsNullOrWhiteSpace(narratorKey) &&
            string.Equals(speaker.Trim(), narratorKey.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        return speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalize Character_Foo → FOO for token checks.</summary>
    public static string NormalizeToken(string? raw)
    {
        var t = StripPrefix(raw);
        t = t.Replace('_', ' ').Trim();
        return t.ToUpperInvariant();
    }

    /// <summary>
    /// True when two keys name the same character: <c>Character_Teacher</c>, <c>Teacher</c>,
    /// and <c>TEACHER</c> all match. Used so Easy Start / voice capture honor a user pick even
    /// when the shot plan speaker spelling differs from the cast list key.
    /// </summary>
    public static bool SameCharacter(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var na = NormalizeToken(a);
        var nb = NormalizeToken(b);
        return na.Length > 0 && string.Equals(na, nb, StringComparison.Ordinal);
    }

    private static bool TokenIsGroup(string? raw)
    {
        var t = NormalizeToken(raw);
        if (string.IsNullOrWhiteSpace(t)) return false;
        if (GroupTokens.Contains(t)) return true;
        // Multi-word: any segment matches
        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(GroupTokens.Contains);
    }

    private static bool LooksPluralToken(string? raw)
    {
        var t = NormalizeToken(raw);
        if (t.Length < 4) return false;
        return t.EndsWith("S", StringComparison.Ordinal) &&
               !t.EndsWith("SS", StringComparison.Ordinal) &&
               !t.EndsWith("US", StringComparison.Ordinal) &&
               !t.EndsWith("IS", StringComparison.Ordinal);
    }
}
