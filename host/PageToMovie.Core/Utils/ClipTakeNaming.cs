using System.Globalization;
using System.Text.RegularExpressions;

namespace PageToMovie.Core.Utils;

/// <summary>
/// Single source of truth for film-take identity and clip video paths.
/// Take files are <c>scene_SS_clip_CC_take_NN</c> (no timestamp). Current take is
/// <c>scene_SS_clip_CC.current.json</c> only. A leftover <c>scene_SS_clip_CC.mp4</c>
/// alias is not the player file. Bytes live on the client folder or the provider.
/// </summary>
public static class ClipTakeNaming
{
    public const string AssetsVideoPrefix = "assets/video";
    public const string ClipJsonSuffix = ".clip.json";
    public const string CurrentTakePointerSuffix = ".current.json";

    private static readonly Regex TakeNumberRx = new(
        @"_take_(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex StableTakeStemRx = new(
        @"^scene_(\d{2})_clip_(\d{2})_take_(\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex TimestampedTakeStemRx = new(
        @"^scene_(\d{2})_clip_(\d{2})_take_(\d{2})_\d{8}_\d{6}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex CanonicalStemRx = new(
        @"^scene_(\d{2})_clip_(\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    public static string SceneClipPrefix(int scene, int clip) =>
        $"scene_{scene:D2}_clip_{clip:D2}";

    public static string CanonicalMp4FileName(int scene, int clip) =>
        $"{SceneClipPrefix(scene, clip)}.mp4";

    public static string CanonicalRelativePath(int scene, int clip) =>
        $"{AssetsVideoPrefix}/{CanonicalMp4FileName(scene, clip)}";

    public static string CanonicalSidecarFileName(int scene, int clip) =>
        $"{SceneClipPrefix(scene, clip)}{ClipJsonSuffix}";

    public static string CurrentTakePointerFileName(int scene, int clip) =>
        $"{SceneClipPrefix(scene, clip)}{CurrentTakePointerSuffix}";

    public static string CurrentTakePointerRelativePath(int scene, int clip) =>
        $"{AssetsVideoPrefix}/{CurrentTakePointerFileName(scene, clip)}";

    /// <summary>
    /// Current player file from a take number in <c>.current.json</c>.
    /// Null when <paramref name="take"/> is not a positive take.
    /// </summary>
    public static string? CurrentTakePath(int scene, int clip, int take) =>
        take > 0 ? TakeRelativePath(scene, clip, take) : null;

    public static string? CurrentTakeFileName(int scene, int clip, int take) =>
        take > 0 ? TakeMp4FileName(scene, clip, take) : null;

    /// <summary>
    /// Parse <c>{"take":N}</c> from a current-take pointer. 0 when absent or invalid.
    /// A clip sidecar also carries a <c>take</c> field, so one handed here by mistake would
    /// read as a valid pointer and silently override the promoted take with its own. Sidecars
    /// are rejected on their <c>schema_version</c>.
    /// </summary>
    public static int ParseCurrentTakePointer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (IsClipSidecarDocument(root))
                return 0;
            if (root.TryGetProperty("take", out var t) && t.TryGetInt32(out var n) && n > 0)
                return n;
        }
        catch { /* best-effort pointer */ }
        return 0;
    }

    /// <summary>Sidecar manifests declare <c>clip_sidecar.*</c>; a pointer never does.</summary>
    private static bool IsClipSidecarDocument(System.Text.Json.JsonElement root) =>
        root.ValueKind == System.Text.Json.JsonValueKind.Object
        && root.TryGetProperty("schema_version", out var sv)
        && sv.ValueKind == System.Text.Json.JsonValueKind.String
        && sv.GetString() is { } v
        && v.StartsWith("clip_sidecar", StringComparison.OrdinalIgnoreCase);

    public static string TakeStem(int scene, int clip, int take) =>
        $"{SceneClipPrefix(scene, clip)}_take_{take:D2}";

    public static string TakeMp4FileName(int scene, int clip, int take) =>
        $"{TakeStem(scene, clip, take)}.mp4";

    public static string TakeSidecarFileName(int scene, int clip, int take) =>
        $"{TakeStem(scene, clip, take)}{ClipJsonSuffix}";

    public static string TakeRelativePath(int scene, int clip, int take) =>
        $"{AssetsVideoPrefix}/{TakeMp4FileName(scene, clip, take)}";

    public static string TakeSidecarSearchPattern(int scene, int clip) =>
        $"{SceneClipPrefix(scene, clip)}_take_*{ClipJsonSuffix}";

    /// <summary>
    /// Take number from a take-named file (<c>…_take_NN[…].mp4|.clip.json</c>).
    /// Timestamped leftovers still parse. Returns 0 when the name has no take.
    /// </summary>
    public static int ParseTakeNumber(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return 0;
        var stem = ClipStem(fileName);
        var m = TakeNumberRx.Match(stem);
        if (!m.Success)
            return 0;
        return int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;
    }

    /// <summary>Filename take number if present, else sidecar <c>take</c>, else 0.</summary>
    public static int ResolveTakeNumber(string? fileName, int sidecarTake = 0)
    {
        var fromName = ParseTakeNumber(fileName);
        if (fromName > 0)
            return fromName;
        return sidecarTake > 0 ? sidecarTake : 0;
    }

    /// <summary>
    /// <c>scene_SS_clip_CC_take_NN</c> with no timestamp or other suffix.
    /// </summary>
    public static bool IsStableTakeName(string? fileName)
    {
        var stem = ClipStem(fileName);
        return !string.IsNullOrEmpty(stem) && StableTakeStemRx.IsMatch(stem);
    }

    /// <summary>Legacy conversion leftover: <c>…_take_NN_yyyyMMdd_HHmmss</c>.</summary>
    public static bool IsTimestampedTakeName(string? fileName)
    {
        var stem = ClipStem(fileName);
        return !string.IsNullOrEmpty(stem) && TimestampedTakeStemRx.IsMatch(stem);
    }

    public static bool IsCanonicalClipName(string? fileName)
    {
        var stem = ClipStem(fileName);
        return !string.IsNullOrEmpty(stem) && CanonicalStemRx.IsMatch(stem);
    }

    public static bool IsClipSidecarName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName.EndsWith(ClipJsonSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keep a local <c>.clip.json</c> that is larger than the server copy — the
    /// client (or a later regen) won; media-sync must not roll it back.
    /// </summary>
    public static bool ShouldKeepLocalSidecar(long localSizeBytes, long serverSizeBytes) =>
        localSizeBytes > 0 && serverSizeBytes > 0 && localSizeBytes > serverSizeBytes;

    /// <summary>
    /// Dedup key for a client media save. Job + take path (not the canonical alias)
    /// so a second regen of the same clip in one circuit still writes.
    /// </summary>
    public static string JobMediaSaveKey(string? projectId, string? jobId, string? relativePath, int? takeNumber = null)
    {
        var pid = projectId ?? "";
        if (!string.IsNullOrWhiteSpace(jobId) && takeNumber is > 0)
            return $"{pid}|{jobId}|take-{takeNumber.Value:D2}";
        if (!string.IsNullOrWhiteSpace(jobId))
            return $"{pid}|{jobId}|{relativePath}";
        return $"{pid}|{relativePath}";
    }

    /// <summary>
    /// Assign unique 1..N display numbers. Prefer each item's resolved take;
    /// collisions (two <c>take_01*</c> leftovers) get the next free number.
    /// Never uses list-index identity.
    /// </summary>
    public static void AssignUniqueTakeNumbers(IReadOnlyList<(int Preferred, Action<int> Set)> items)
    {
        if (items is null || items.Count == 0)
            return;
        var used = new HashSet<int>();
        foreach (var (preferred, set) in items)
        {
            var n = preferred > 0 && used.Add(preferred)
                ? preferred
                : NextUnused(used);
            used.Add(n);
            set(n);
        }
    }

    public static int NextUnused(ISet<int> used)
    {
        var n = 1;
        while (used.Contains(n))
            n++;
        return n;
    }

    public static string ClipStem(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "";
        var name = Path.GetFileName(fileName.Trim());
        if (name.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase))
            name = name[..^12];
        if (name.EndsWith(ClipJsonSuffix, StringComparison.OrdinalIgnoreCase))
            return name[..^ClipJsonSuffix.Length];
        return Path.GetFileNameWithoutExtension(name);
    }
}
