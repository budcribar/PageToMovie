using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Rare Railway copy of a clip: only when a fork could not stream the xAI file_id.
/// Imagine file_ids are generate-only (read-only). Owner attach then stores the local .mp4
/// on our server and the pruner leaves it alone.
/// </summary>
public static class ClipForkFallback
{
    public const string NeedSuffix = ".need-fork";
    public const string ProtectedSuffix = ".fork-fallback";

    private static readonly Regex NeedName = new(
        @"^scene_(\d+)_clip_(\d+)\.need-fork$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        CommonRegex.Timeout);

    public static string NeedFileName(int scene, int clip) =>
        $"scene_{scene:D2}_clip_{clip:D2}{NeedSuffix}";

    public static string Mp4FileName(int scene, int clip) =>
        $"scene_{scene:D2}_clip_{clip:D2}.mp4";

    public static void MarkNeeded(string projectDir, int scene, int clip)
    {
        var videoDir = VideoDir(projectDir);
        Directory.CreateDirectory(videoDir);
        var path = Path.Combine(videoDir, NeedFileName(scene, clip));
        if (File.Exists(path)) return;
        File.WriteAllText(path, "");
    }

    public static IReadOnlyList<(int Scene, int Clip)> ListNeeded(string projectDir)
    {
        var videoDir = VideoDir(projectDir);
        var list = new List<(int, int)>();
        if (!Directory.Exists(videoDir)) return list;
        foreach (var file in Directory.EnumerateFiles(videoDir, "*" + NeedSuffix))
        {
            var m = NeedName.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            list.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        return list;
    }

    public static void ClearNeeded(string projectDir, int scene, int clip)
    {
        var path = Path.Combine(VideoDir(projectDir), NeedFileName(scene, clip));
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool IsProtectedFromPrune(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        if (File.Exists(fullPath + ProtectedSuffix)) return true;
        var name = Path.GetFileName(fullPath);
        return name.EndsWith(ProtectedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static void WriteProtectedMp4(string projectDir, int scene, int clip, byte[] bytes)
    {
        var videoDir = VideoDir(projectDir);
        Directory.CreateDirectory(videoDir);
        var mp4 = Path.Combine(videoDir, Mp4FileName(scene, clip));
        File.WriteAllBytes(mp4, bytes);
        File.WriteAllText(mp4 + ProtectedSuffix, "fork-fallback\n");
    }

    /// <summary>{project}/assets/video → project root. Null when the path is too short.</summary>
    public static string? ProjectDirFromVideoDir(string? videoDir)
    {
        if (string.IsNullOrWhiteSpace(videoDir)) return null;
        var assets = Path.GetDirectoryName(Path.GetFullPath(videoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        return string.IsNullOrWhiteSpace(assets) ? null : Path.GetDirectoryName(assets);
    }

    /// <summary>Railway-hosted fork copy, only when the prune-guard marker is present.</summary>
    public static string? TryProtectedMp4Path(string? projectDir, int scene, int clip)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || scene <= 0 || clip <= 0) return null;
        var mp4 = Path.Combine(VideoDir(projectDir), Mp4FileName(scene, clip));
        return File.Exists(mp4) && IsProtectedFromPrune(mp4) ? mp4 : null;
    }

    /// <summary>Mark .need-fork so the owner can push a local copy. Never throws.</summary>
    public static void TryMarkNeeded(string? projectDir, int scene, int clip)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || scene <= 0 || clip <= 0) return;
        try { MarkNeeded(projectDir, scene, clip); }
        catch { /* never fail playback on a marker write */ }
    }

    public static void WriteSidecarFileId(string projectDir, int scene, int clip, string fileId)
    {
        var sidecar = Path.Combine(VideoDir(projectDir), $"scene_{scene:D2}_clip_{clip:D2}.clip.json");
        JsonObject node;
        if (File.Exists(sidecar))
        {
            try { node = JsonNode.Parse(File.ReadAllText(sidecar)) as JsonObject ?? new JsonObject(); }
            catch { node = new JsonObject(); }
        }
        else node = new JsonObject();
        node["source_file_id"] = fileId;
        node.Remove("source_file_expires_at");
        File.WriteAllText(sidecar, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string VideoDir(string projectDir) =>
        Path.Combine(projectDir, "assets", "video");
}
