using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PageToMovie.Core.Utils;

/// <summary>
/// Content-stable beat identifiers for screenplay → Stage 1 → Stage 2 → clip linkage.
/// Format: <c>sb_</c> + 12 hex chars of SHA-256 over (scene key, kind, speaker, body, occurrence).
/// Dialogue splits use <c>{id}#p{n}of{m}</c>; merges keep the first id and accumulate
/// <c>source_beat_ids</c> on the beat/clip.
/// </summary>
public static partial class StableBeatId
{
    public const string Prefix = "sb_";

    /// <summary>True when the id was produced by this helper (not legacy sequential <c>b1</c>…).</summary>
    public static bool IsStable(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var root = Root(id);
        return root.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
               && root.Length == Prefix.Length + 12;
    }

    /// <summary>
    /// Content-addressed id for a story beat. Same screenplay content in the same scene
    /// yields the same id across re-imports. <paramref name="occurrence"/> disambiguates
    /// identical repeated lines (0-based among equal content keys in the scene).
    /// </summary>
    public static string ForContent(
        string? sceneKey,
        string kind,
        string? speaker,
        string? body,
        int occurrence = 0)
    {
        var payload = string.Join(
            '\n',
            Normalize(sceneKey),
            Normalize(kind),
            Normalize(speaker),
            Normalize(body),
            occurrence.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        // 6 bytes → 12 hex chars
        return Prefix + Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <summary>Root id without a <c>#pNofM</c> part suffix.</summary>
    public static string Root(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var t = id.Trim();
        var hash = t.IndexOf('#');
        return hash >= 0 ? t[..hash] : t;
    }

    /// <summary>
    /// Id for a dialogue-split part. Unsplit (partCount ≤ 1) returns the base id unchanged.
    /// </summary>
    public static string ForPart(string? baseId, int partIndex, int partCount)
    {
        var root = Root(baseId);
        if (string.IsNullOrWhiteSpace(root))
            root = ForContent("", "part", "", $"{partIndex}/{partCount}");
        if (partCount <= 1) return root;
        var p = Math.Clamp(partIndex, 0, Math.Max(0, partCount - 1)) + 1;
        return $"{root}#p{p}of{partCount}";
    }

    /// <summary>Normalize text for hashing: lowercase, collapse whitespace, strip common punctuation noise.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim().ToLowerInvariant();
        t = WhitespaceRe().Replace(t, " ");
        return t;
    }

    /// <summary>
    /// Collect ordered unique beat ids from a beat dict: existing <c>source_beat_ids</c> if present,
    /// else <c>beat_id</c>.
    /// </summary>
    public static List<string> CollectIds(Dictionary<string, object?> beat)
    {
        var result = new List<string>();
        if (beat.TryGetValue("source_beat_ids", out var raw) && raw is not null)
        {
            if (raw is List<object?> list)
            {
                foreach (var item in list)
                    AddUnique(result, item?.ToString());
            }
            else if (raw is IEnumerable<object?> enumObj)
            {
                foreach (var item in enumObj)
                    AddUnique(result, item?.ToString());
            }
            else if (raw is IEnumerable<string> enumStr)
            {
                foreach (var item in enumStr)
                    AddUnique(result, item);
            }
        }

        if (result.Count == 0 && beat.TryGetValue("beat_id", out var bid))
            AddUnique(result, bid?.ToString());

        return result;
    }

    /// <summary>Merge source ids from <paramref name="next"/> into <paramref name="cur"/> (primary <c>beat_id</c> unchanged).</summary>
    public static void MergeSourceIds(Dictionary<string, object?> cur, Dictionary<string, object?> next)
    {
        var merged = CollectIds(cur);
        foreach (var id in CollectIds(next))
            AddUnique(merged, id);
        cur["source_beat_ids"] = merged.Cast<object?>().ToList();
        if ((!cur.TryGetValue("beat_id", out var primary) || string.IsNullOrWhiteSpace(primary?.ToString()))
            && merged.Count > 0)
            cur["beat_id"] = merged[0];
    }

    private static void AddUnique(List<string> list, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var t = id.Trim();
        if (list.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
            return;
        list.Add(t);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRe();
}
