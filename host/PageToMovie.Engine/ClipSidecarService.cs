using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Writes and parses structured <c>.clip.json</c> sidecar manifests alongside <c>.mp4</c> files.
/// Includes script dialogue/action text, visual prompt, model, resolution, duration, SHA-256 hash,
/// and UTC generation timestamp for timezone-immune versioning.
/// </summary>
public sealed class ClipSidecarService
{
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;
    private static readonly byte[] NewLineBytes = new byte[] { (byte)'\n' };

    /// <summary>Project <c>assets/video</c> directory — naming SSoT is <see cref="ClipTakeNaming.AssetsVideoPrefix"/>.</summary>
    private static string VideoDirFor(string projectDir) =>
        Path.Combine(projectDir, ClipTakeNaming.AssetsVideoPrefix);

    private static async Task WriteSidecarStreamAsync(string sidecarPath, object sidecar, CancellationToken ct)
    {
        await using var stream = new FileStream(sidecarPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, sidecar, JsonOpts, ct).ConfigureAwait(false);
        await stream.WriteAsync(NewLineBytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the common <c>clip_sidecar.v1</c> manifest dictionary. The optional
    /// <paramref name="take"/> key is inserted immediately after <c>clip</c> (only when provided)
    /// to preserve the on-disk key order. Callers append any extra fields (e.g. source_url) after.
    /// </summary>
    private static Dictionary<string, object?> BuildSidecar(
        string projectId, int scene, int clip, int? take,
        string prompt, string scriptText, string model, string resolution,
        double durationSeconds, string sha256, long sizeBytes, DateTime createdUtc)
    {
        var sidecar = new Dictionary<string, object?>
        {
            ["schema_version"] = "clip_sidecar.v1",
            ["project_id"] = projectId,
            ["scene"] = scene,
            ["clip"] = clip,
        };
        if (take is { } t) sidecar["take"] = t;
        sidecar["script_text"] = scriptText ?? "";
        sidecar["visual_prompt"] = prompt ?? "";
        sidecar["model"] = model ?? "";
        sidecar["resolution"] = resolution ?? "";
        sidecar["duration_seconds"] = Math.Round(durationSeconds, 2);
        sidecar["sha256"] = MediaRegistryService.NormalizeSha256(sha256);
        sidecar["size_bytes"] = sizeBytes;
        sidecar["created_at_utc"] = createdUtc.ToString("o");
        return sidecar;
    }
    private readonly ProjectAutoGitService? _autoGit;
    private readonly ILogger<ClipSidecarService> _log;

    public ClipSidecarService(ProjectStore projects, ProjectAutoGitService? autoGit = null, ILogger<ClipSidecarService>? log = null)
    {
        _autoGit = autoGit;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ClipSidecarService>.Instance;
    }

    /// <summary>
    /// 1 + the highest <b>stable</b> <c>take_NN</c> sidecar. Timestamped leftover stubs
    /// (<c>take_NN_yyyyMMdd_HHmmss</c>) are not takes and must not steal the next number.
    /// </summary>
    public static int NextTakeNumber(string videoDir, int scene, int clip)
    {
        var max = 0;
        if (Directory.Exists(videoDir))
        {
            foreach (var f in Directory.EnumerateFiles(videoDir, ClipTakeNaming.TakeSidecarSearchPattern(scene, clip)))
            {
                var name = Path.GetFileName(f);
                if (!ClipTakeNaming.IsStableTakeName(name))
                    continue;
                var n = ParseTakeNumber(name);
                if (n > max) max = n;
            }
        }
        return max + 1;
    }

    /// <summary>Delegates to <see cref="ClipTakeNaming.ParseTakeNumber"/> (filename SSoT).</summary>
    public static int ParseTakeNumber(string fileName) => ClipTakeNaming.ParseTakeNumber(fileName);

    /// <summary>
    /// Next unused <c>take_NN</c> among stable (non-timestamped) sidecars. A leftover
    /// may keep its parsed number when that stable name is free.
    /// </summary>
    public static int NextFreeStableTake(string videoDir, int scene, int clip, string? leftoverName = null)
    {
        var occupied = new HashSet<int>();
        if (Directory.Exists(videoDir))
        {
            foreach (var f in Directory.EnumerateFiles(videoDir, ClipTakeNaming.TakeSidecarSearchPattern(scene, clip)))
            {
                var name = Path.GetFileName(f);
                if (!ClipTakeNaming.IsStableTakeName(name))
                    continue;
                var n = ParseTakeNumber(name);
                if (n > 0) occupied.Add(n);
            }
        }
        var preferred = ClipTakeNaming.ParseTakeNumber(leftoverName);
        if (preferred > 0 && occupied.Add(preferred))
            return preferred;
        return ClipTakeNaming.NextUnused(occupied);
    }

    public static string CurrentTakePointerPath(string videoDir, int scene, int clip) =>
        Path.Combine(videoDir, ClipTakeNaming.CurrentTakePointerFileName(scene, clip));

    /// <summary>Persisted current-take pointer; 0 when absent.</summary>
    public static int ReadCurrentTake(string videoDir, int scene, int clip)
    {
        var path = CurrentTakePointerPath(videoDir, scene, clip);
        if (!File.Exists(path))
            return 0;
        try
        {
            return ClipTakeNaming.ParseCurrentTakePointer(File.ReadAllText(path));
        }
        catch { /* best-effort pointer */ }
        return 0;
    }

    /// <summary>
    /// Player file for this clip: <c>take_NN.mp4</c> named by <c>.current.json</c>.
    /// Never the leftover bare <c>scene_SS_clip_CC.mp4</c> alias.
    /// </summary>
    public static string? CurrentTakePath(string videoDir, int scene, int clip)
    {
        var take = ReadCurrentTake(videoDir, scene, clip);
        var name = ClipTakeNaming.CurrentTakeFileName(scene, clip, take);
        return name is null ? null : Path.Combine(videoDir, name);
    }

    /// <summary>Project-relative player path from <c>.current.json</c>, or null.</summary>
    public static string? CurrentTakeRelativePath(string videoDir, int scene, int clip)
    {
        var take = ReadCurrentTake(videoDir, scene, clip);
        return ClipTakeNaming.CurrentTakePath(scene, clip, take);
    }

    public static void WriteCurrentTake(string videoDir, int scene, int clip, int take)
    {
        if (take <= 0)
            return;
        Directory.CreateDirectory(videoDir);
        var payload = new Dictionary<string, object?>
        {
            ["scene"] = scene,
            ["clip"] = clip,
            ["take"] = take,
        };
        File.WriteAllText(CurrentTakePointerPath(videoDir, scene, clip), JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>
    /// A leftover player-alias sidecar (no <c>_take_NN</c> files yet) is implicit take 1.
    /// Persist it as <c>take_01.clip.json</c> so the next write is take 2 and compare
    /// still has the original. No-op when take sidecars already exist.
    /// </summary>
    public static bool EnsureLegacyCanonicalHasTakeSidecar(string videoDir, int scene, int clip)
    {
        if (!Directory.Exists(videoDir))
            return false;
        if (Directory.EnumerateFiles(videoDir, ClipTakeNaming.TakeSidecarSearchPattern(scene, clip)).Any())
            return false;
        var canonical = Path.Combine(videoDir, ClipTakeNaming.CanonicalSidecarFileName(scene, clip));
        if (!File.Exists(canonical))
            return false;
        var dest = Path.Combine(videoDir, ClipTakeNaming.TakeSidecarFileName(scene, clip, 1));
        RewriteSidecarTakeNumber(canonical, dest, 1);
        WriteCurrentTake(videoDir, scene, clip, 1);
        return true;
    }

    public static string GetSidecarPathForMp4(string mp4Path) =>
        Path.ChangeExtension(mp4Path, ".clip.json");

    /// <summary>
    /// Write a .clip.json sidecar alongside an MP4 video file.
    /// </summary>
    public async Task<string> WriteSidecarAsync(
        string projectDir,
        int scene,
        int clip,
        string prompt,
        string scriptText,
        string model,
        string resolution,
        double durationSeconds,
        string sha256,
        long sizeBytes,
        string? mp4FileName = null,
        string? sourceUrl = null,
        string? sourceProvider = null,
        string? sourceFileId = null,
        long? sourceFileExpiresAtUnixSeconds = null,
        double? providerLeadInSeconds = null,
        double? providerClipStartSeconds = null,
        double? providerClipStopSeconds = null,
        CancellationToken ct = default)
    {
        var videoDir = VideoDirFor(projectDir);
        Directory.CreateDirectory(videoDir);

        // Takes are the sidecars: every generation writes a NEW numbered sidecar and the previous
        // ones stay (their source_url still points at the earlier provider video). Overwriting
        // _take_01 each time lost every prior take once the server stopped keeping MP4s.
        // A leftover player-alias sidecar is implicit take 1 — persist it first so this write is 2.
        EnsureLegacyCanonicalHasTakeSidecar(videoDir, scene, clip);
        var take = NextTakeNumber(videoDir, scene, clip);
        var fileName = string.IsNullOrWhiteSpace(mp4FileName)
            ? ClipTakeNaming.TakeMp4FileName(scene, clip, take)
            : mp4FileName.Trim();

        var mp4Path = Path.Combine(videoDir, fileName);
        var sidecarPath = GetSidecarPathForMp4(mp4Path);

        var projectId = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var sidecar = BuildSidecar(
            projectId, scene, clip, take: take,
            prompt, scriptText, model, resolution,
            durationSeconds, sha256, sizeBytes, DateTime.UtcNow);

        // Provider-hosted source URL (e.g. xAI keeps generated videos for a long time). Persisting it
        // lets a project export carry a re-downloadable pointer so a DIFFERENT user who imports the
        // project can re-fetch the clip bytes, instead of landing with dead clips (bytes only ever
        // lived in the original user's browser media folder). Only stored when the provider gives one.
        if (!string.IsNullOrWhiteSpace(sourceUrl))
            sidecar["source_url"] = sourceUrl.Trim();
        if (!string.IsNullOrWhiteSpace(sourceProvider)
            || !string.IsNullOrWhiteSpace(sourceUrl)
            || !string.IsNullOrWhiteSpace(sourceFileId))
            sidecar["source_provider"] = string.IsNullOrWhiteSpace(sourceProvider) ? "" : sourceProvider.Trim();

        // Provider Files file_id. Forks skip .mp4s; playback streams this id through IVideoClient.
        if (!string.IsNullOrWhiteSpace(sourceFileId))
        {
            sidecar["source_file_id"] = sourceFileId.Trim();
            if (sourceFileExpiresAtUnixSeconds is { } exp)
                sidecar["source_file_expires_at"] = exp;
        }

        // Video-extend: the provider copy is the COMBINED video (continuation input + new footage).
        // Record how much of its head is the previous clip so every consumer of source_url /
        // source_file_id (playback, verification, hop-walk, forks) drops it. Optional start/stop
        // name this clip's window in that file (lead-in → end).
        if (providerLeadInSeconds is > 0.1)
            sidecar[ClipProviderSource.LeadInProperty] = Math.Round(providerLeadInSeconds.Value, 3);
        if (providerClipStartSeconds is { } start && start >= 0)
            sidecar[ClipProviderSource.ClipStartProperty] = Math.Round(start, 3);
        if (providerClipStopSeconds is { } stop && stop > 0.1)
            sidecar[ClipProviderSource.ClipStopProperty] = Math.Round(stop, 3);

        await WriteSidecarStreamAsync(sidecarPath, sidecar, ct).ConfigureAwait(false);
        WriteCurrentTake(videoDir, scene, clip, take);
        _log.LogInformation("Written clip sidecar manifest → {Path}", sidecarPath);
        _autoGit?.QueueCommitAndPush(projectDir, projectId, $"Generate S{scene:D2}C{clip:D2} clip sidecar");
        return sidecarPath;
    }

    /// <summary>
    /// After any generator (catalog video, VideoEdit, or the ffmpeg credits card) produces
    /// bytes: next unique take, <c>take_NN.mp4</c> + sidecar, current pointer.
    /// Does not write or refresh a leftover <c>scene_SS_clip_CC.mp4</c> alias.
    /// </summary>
    public async Task<int> PersistGeneratedTakeAsync(
        string projectDir,
        int scene,
        int clip,
        byte[] bytes,
        PersistGeneratedTakeOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new PersistGeneratedTakeOptions();
        var videoDir = VideoDirFor(projectDir);
        Directory.CreateDirectory(videoDir);
        EnsureLegacyCanonicalHasTakeSidecar(videoDir, scene, clip);

        var take = NextTakeNumber(videoDir, scene, clip);
        var takeMp4Name = ClipTakeNaming.TakeMp4FileName(scene, clip, take);
        await File.WriteAllBytesAsync(Path.Combine(videoDir, takeMp4Name), bytes, ct).ConfigureAwait(false);

        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        await WriteSidecarWithTakeAsync(
            projectDir, scene, clip,
            take: take,
            prompt: options.Prompt,
            scriptText: options.ScriptText,
            model: options.Model,
            resolution: options.Resolution,
            durationSeconds: options.DurationSeconds,
            sha256: sha256,
            sizeBytes: bytes.LongLength,
            mp4FileName: takeMp4Name,
            editedFromTake: options.EditedFromTake,
            sourceUrl: options.SourceUrl,
            sourceProvider: options.SourceProvider,
            ct: ct).ConfigureAwait(false);
        return take;
    }

    /// <summary>
    /// Write a .clip.json sidecar alongside an MP4 video file with take and timestamp metadata.
    /// </summary>
    public async Task<string> WriteSidecarWithTakeAsync(
        string projectDir,
        int scene,
        int clip,
        int take,
        string prompt,
        string scriptText,
        string model,
        string resolution,
        double durationSeconds,
        string sha256,
        long sizeBytes,
        string mp4FileName,
        DateTime? createdUtc = null,
        int? editedFromTake = null,
        string? sourceUrl = null,
        string? sourceProvider = null,
        CancellationToken ct = default)
    {
        var videoDir = VideoDirFor(projectDir);
        Directory.CreateDirectory(videoDir);

        var sidecarName = Path.GetFileNameWithoutExtension(mp4FileName) + ".clip.json";
        var sidecarPath = Path.Combine(videoDir, sidecarName);

        var projectId = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var sidecar = BuildSidecar(
            projectId, scene, clip, take,
            prompt, scriptText, model, resolution,
            durationSeconds, sha256, sizeBytes, createdUtc ?? DateTime.UtcNow);

        // Provenance for an AI-edited take: which prior take it was derived from, so the Takes
        // compare UI can show "edited from Take N" instead of an indistinguishable flat entry.
        // Absent for ordinary (non-edit) takes.
        if (editedFromTake is { } fromTake)
            sidecar["edited_from_take"] = fromTake;
        if (!string.IsNullOrWhiteSpace(sourceUrl))
            sidecar["source_url"] = sourceUrl.Trim();
        if (!string.IsNullOrWhiteSpace(sourceProvider) || !string.IsNullOrWhiteSpace(sourceUrl))
            sidecar["source_provider"] = string.IsNullOrWhiteSpace(sourceProvider) ? "" : sourceProvider.Trim();

        await WriteSidecarStreamAsync(sidecarPath, sidecar, ct).ConfigureAwait(false);
        WriteCurrentTake(videoDir, scene, clip, take);
        _log.LogInformation("Written clip sidecar manifest → {Path}", sidecarPath);
        return sidecarPath;
    }

    /// <summary>
    /// Bring leftover clip files onto the stable take model: <c>scene_SS_clip_CC_take_NN</c>
    /// with no timestamp. Already-converted take trees and leftover bare aliases
    /// (<c>scene_SS_clip_CC.mp4</c>) are left alone. Timestamped leftovers are
    /// <b>renumbered</b> onto the next free take so they never clobber an existing
    /// <c>take_01</c>.
    /// </summary>
    public async Task<int> ConvertProjectClipsToNewFormatAsync(string projectDir, CancellationToken ct = default)
    {
        var videoDir = VideoDirFor(projectDir);
        if (!Directory.Exists(videoDir))
            return 0;

        var converted = 0;
        converted += MigrateTimestampedLeftovers(videoDir);

        var videoFiles = Directory.EnumerateFiles(videoDir, "*", SearchOption.TopDirectoryOnly)
            .Where(IsConvertibleLegacyVideoFile)
            .ToList();
        if (videoFiles.Count > 0)
        {
            var parsedFiles = ParseVideoFiles(videoFiles);
            foreach (var group in parsedFiles.GroupBy(ClipGroupKey))
                converted += await ConvertLegacyClipGroupAsync(projectDir, videoDir, group, ct).ConfigureAwait(false);
        }

        converted += await BackfillCanonicalTakeSidecarsAsync(projectDir, videoDir, ct).ConfigureAwait(false);
        return converted;
    }

    private static bool IsConvertibleVideoFile(string f) =>
        (f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
         f.EndsWith(".mp4.client.json", StringComparison.OrdinalIgnoreCase) ||
         f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
         f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
        && !f.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Legacy names only: not the current alias, not a stable take, not a leftover
    /// timestamped take (those are migrated separately so they can be renumbered).
    /// </summary>
    private static bool IsConvertibleLegacyVideoFile(string f)
    {
        if (!IsConvertibleVideoFile(f))
            return false;
        var name = Path.GetFileName(f);
        if (ClipTakeNaming.IsCanonicalClipName(name))
            return false;
        if (ClipTakeNaming.IsStableTakeName(name))
            return false;
        if (ClipTakeNaming.IsTimestampedTakeName(name))
            return false;
        return true;
    }

    /// <summary>
    /// Rename leftover <c>_take_NN_yyyyMMdd_HHmmss</c> sidecars (and matching videos)
    /// onto the next free take number without a timestamp. Never overwrites an
    /// existing stable <c>take_NN</c>.
    /// </summary>
    private int MigrateTimestampedLeftovers(string videoDir)
    {
        var moved = 0;
        foreach (var sidecar in Directory.EnumerateFiles(videoDir, "*_take_*" + ClipTakeNaming.ClipJsonSuffix)
                     .Where(p => ClipTakeNaming.IsTimestampedTakeName(Path.GetFileName(p)))
                     .OrderBy(p => new FileInfo(p).LastWriteTimeUtc)
                     .ToList())
        {
            if (!TryParseSceneClipFromName(Path.GetFileName(sidecar), out var scene, out var clip))
                continue;
            var destTake = NextFreeStableTake(videoDir, scene, clip, Path.GetFileName(sidecar));
            if (RenameTakeArtifacts(videoDir, sidecar, scene, clip, destTake))
                moved++;
        }

        foreach (var video in Directory.EnumerateFiles(videoDir, "*")
                     .Where(IsConvertibleVideoFile)
                     .Where(p => ClipTakeNaming.IsTimestampedTakeName(Path.GetFileName(p)))
                     .OrderBy(p => new FileInfo(p).LastWriteTimeUtc)
                     .ToList())
        {
            if (!TryParseSceneClipFromName(Path.GetFileName(video), out var scene, out var clip))
                continue;
            var destTake = NextFreeStableTake(videoDir, scene, clip, Path.GetFileName(video));
            var leftoverSidecar = Path.Combine(videoDir, ClipTakeNaming.ClipStem(Path.GetFileName(video)) + ClipTakeNaming.ClipJsonSuffix);
            if (RenameTakeArtifacts(videoDir, File.Exists(leftoverSidecar) ? leftoverSidecar : video, scene, clip, destTake))
                moved++;
        }

        return moved;
    }

    private static bool TryParseSceneClipFromName(string fileName, out int scene, out int clip)
    {
        var (s, c) = ParseSceneClipNumbers(fileName);
        scene = s;
        clip = c;
        return scene > 0 && clip > 0;
    }

    private bool RenameTakeArtifacts(string videoDir, string leftoverPath, int scene, int clip, int destTake)
    {
        var leftoverStem = ClipTakeNaming.ClipStem(Path.GetFileName(leftoverPath));
        var destStem = ClipTakeNaming.TakeStem(scene, clip, destTake);
        if (string.Equals(leftoverStem, destStem, StringComparison.OrdinalIgnoreCase))
            return false;

        var destSidecar = Path.Combine(videoDir, ClipTakeNaming.TakeSidecarFileName(scene, clip, destTake));
        if (File.Exists(destSidecar))
            return false;

        var leftoverSidecar = Path.Combine(videoDir, leftoverStem + ClipTakeNaming.ClipJsonSuffix);
        var leftoverMp4 = Path.Combine(videoDir, leftoverStem + ".mp4");
        var destMp4 = Path.Combine(videoDir, ClipTakeNaming.TakeMp4FileName(scene, clip, destTake));

        try
        {
            if (File.Exists(leftoverSidecar))
            {
                RewriteSidecarTakeNumber(leftoverSidecar, destSidecar, destTake);
                if (!string.Equals(leftoverSidecar, destSidecar, StringComparison.OrdinalIgnoreCase))
                    File.Delete(leftoverSidecar);
            }
            if (File.Exists(leftoverMp4) && !File.Exists(destMp4))
                File.Move(leftoverMp4, destMp4);
            _log.LogInformation("Migrated leftover take {Old} → take {Take}", leftoverStem, destTake);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed migrating leftover take {Old}", leftoverStem);
            return false;
        }
    }

    private static void RewriteSidecarTakeNumber(string sourceSidecar, string destSidecar, int take)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sourceSidecar));
            var obj = JsonNode.Parse(doc.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
            obj["take"] = take;
            File.WriteAllText(destSidecar, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            File.Copy(sourceSidecar, destSidecar, overwrite: false);
        }
    }

    /// <summary>
    /// Canonical <c>scene_SS_clip_CC.mp4</c> is the player alias — do not rename it.
    /// If the clip has no take sidecar yet, write <c>take_01.clip.json</c>.
    /// </summary>
    private async Task<int> BackfillCanonicalTakeSidecarsAsync(string projectDir, string videoDir, CancellationToken ct)
    {
        var count = 0;
        foreach (var mp4 in Directory.EnumerateFiles(videoDir, "scene_*_clip_*.mp4"))
        {
            var name = Path.GetFileName(mp4);
            if (!ClipTakeNaming.IsCanonicalClipName(name))
                continue;
            if (!TryParseSceneClipFromName(name, out var scene, out var clip))
                continue;
            if (Directory.EnumerateFiles(videoDir, ClipTakeNaming.TakeSidecarSearchPattern(scene, clip)).Any())
                continue;

            var fi = new FileInfo(mp4);
            var sha256 = fi.Exists ? await MediaRegistryService.HashFileAsync(mp4, ct).ConfigureAwait(false) : "";
            var promptText = await LoadClipPromptTextAsync(videoDir, scene, clip, ct).ConfigureAwait(false);
            await WriteSidecarWithTakeAsync(
                projectDir, scene, clip, take: 1,
                prompt: promptText, scriptText: "", model: "", resolution: "480p",
                durationSeconds: 6.0, sha256: sha256, sizeBytes: fi.Exists ? fi.Length : 0,
                mp4FileName: ClipTakeNaming.TakeMp4FileName(scene, clip, 1),
                createdUtc: fi.LastWriteTimeUtc, ct: ct).ConfigureAwait(false);
            WriteCurrentTake(videoDir, scene, clip, 1);
            count++;
        }
        return count;
    }

    private static (int Scene, int Clip) ClipGroupKey(
        (string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite) x) =>
        (x.Scene, x.Clip);

    private static List<(string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite)> ParseVideoFiles(
        List<string> videoFiles)
    {
        var parsedFiles = new List<(string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite)>();
        foreach (var file in videoFiles)
        {
            var name = Path.GetFileName(file);
            var cleanName = name.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase) ? name[..^12] : name;
            var (scene, clip) = ParseSceneClipNumbers(cleanName);
            var fi = new FileInfo(file);
            parsedFiles.Add((file, name, scene, clip, fi.LastWriteTimeUtc));
        }
        return parsedFiles;
    }

    private static (int Scene, int Clip) ParseSceneClipNumbers(string cleanName)
    {
        var match = CommonRegex.Match(
            cleanName, @"scene_?(\d+)(?:_clip_?(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var scene = MatchGroupInt(match, 1, requireGroupSuccess: false, fallback: 1);
        var clip = MatchGroupInt(match, 2, requireGroupSuccess: true, fallback: 1);
        return (scene, clip);
    }

    private static int MatchGroupInt(System.Text.RegularExpressions.Match match, int group, bool requireGroupSuccess, int fallback)
    {
        if (!match.Success)
            return fallback;
        if (requireGroupSuccess && !match.Groups[group].Success)
            return fallback;
        if (!int.TryParse(match.Groups[group].Value, out var n))
            return fallback;
        return n;
    }

    private async Task<int> ConvertLegacyClipGroupAsync(
        string projectDir,
        string videoDir,
        IGrouping<(int Scene, int Clip), (string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite)> group,
        CancellationToken ct)
    {
        var convertedCount = 0;
        var sorted = group.OrderBy(GetLastWrite).ToList();

        foreach (var item in sorted)
        {
            var take = NextTakeNumber(videoDir, item.Scene, item.Clip);
            await ConvertOneClipItemAsync(projectDir, videoDir, item, take, ct).ConfigureAwait(false);
            convertedCount++;
        }
        return convertedCount;
    }

    private static DateTime GetLastWrite(
        (string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite) item) =>
        item.LastWrite;

    private async Task ConvertOneClipItemAsync(
        string projectDir,
        string videoDir,
        (string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite) item,
        int take,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var isClientMarker = item.OriginalName.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase);
        var baseClean = isClientMarker ? item.OriginalName[..^12] : item.OriginalName;
        var ext = Path.GetExtension(baseClean);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        var newBaseName = ClipTakeNaming.TakeStem(item.Scene, item.Clip, take);
        var newMp4Name = $"{newBaseName}{ext}";
        var dir = Path.GetDirectoryName(item.FullPath) ?? ".";

        var newMp4Path = Path.Combine(dir, newMp4Name);

        await TryRenameClipFileAsync(item, isClientMarker, dir, newMp4Name, newMp4Path, ct).ConfigureAwait(false);

        var promptText = await LoadClipPromptTextAsync(videoDir, item.Scene, item.Clip, ct).ConfigureAwait(false);

        var targetFileForHash = File.Exists(newMp4Path) ? newMp4Path : item.FullPath;
        var fi = new FileInfo(targetFileForHash);
        var sha256 = File.Exists(newMp4Path) ? await MediaRegistryService.HashFileAsync(newMp4Path, ct).ConfigureAwait(false) : "";

        await WriteSidecarWithTakeAsync(
            projectDir: projectDir,
            scene: item.Scene,
            clip: item.Clip,
            take: take,
            prompt: promptText,
            scriptText: "",
            model: "",
            resolution: "480p",
            durationSeconds: 6.0,
            sha256: sha256,
            sizeBytes: fi.Exists ? fi.Length : 0,
            mp4FileName: newMp4Name,
            createdUtc: item.LastWrite,
            ct: ct).ConfigureAwait(false);
    }

    private async Task TryRenameClipFileAsync(
        (string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite) item,
        bool isClientMarker,
        string dir,
        string newMp4Name,
        string newMp4Path,
        CancellationToken ct)
    {
        if (string.Equals(item.OriginalName, newMp4Name, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (isClientMarker)
            {
                var targetMarker = Path.Combine(dir, $"{newMp4Name}.client.json");
                if (!File.Exists(targetMarker))
                    await MoveFileAsync(item.FullPath, targetMarker, ct).ConfigureAwait(false);
            }
            else if (!File.Exists(newMp4Path))
            {
                await MoveFileAsync(item.FullPath, newMp4Path, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed renaming clip {Old} → {New}", item.OriginalName, newMp4Name);
        }
    }

    private static Task MoveFileAsync(string source, string dest, CancellationToken ct) =>
        Task.Run(() => File.Move(source, dest), ct);

    private static async Task<string> LoadClipPromptTextAsync(string videoDir, int scene, int clip, CancellationToken ct)
    {
        var promptPath = Path.Combine(videoDir, "prompts", $"S{scene:D2}C{clip:D2}.txt");
        if (!File.Exists(promptPath))
            return "";
        try
        {
            return await File.ReadAllTextAsync(promptPath, ct).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Alias for ConvertProjectClipsToNewFormatAsync for backward compatibility.
    /// </summary>
    public Task<int> EnsureAllSidecarsExistAsync(string projectDir, CancellationToken ct = default) =>
        ConvertProjectClipsToNewFormatAsync(projectDir, ct);
}

/// <summary>
/// Sidecar metadata for <see cref="ClipSidecarService.PersistGeneratedTakeAsync"/>.
/// Identity (project, scene, clip, bytes) stays on that method.
/// </summary>
public sealed class PersistGeneratedTakeOptions
{
    public string Prompt { get; init; } = "";
    public string ScriptText { get; init; } = "";
    public string Model { get; init; } = "";
    public string Resolution { get; init; } = "";
    public double DurationSeconds { get; init; }
    public int? EditedFromTake { get; init; }
    public string? SourceUrl { get; init; }
    public string? SourceProvider { get; init; }
}
