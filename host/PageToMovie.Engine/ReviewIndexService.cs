using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using Microsoft.Extensions.Logging;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Durable project review index: <c>assets/review/index.json</c> — one row per on-disk clip
/// with auto/human status, assembly eligibility, and durable frame paths (PR3).
/// </summary>
public sealed class ReviewIndexService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private const string AssetsFolder = "assets";
    private const string ReviewFolder = "review";
    private static Regex ExactClipNameRe => ClipFileNaming.ExactClipNameRe;

    private readonly ProjectStore _projects;
    private readonly EditLogService _editLogs;
    private readonly ILogger<ReviewIndexService> _log;

    public ReviewIndexService(
        ProjectStore projects,
        EditLogService editLogs,
        ILogger<ReviewIndexService> log)
    {
        _projects = projects;
        _editLogs = editLogs;
        _log = log;
    }

    public async Task<string> IndexPathAsync(string projectId, CancellationToken ct = default) =>
        Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), AssetsFolder, ReviewFolder, "index.json");

    public string IndexPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), AssetsFolder, ReviewFolder, "index.json");

    public async Task<string> FramesDirAsync(string projectId, CancellationToken ct = default) =>
        Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), AssetsFolder, ReviewFolder, "frames");

    public string FramesDir(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), AssetsFolder, ReviewFolder, "frames");

    public static string DraftRelPath(int scene, int clip) =>
        $"assets/review/S{scene:D2}C{clip:D2}.auto_review.json";

    public static string FrameRelPath(int scene, int clip, int frameIndex) =>
        $"assets/review/frames/S{scene:D2}C{clip:D2}_{frameIndex:D2}.jpg";

    public async Task<ReviewIndexDocument?> LoadAsync(string projectId, CancellationToken ct = default)
    {
        var path = await IndexPathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ReviewIndexDocument>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load review index for {ProjectId}", projectId);
            return null;
        }
    }

    public async Task SaveAsync(ReviewIndexDocument doc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(doc.ProjectId))
            throw new ArgumentException("projectId required", nameof(doc));
        var path = await IndexPathAsync(doc.ProjectId, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        doc.BuiltAtUtc = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(doc, JsonOpts) + "\n";
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    /// <summary>Scan on-disk clips and rebuild full index (drafts, human, assembly, frames).</summary>
    public async Task<ReviewIndexDocument> RebuildAsync(
        string projectId, int? sceneFilter = null, CancellationToken ct = default)
    {
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var clips = ListOnDiskClips(projectDir, sceneFilter);
        var doc = new ReviewIndexDocument
        {
            ProjectId = projectId,
            SchemaVersion = "1",
            BuiltAtUtc = DateTimeOffset.UtcNow,
        };

        foreach (var (scene, clip) in clips)
        {
            doc.Clips.Add(await BuildRowAsync(projectId, projectDir, scene, clip, ct: ct).ConfigureAwait(false));
        }

        await SaveAsync(doc, ct).ConfigureAwait(false);
        return doc;
    }

    /// <summary>Upsert one clip after auto-review (or frame persist).</summary>
    public async Task<ReviewIndexDocument> UpsertClipAsync(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<string>? durableFrameRelPaths = null,
        ClipAutoReviewDraft? draft = null,
        CancellationToken ct = default)
    {
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false) ?? new ReviewIndexDocument
        {
            ProjectId = projectId,
            SchemaVersion = "1",
        };

        var row = await BuildRowAsync(projectId, projectDir, scene, clip, draft, durableFrameRelPaths, ct).ConfigureAwait(false);
        var key = row.Key;
        var idx = doc.Clips.FindIndex(c =>
            string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            doc.Clips[idx] = row;
        else
            doc.Clips.Add(row);

        doc.Clips = doc.Clips
            .OrderBy(c => c.Scene)
            .ThenBy(c => c.Clip)
            .ToList();
        await SaveAsync(doc, ct).ConfigureAwait(false);
        return doc;
    }

    /// <summary>
    /// Remove one clip's row (if present) plus its auto-review draft and durable frames.
    /// Unlike <see cref="Rebuild"/>, this only touches the one row — other scenes/clips in
    /// the index are left untouched.
    /// </summary>
    public async Task RemoveClipAsync(string projectId, int scene, int clip, CancellationToken ct = default)
    {
        var key = $"S{scene:D2}C{clip:D2}";
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        if (doc is not null)
        {
            var removed = doc.Clips.RemoveAll(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                await SaveAsync(doc, ct).ConfigureAwait(false);
        }

        var draftAbs = Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false),
            DraftRelPath(scene, clip).Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(draftAbs))
        {
            try { File.Delete(draftAbs); } catch { /* best effort */ }
        }

        var framesDir = await FramesDirAsync(projectId, ct).ConfigureAwait(false);
        if (Directory.Exists(framesDir))
        {
            var prefix = $"{key}_";
            foreach (var f in Directory.EnumerateFiles(framesDir, prefix + "*.jpg"))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>On-disk (scene, clip) pairs under assets/video, optional scene filter.</summary>
    public IReadOnlyList<(int Scene, int Clip)> ListOnDiskClipCoords(
        string projectId,
        int? sceneFilter = null)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        return ListOnDiskClips(projectDir, sceneFilter);
    }

    /// <summary>Whether a draft file exists for this clip.</summary>
    public bool HasDraft(string projectId, int scene, int clip)
    {
        var path = Path.Combine(
            _projects.GetProjectDir(projectId),
            AssetsFolder, ReviewFolder,
            $"S{scene:D2}C{clip:D2}.auto_review.json");
        return File.Exists(path);
    }

    /// <summary>
    /// Copy current-clip sample frames into durable <c>assets/review/frames/</c>.
    /// Returns project-relative paths (forward slashes).
    /// </summary>
    public async Task<IReadOnlyList<string>> PersistDurableFramesAsync(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<string> sourceFramePaths,
        int maxFrames = 4,
        CancellationToken ct = default)
    {
        if (sourceFramePaths is null || sourceFramePaths.Count == 0)
            return Array.Empty<string>();

        var framesDir = await FramesDirAsync(projectId, ct).ConfigureAwait(false);
        Directory.CreateDirectory(framesDir);

        // Clear prior durable frames for this clip
        var prefix = $"S{scene:D2}C{clip:D2}_";
        try
        {
            foreach (var old in Directory.EnumerateFiles(framesDir, prefix + "*.jpg"))
            {
                try { File.Delete(old); } catch { /* best effort */ }
            }
        }
        catch { /* directory listing is best-effort */ }

        var rel = new List<string>();
        var n = 0;
        foreach (var src in sourceFramePaths.Take(Math.Clamp(maxFrames, 1, 8)))
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) continue;
            n++;
            var relPath = FrameRelPath(scene, clip, n);
            var dest = Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false),
                relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
            File.Copy(src, dest, overwrite: true);
            rel.Add(relPath.Replace('\\', '/'));
        }

        return rel;
    }

    public IReadOnlyList<string> ListExistingFrameRelPaths(string projectId, int scene, int clip)
    {
        var framesDir = FramesDir(projectId);
        if (!Directory.Exists(framesDir)) return Array.Empty<string>();
        var prefix = $"S{scene:D2}C{clip:D2}_";
        return Directory.EnumerateFiles(framesDir, prefix + "*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => ("assets/review/frames/" + Path.GetFileName(f)).Replace('\\', '/'))
            .ToList();
    }

    private async Task<ReviewIndexClipRow> BuildRowAsync(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        ClipAutoReviewDraft? draft = null,
        IReadOnlyList<string>? durableFrameRelPaths = null,
        CancellationToken ct = default)
    {
        var key = $"S{scene:D2}C{clip:D2}";
        var videoRel = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
        var videoAbs = Path.Combine(projectDir, AssetsFolder, "video",
            $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        var videoExists = File.Exists(videoAbs) && new FileInfo(videoAbs).Length >= 512;

        draft ??= TryLoadDraft(projectDir, scene, clip);
        var draftRel = DraftRelPath(scene, clip);
        var draftAbs = Path.Combine(projectDir, draftRel.Replace('/', Path.DirectorySeparatorChar));
        var hasDraft = draft is not null || File.Exists(draftAbs);

        var human = ReadHumanReview(projectDir, key);
        var (eligible, blockReason) = await _editLogs.IsClipEligibleForAssemblyAsync(projectId, scene, clip, ct).ConfigureAwait(false);

        var frames = durableFrameRelPaths is { Count: > 0 }
            ? durableFrameRelPaths.Select(p => p.Replace('\\', '/')).ToList()
            : ListExistingFrameRelPaths(projectId, scene, clip).ToList();

        return new ReviewIndexClipRow
        {
            Key = key,
            Scene = scene,
            Clip = clip,
            VideoPath = videoRel.Replace('\\', '/'),
            VideoExists = videoExists,
            AutoSuggestion = draft?.Suggestion,
            AutoCategory = draft?.Category,
            AutoNote = draft?.Note,
            AutoReviewedAt = draft?.GeneratedAt,
            HumanStatus = string.IsNullOrWhiteSpace(human.Status) ? null : human.Status,
            HumanNote = string.IsNullOrWhiteSpace(human.Note) ? null : human.Note,
            AssemblyEligible = eligible,
            AssemblyBlockReason = eligible ? null : blockReason,
            DraftPath = hasDraft ? draftRel.Replace('\\', '/') : null,
            HasDraft = hasDraft,
            FramePaths = frames,
        };
    }

    private static ClipAutoReviewDraft? TryLoadDraft(string projectDir, int scene, int clip)
    {
        var path = Path.Combine(projectDir, AssetsFolder, ReviewFolder,
            $"S{scene:D2}C{clip:D2}.auto_review.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClipAutoReviewDraft>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static (string Status, string Note) ReadHumanReview(string projectDir, string key)
    {
        try
        {
            var statePath = Path.Combine(projectDir, "pipeline_state.json");
            if (!File.Exists(statePath)) return ("", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!doc.RootElement.TryGetProperty("clip_review", out var cr) ||
                cr.ValueKind != JsonValueKind.Object)
                return ("", "");
            if (!cr.TryGetProperty(key, out var row) || row.ValueKind != JsonValueKind.Object)
                return ("", "");
            var status = row.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var note = row.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            return (status, note);
        }
        catch
        {
            return ("", "");
        }
    }

    private static readonly Regex ExactClipClientJsonRe = new(@"^scene_(\d{2})_clip_(\d{2})\.mp4\.client\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static List<(int Scene, int Clip)> ListOnDiskClips(string projectDir, int? sceneFilter)
    {
        var videoDir = Path.Combine(projectDir, AssetsFolder, "video");
        var set = new HashSet<(int, int)>();
        if (!Directory.Exists(videoDir)) return new List<(int, int)>();

        AddOnDiskMp4Clips(new DirectoryInfo(videoDir), set, sceneFilter);
        AddOnDiskClientJsonClips(new DirectoryInfo(videoDir), set, sceneFilter);

        return set
            .OrderBy(x => x.Item1)
            .ThenBy(x => x.Item2)
            .ToList();
    }

    private static void AddOnDiskMp4Clips(DirectoryInfo videoDir, HashSet<(int, int)> set, int? sceneFilter)
    {
        foreach (var fi in videoDir.EnumerateFiles("scene_*_clip_*.mp4"))
        {
            if (!ExactClipNameRe.IsMatch(fi.Name)) continue;
            if (fi.Length < 512) continue;
            TryAddClipCoord(set, ExactClipNameRe.Match(fi.Name), sceneFilter);
        }
    }

    private static void AddOnDiskClientJsonClips(DirectoryInfo videoDir, HashSet<(int, int)> set, int? sceneFilter)
    {
        foreach (var fi in videoDir.EnumerateFiles("scene_*_clip_*.mp4.client.json"))
        {
            var m = ExactClipClientJsonRe.Match(fi.Name);
            if (!m.Success) continue;
            TryAddClipCoord(set, m, sceneFilter);
        }
    }

    private static void TryAddClipCoord(HashSet<(int, int)> set, Match m, int? sceneFilter)
    {
        if (!int.TryParse(m.Groups[1].Value, out var sn) ||
            !int.TryParse(m.Groups[2].Value, out var cn))
            return;
        if (sceneFilter is int only && only > 0 && sn != only) return;
        set.Add((sn, cn));
    }
}
