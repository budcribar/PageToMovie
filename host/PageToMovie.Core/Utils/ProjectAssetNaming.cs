namespace PageToMovie.Core.Utils;

/// <summary>
/// Single source of truth for asset file naming conventions and candidate matching
/// across PageToMovie.Core, Engine, and Web.
/// </summary>
public static class ProjectAssetNaming
{
    public const string RefPngSuffix = "_ref.png";
    public const string LocationPrefix = "Loc_";
    public const string CharacterPrefix = "Character_";

    /// <summary>Canonical locked set plate filename: <c>{loc_key_lower}_ref.png</c>.</summary>
    public static string LocationRefFileName(string locKey)
    {
        var k = (locKey ?? "").Trim().Replace(' ', '_').Replace('\\', '/');
        k = Path.GetFileName(k).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(k) || k is "." or "..")
            k = "unknown_location";
        if (k.EndsWith(RefPngSuffix, StringComparison.OrdinalIgnoreCase))
            return k;
        return $"{k}_ref.png";
    }

    /// <summary>Canonical locked character reference filename: <c>{char_key_lower}_ref.png</c>.</summary>
    public static string CharacterRefFileName(string charKey)
    {
        var k = (charKey ?? "").Trim().Replace(' ', '_').Replace('\\', '/');
        k = Path.GetFileName(k).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(k) || k is "." or "..")
            k = "unknown_character";
        if (k.EndsWith(RefPngSuffix, StringComparison.OrdinalIgnoreCase))
            return k;
        return $"{k}_ref.png";
    }

    /// <summary>
    /// Candidate filenames for a locked location plate (canonical + Loc_ aliases + extensions).
    /// </summary>
    public static IEnumerable<string> LocationRefFileNameCandidates(string locKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileName(name.Trim().Replace(' ', '_')).ToLowerInvariant();
            if (!name.EndsWith(RefPngSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.EndsWith("_ref", StringComparison.OrdinalIgnoreCase) ? name + ".png" : name + "_ref.png";
            if (seen.Add(name))
                list.Add(name);
        }

        Add(LocationRefFileName(locKey));
        var raw = (locKey ?? "").Trim();
        if (raw.StartsWith(LocationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bare = raw[LocationPrefix.Length..];
            Add(LocationRefFileName(bare));
            Add(bare + "_ref.png");
        }
        else
        {
            Add(LocationRefFileName(LocationPrefix + raw));
        }
        return list;
    }

    /// <summary>
    /// Candidate filenames for a locked character reference (canonical + short aliases).
    /// </summary>
    public static IEnumerable<string> CharacterRefFileCandidates(string charKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileName(name.Trim().Replace(' ', '_')).ToLowerInvariant();
            if (!name.EndsWith(RefPngSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.EndsWith("_ref", StringComparison.OrdinalIgnoreCase) ? name + ".png" : name + "_ref.png";
            if (seen.Add(name))
                list.Add(name);
        }

        Add(CharacterRefFileName(charKey));
        var raw = (charKey ?? "").Trim();
        if (raw.StartsWith(CharacterPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bare = raw[CharacterPrefix.Length..];
            Add(CharacterRefFileName(bare));
            Add(bare + "_ref.png");
        }
        else
        {
            Add(CharacterRefFileName(CharacterPrefix + raw));
        }
        return list;
    }
}
