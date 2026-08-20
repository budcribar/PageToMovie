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
    public static string LocationRefFileName(string locKey) =>
        RefFileName(locKey, "unknown_location");

    /// <summary>Canonical locked character reference filename: <c>{char_key_lower}_ref.png</c>.</summary>
    public static string CharacterRefFileName(string charKey) =>
        RefFileName(charKey, "unknown_character");

    private static string RefFileName(string? key, string unknownFallback)
    {
        var k = (key ?? "").Trim().Replace(' ', '_').Replace('\\', '/');
        k = Path.GetFileName(k).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(k) || k is "." or "..")
            k = unknownFallback;
        if (k.EndsWith(RefPngSuffix, StringComparison.OrdinalIgnoreCase))
            return k;
        return k + RefPngSuffix;
    }

    /// <summary>
    /// Candidate filenames for a locked location plate (canonical + Loc_ aliases + extensions).
    /// </summary>
    public static IEnumerable<string> LocationRefFileNameCandidates(string locKey) =>
        RefFileCandidates(locKey, LocationPrefix, LocationRefFileName);

    /// <summary>
    /// Candidate filenames for a locked character reference (canonical + short aliases).
    /// </summary>
    public static IEnumerable<string> CharacterRefFileCandidates(string charKey) =>
        RefFileCandidates(charKey, CharacterPrefix, CharacterRefFileName);

    private static IEnumerable<string> RefFileCandidates(
        string? key,
        string prefix,
        Func<string, string> canonicalFileName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileName(name.Trim().Replace(' ', '_')).ToLowerInvariant();
            if (!name.EndsWith(RefPngSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.EndsWith("_ref", StringComparison.OrdinalIgnoreCase) ? name + ".png" : name + RefPngSuffix;
            if (seen.Add(name))
                list.Add(name);
        }

        Add(canonicalFileName(key ?? ""));
        var raw = (key ?? "").Trim();
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var bare = raw[prefix.Length..];
            Add(canonicalFileName(bare));
            Add(bare + RefPngSuffix);
        }
        else
        {
            Add(canonicalFileName(prefix + raw));
        }
        return list;
    }
}
