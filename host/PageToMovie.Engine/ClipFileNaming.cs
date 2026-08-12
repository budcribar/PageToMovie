using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Clip/scene/WIP file naming and listing helpers (no ffmpeg).
/// </summary>
public static class ClipFileNaming
{
    /// <summary>Matches clip video files formatted with scene, clip, and take numbers (rejecting .native.mp4).</summary>
    public static readonly Regex ExactClipNameRe = new(@"^scene_(\d{2})_clip_(\d{2})(?:_take_\d+.*)?\.mp4$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    public static string SceneSourcesManifestPath(string compositePath) =>
        compositePath + ".sources.json";

    public static string WipSourcesManifestPath(string wipPath) =>
        wipPath + ".sources.json";

    public static bool IsExactClipFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && ExactClipNameRe.IsMatch(fileName);

    private static bool RegexSceneOnly(string name) =>
        CommonRegex.IsMatch(name, @"^scene_\d{2}\.mp4$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Ordered inputs for freshness checks: scene composites first, else exact clip files.
    /// </summary>
    public static List<string> ListWipSourceFiles(string videoDir)
    {
        if (!Directory.Exists(videoDir))
            return new List<string>();

        var sceneFiles = new DirectoryInfo(videoDir).GetFiles("scene_*.mp4")
            .Where(f => RegexSceneOnly(f.Name))
            .Where(f =>
            {
                try { return f.Length >= 1024; }
                catch { return false; }
            })
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.FullName)
            .ToList();

        if (sceneFiles.Count > 0)
            return sceneFiles;

        return new DirectoryInfo(videoDir).GetFiles("scene_*_clip_*.mp4")
            .Where(f => IsExactClipFileName(f.Name))
            .Where(f =>
            {
                try { return f.Length >= 1024; }
                catch { return false; }
            })
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.FullName)
            .ToList();
    }
}
