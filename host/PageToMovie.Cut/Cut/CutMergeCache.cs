using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Per-scene / per-join compose cache for a feature-length cut.
/// A title or join on scene 40 must not re-encode scenes 1–39.
/// Music is a last mix over cached picture — not a reason to xfade again.
/// </summary>
public static class CutMergeCache
{
    public const string DirectoryName = "cut.cache";
    public const string PictureFileName = DirectoryName + "/picture.mp4";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex SceneFileRx = new(
        @"^(?:.*[/\\])?s(\d{2})\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex JoinFileRx = new(
        @"^(?:.*[/\\])?j(\d{2})\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    public static string SceneFileName(int scene) =>
        string.Create(CultureInfo.InvariantCulture, $"{DirectoryName}/s{scene:D2}.mp4");

    public static string JoinFileName(int fromScene) =>
        string.Create(CultureInfo.InvariantCulture, $"{DirectoryName}/j{fromScene:D2}.mp4");

    public static bool IsPictureFileName(string? path) =>
        string.Equals(FileNameOf(path), "picture.mp4", StringComparison.OrdinalIgnoreCase)
        && IsUnderCacheDir(path);

    public static bool IsCacheFileName(string? path) =>
        IsPictureFileName(path)
        || TryParseSceneFile(path, out _)
        || TryParseJoinFile(path, out _);

    public static bool TryParseSceneFile(string? path, out int scene)
    {
        scene = 0;
        if (!IsUnderCacheDir(path))
            return false;
        var m = SceneFileRx.Match(FileNameOf(path));
        if (!m.Success)
            return false;
        scene = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return scene > 0;
    }

    public static bool TryParseJoinFile(string? path, out int fromScene)
    {
        fromScene = 0;
        if (!IsUnderCacheDir(path))
            return false;
        var m = JoinFileRx.Match(FileNameOf(path));
        if (!m.Success)
            return false;
        fromScene = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return fromScene > 0;
    }

    public static CutMergePlan Build(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts,
        string? audioFileName,
        CutMusic? music)
    {
        var layout = CutTimelineLayout.Build(clips, CutTimelineLayout.DefaultPxPerSec);
        var titles = texts ?? [];
        var scenes = new List<CutMergeScene>(layout.Scenes.Count);
        foreach (var band in layout.Scenes)
        {
            var fp = SceneFingerprint(clips, titles, band);
            scenes.Add(new CutMergeScene(
                band.Scene,
                band.FirstIndex,
                band.ClipCount,
                band.StartSec,
                band.WidthSec,
                fp,
                SceneFileName(band.Scene)));
        }

        var joins = new List<CutMergeJoin>(Math.Max(0, scenes.Count - 1));
        for (var i = 0; i < layout.Scenes.Count - 1; i++)
        {
            var left = layout.Scenes[i];
            var right = layout.Scenes[i + 1];
            var last = clips[left.FirstIndex + left.ClipCount - 1];
            var first = clips[right.FirstIndex];
            var kind = last.JoinToNext(first);
            var fade = CutComposeContract.JoinIsXfade(kind)
                ? CutComposeContract.XfadeSecondsFor(left.WidthSec)
                : 0;
            var hold = CutComposeContract.HoldSeconds(kind);
            var fp = JoinFingerprint(clips, titles, left, right, kind, fade, hold);
            joins.Add(new CutMergeJoin(
                left.Scene,
                right.Scene,
                kind,
                hold,
                fade,
                fp,
                JoinFileName(left.Scene),
                Encodes: kind != CutJoinKind.Cut));
        }

        var picture = PictureFingerprint(scenes, joins);
        var score = MusicFingerprint(audioFileName, music);
        return new CutMergePlan(
            scenes,
            joins,
            picture,
            score,
            CutPlayMerge.Fingerprint(clips, titles, audioFileName, music));
    }

    public static CutMergeManifest ManifestOf(CutMergePlan plan) =>
        new()
        {
            MovieFingerprint = plan.MovieFingerprint,
            PictureFingerprint = plan.PictureFingerprint,
            MusicFingerprint = plan.MusicFingerprint,
            PictureFile = PictureFileName,
            Scenes = plan.Scenes
                .Select(s => new CutMergeSeg(s.Scene, s.Fingerprint, s.FileName))
                .ToList(),
            Joins = plan.Joins
                .Select(j => new CutMergeSeg(j.FromScene, j.Fingerprint, j.FileName))
                .ToList(),
        };

    public static CutMergeDiff Diff(CutMergePlan plan, CutMergeManifest? saved)
    {
        var savedScenes = IndexById(saved?.Scenes);
        var savedJoins = IndexById(saved?.Joins);
        var rebuildScenes = new List<int>();
        foreach (var scene in plan.Scenes)
        {
            if (!savedScenes.TryGetValue(scene.Scene, out var fp)
                || !string.Equals(fp, scene.Fingerprint, StringComparison.Ordinal))
                rebuildScenes.Add(scene.Scene);
        }

        var rebuildJoins = new List<int>();
        foreach (var join in plan.Joins)
        {
            if (!join.Encodes)
                continue;
            if (!savedJoins.TryGetValue(join.FromScene, out var fp)
                || !string.Equals(fp, join.Fingerprint, StringComparison.Ordinal))
                rebuildJoins.Add(join.FromScene);
        }

        var pictureFresh = saved is not null
            && !string.IsNullOrWhiteSpace(saved.PictureFingerprint)
            && string.Equals(saved.PictureFingerprint, plan.PictureFingerprint, StringComparison.Ordinal)
            && rebuildScenes.Count == 0
            && rebuildJoins.Count == 0;
        var musicFresh = saved is not null
            && string.Equals(saved.MusicFingerprint ?? "", plan.MusicFingerprint, StringComparison.Ordinal);
        var movieFresh = saved is not null
            && !string.IsNullOrWhiteSpace(saved.MovieFingerprint)
            && string.Equals(saved.MovieFingerprint, plan.MovieFingerprint, StringComparison.Ordinal);
        var remixOnly = pictureFresh && !musicFresh;
        return new CutMergeDiff(
            movieFresh,
            pictureFresh,
            musicFresh,
            rebuildScenes,
            rebuildJoins,
            MustStitch: !movieFresh,
            RemixMusicOnly: remixOnly);
    }

    public static bool CanReuseMovie(CutMergeDiff diff, string? movieUrl) =>
        diff.MovieFresh && !string.IsNullOrWhiteSpace(movieUrl);

    public static bool CanReusePicture(CutMergeDiff diff, string? pictureUrl) =>
        diff.PictureFresh && !string.IsNullOrWhiteSpace(pictureUrl);

    public static string SceneFingerprint(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip> titles,
        CutTimelineSceneBand band)
    {
        var sb = new StringBuilder();
        sb.Append('S').Append(band.Scene);
        var last = Math.Min(clips.Count, band.FirstIndex + band.ClipCount);
        for (var i = band.FirstIndex; i < last; i++)
            AppendClipPicture(sb, clips[i]);
        AppendOverlappingTitles(sb, titles, band.StartSec, band.StartSec + band.WidthSec);
        return Hash(sb);
    }

    public static string JoinFingerprint(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip> titles,
        CutTimelineSceneBand left,
        CutTimelineSceneBand right,
        CutJoinKind kind,
        double fadeSec,
        double holdSec)
    {
        var sb = new StringBuilder();
        sb.Append('J').Append(left.Scene).Append('>').Append(right.Scene);
        sb.Append(CutTransitionMap.WireName(kind));
        sb.Append('H').Append(Num(holdSec)).Append('F').Append(Num(fadeSec));
        var last = clips[left.FirstIndex + left.ClipCount - 1];
        var first = clips[right.FirstIndex];
        AppendEdge(sb, 'L', last, outgoing: true);
        AppendEdge(sb, 'R', first, outgoing: false);
        if (fadeSec > 0.05)
        {
            AppendOverlappingTitles(sb, titles, left.StartSec + left.WidthSec - fadeSec, left.StartSec + left.WidthSec);
            AppendOverlappingTitles(sb, titles, right.StartSec, right.StartSec + fadeSec);
        }

        return Hash(sb);
    }

    public static string MusicFingerprint(string? audioFileName, CutMusic? music)
    {
        var sb = new StringBuilder();
        sb.Append(audioFileName ?? music?.FileName ?? "");
        if (music is not null)
        {
            sb.Append('M').Append(Num(music.StartSec))
                .Append('/').Append(Num(music.MarkIn))
                .Append('-').Append(Num(music.MarkOut));
        }

        return Hash(sb);
    }

    public static string PictureFingerprint(
        IReadOnlyList<CutMergeScene> scenes,
        IReadOnlyList<CutMergeJoin> joins)
    {
        var sb = new StringBuilder();
        foreach (var scene in scenes)
            sb.Append('S').Append(scene.Scene).Append(scene.Fingerprint);
        foreach (var join in joins)
            sb.Append('J').Append(join.FromScene).Append(join.Fingerprint);
        return Hash(sb);
    }

    private static void AppendClipPicture(StringBuilder sb, CutClip clip)
    {
        sb.Append('|').Append(clip.Scene).Append(':').Append(clip.Clip);
        sb.Append('@').Append(Num(clip.MarkIn)).Append('-').Append(Num(clip.MarkOut));
        foreach (var span in clip.RangeDeletes)
            sb.Append('~').Append(Num(span.Start)).Append('-').Append(Num(span.End));
        if (clip.Card.Enabled)
            sb.Append('C').Append(clip.Card.Text).Append('/').Append(Num(clip.Card.HoldSeconds));
    }

    private static void AppendEdge(StringBuilder sb, char side, CutClip clip, bool outgoing)
    {
        sb.Append(side).Append(clip.Scene).Append(':').Append(clip.Clip);
        sb.Append('@').Append(Num(outgoing ? clip.MarkOut : clip.MarkIn));
        var windows = clip.KeepWindows();
        if (windows.Count == 0)
            return;
        var edge = outgoing ? windows[^1].End : windows[0].Start;
        sb.Append('W').Append(Num(edge));
    }

    private static void AppendOverlappingTitles(
        StringBuilder sb,
        IReadOnlyList<CutTextClip> titles,
        double fromSec,
        double toSec)
    {
        foreach (var title in titles)
        {
            var start = title.StartSec;
            var end = start + title.HoldSeconds;
            if (end <= fromSec + 0.0001 || start >= toSec - 0.0001)
                continue;
            sb.Append('#').Append(title.Text)
                .Append('@').Append(Num(title.StartSec))
                .Append('x').Append(Num(title.HoldSeconds));
        }
    }

    private static Dictionary<int, string> IndexById(IReadOnlyList<CutMergeSeg>? rows)
    {
        var map = new Dictionary<int, string>();
        foreach (var row in rows ?? [])
        {
            if (row.Id <= 0 || string.IsNullOrWhiteSpace(row.Fingerprint))
                continue;
            map[row.Id] = row.Fingerprint;
        }

        return map;
    }

    private static bool IsUnderCacheDir(string? path)
    {
        var rel = (path ?? "").Replace('\\', '/');
        return rel.StartsWith(DirectoryName + "/", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("/" + DirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileNameOf(string? path) => CutClipNaming.FileNameOnly(path);

    private static string Hash(StringBuilder sb)
    {
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Num(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}

public readonly record struct CutMergePlan(
    IReadOnlyList<CutMergeScene> Scenes,
    IReadOnlyList<CutMergeJoin> Joins,
    string PictureFingerprint,
    string MusicFingerprint,
    string MovieFingerprint);

public readonly record struct CutMergeScene(
    int Scene,
    int FirstClipIndex,
    int ClipCount,
    double StartSec,
    double Seconds,
    string Fingerprint,
    string FileName);

public readonly record struct CutMergeJoin(
    int FromScene,
    int ToScene,
    CutJoinKind Kind,
    double HoldSec,
    double FadeSec,
    string Fingerprint,
    string FileName,
    bool Encodes);

public readonly record struct CutMergeDiff(
    bool MovieFresh,
    bool PictureFresh,
    bool MusicFresh,
    IReadOnlyList<int> RebuildScenes,
    IReadOnlyList<int> RebuildJoins,
    bool MustStitch,
    bool RemixMusicOnly);

public sealed class CutMergeManifest
{
    public string? MovieFingerprint { get; set; }
    public string? PictureFingerprint { get; set; }
    public string? MusicFingerprint { get; set; }
    public string? PictureFile { get; set; }
    public List<CutMergeSeg> Scenes { get; set; } = [];
    public List<CutMergeSeg> Joins { get; set; } = [];
}

public readonly record struct CutMergeSeg(int Id, string Fingerprint, string File);

/// <summary>In-memory URLs for cached scene / join / picture files.</summary>
public sealed class CutMergeRuntime
{
    public CutMergeManifest Built { get; set; } = new();
    public Dictionary<int, string> SceneUrls { get; } = [];
    public Dictionary<int, string> JoinUrls { get; } = [];
    public string? PictureUrl { get; set; }

    public void Clear()
    {
        Built = new CutMergeManifest();
        SceneUrls.Clear();
        JoinUrls.Clear();
        PictureUrl = null;
    }

    public void RememberPlan(CutMergePlan plan)
    {
        Built.MovieFingerprint = plan.MovieFingerprint;
        Built.PictureFingerprint = plan.PictureFingerprint;
        Built.MusicFingerprint = plan.MusicFingerprint;
        Built.PictureFile = CutMergeCache.PictureFileName;
        Built.Scenes = plan.Scenes
            .Select(s => new CutMergeSeg(s.Scene, s.Fingerprint, s.FileName))
            .ToList();
        Built.Joins = plan.Joins
            .Select(j => new CutMergeSeg(j.FromScene, j.Fingerprint, j.FileName))
            .ToList();
    }

    public void RememberScene(int scene, string url, string fingerprint)
    {
        if (scene <= 0 || string.IsNullOrWhiteSpace(url))
            return;
        SceneUrls[scene] = url;
        Upsert(Built.Scenes, scene, fingerprint, CutMergeCache.SceneFileName(scene));
    }

    public void RememberJoin(int fromScene, string url, string fingerprint)
    {
        if (fromScene <= 0 || string.IsNullOrWhiteSpace(url))
            return;
        JoinUrls[fromScene] = url;
        Upsert(Built.Joins, fromScene, fingerprint, CutMergeCache.JoinFileName(fromScene));
    }

    public string? SceneUrlIfFresh(int scene, string fingerprint) =>
        FreshUrl(SceneUrls, Built.Scenes, scene, fingerprint);

    public string? JoinUrlIfFresh(int fromScene, string fingerprint) =>
        FreshUrl(JoinUrls, Built.Joins, fromScene, fingerprint);

    private static string? FreshUrl(
        Dictionary<int, string> urls,
        List<CutMergeSeg> rows,
        int id,
        string fingerprint)
    {
        if (!urls.TryGetValue(id, out var url) || string.IsNullOrWhiteSpace(url))
            return null;
        var row = rows.FirstOrDefault(s => s.Id == id);
        if (string.IsNullOrWhiteSpace(row.Fingerprint)
            || !string.Equals(row.Fingerprint, fingerprint, StringComparison.Ordinal))
            return null;
        return url;
    }

    private static void Upsert(List<CutMergeSeg> rows, int id, string fingerprint, string file)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id != id)
                continue;
            rows[i] = new CutMergeSeg(id, fingerprint, file);
            return;
        }

        rows.Add(new CutMergeSeg(id, fingerprint, file));
    }
}
