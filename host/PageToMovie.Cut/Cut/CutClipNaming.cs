using System.Globalization;
using System.Text.RegularExpressions;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Take-file SSoT for standalone Cut (no Engine / Core reference).
/// Each take is <c>scene_SS_clip_CC_take_NN.mp4</c>. Current = <c>.current.json</c> only.
/// Bare <c>scene_SS_clip_CC.mp4</c> is legacy and ignored.
/// </summary>
public static class CutClipNaming
{
    public const string ClipJsonSuffix = ".clip.json";
    public const string CurrentTakePointerSuffix = ".current.json";
    public const string ProjectFileName = "cut.project.json";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex TakeNumberRx = new(
        @"_take_(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex StableTakeStemRx = new(
        @"^scene_(\d{2})_clip_(\d{2})_take_(\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex TimestampedTakeStemRx = new(
        @"^scene_(\d{2})_clip_(\d{2})_take_(\d{2})_\d{8}_\d{6}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex CanonicalStemRx = new(
        @"^scene_(\d{2})_clip_(\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SceneClipRx = new(
        @"^scene_(\d{2})_clip_(\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    public static string FileNameOnly(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
            return "";
        var name = pathOrFileName.Replace('\\', '/').Trim();
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    public static string ClipStem(string? fileName)
    {
        var name = FileNameOnly(fileName);
        if (name.Length == 0)
            return "";
        if (name.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase))
            name = name[..^12];
        if (name.EndsWith(ClipJsonSuffix, StringComparison.OrdinalIgnoreCase))
            return name[..^ClipJsonSuffix.Length];
        if (name.EndsWith(CurrentTakePointerSuffix, StringComparison.OrdinalIgnoreCase))
            return name[..^CurrentTakePointerSuffix.Length];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    public static bool TryParseSceneClip(string? pathOrFileName, out int scene, out int clip)
    {
        scene = 0;
        clip = 0;
        var stem = ClipStem(pathOrFileName);
        if (stem.Length == 0)
            return false;
        var m = SceneClipRx.Match(stem);
        if (!m.Success)
            return false;
        scene = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        clip = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    public static bool IsCanonicalClipName(string? fileName)
    {
        var stem = ClipStem(fileName);
        return stem.Length > 0 && CanonicalStemRx.IsMatch(stem);
    }

    public static bool IsStableTakeName(string? fileName)
    {
        var stem = ClipStem(fileName);
        return stem.Length > 0 && StableTakeStemRx.IsMatch(stem);
    }

    public static bool IsTimestampedTakeName(string? fileName)
    {
        var stem = ClipStem(fileName);
        return stem.Length > 0 && TimestampedTakeStemRx.IsMatch(stem);
    }

    public static bool IsCurrentPointerName(string? fileName)
    {
        var name = FileNameOnly(fileName);
        return name.EndsWith(CurrentTakePointerSuffix, StringComparison.OrdinalIgnoreCase)
               && IsCanonicalClipName(name);
    }

    public static bool IsProjectFileName(string? fileName) =>
        string.Equals(FileNameOnly(fileName), ProjectFileName, StringComparison.OrdinalIgnoreCase);

    public static bool IsClipSidecarName(string? fileName)
    {
        var name = FileNameOnly(fileName);
        return name.EndsWith(ClipJsonSuffix, StringComparison.OrdinalIgnoreCase)
               && TryParseSceneClip(name, out _, out _);
    }

    public static int ParseTakeNumber(string? fileName)
    {
        var stem = ClipStem(fileName);
        if (stem.Length == 0)
            return 0;
        var m = TakeNumberRx.Match(stem);
        if (!m.Success)
            return 0;
        return int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;
    }

    public static bool IsLegacyAliasMp4(string? fileName) =>
        IsCanonicalClipName(fileName)
        && FileNameOnly(fileName).EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        && !IsStableTakeName(fileName);

    public static bool IsUsableClipMp4(string? fileName) =>
        IsStableTakeName(fileName)
        && FileNameOnly(fileName).EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        && !IsTimestampedTakeName(fileName);

    public static string CurrentTakePointerFileName(int scene, int clip) =>
        $"scene_{scene:D2}_clip_{clip:D2}{CurrentTakePointerSuffix}";

    public static string PointerPathBeside(string takeRelativePath, int scene, int clip)
    {
        var path = takeRelativePath.Replace('\\', '/');
        var slash = path.LastIndexOf('/');
        var dir = slash >= 0 ? path[..slash] : "";
        var name = CurrentTakePointerFileName(scene, clip);
        return dir.Length == 0 ? name : $"{dir}/{name}";
    }

    public static string CurrentPointerJson(int scene, int clip, int take) =>
        $$"""{"scene":{{scene}},"clip":{{clip}},"take":{{take}}}""";
}
