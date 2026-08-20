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

    /// <summary>1 + the highest take_NN among the clip's sidecars (1 when none).</summary>
    public static int NextTakeNumber(string videoDir, int scene, int clip)
    {
        var max = 0;
        if (Directory.Exists(videoDir))
        {
            foreach (var f in Directory.EnumerateFiles(videoDir, $"scene_{scene:D2}_clip_{clip:D2}_take_*.clip.json"))
            {
                var n = ParseTakeNumber(Path.GetFileName(f));
                if (n > max) max = n;
            }
        }
        return max + 1;
    }

    /// <summary>take number from "scene_01_clip_02_take_03[...].clip.json|.mp4", 0 when absent.</summary>
    public static int ParseTakeNumber(string fileName)
    {
        var i = fileName.IndexOf("_take_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 8 > fileName.Length) return 0;
        return int.TryParse(fileName.AsSpan(i + 6, 2), out var n) ? n : 0;
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
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        // Takes are the sidecars: every generation writes a NEW numbered sidecar and the previous
        // ones stay (their source_url still points at the earlier provider video). Overwriting
        // _take_01 each time lost every prior take once the server stopped keeping MP4s.
        var take = NextTakeNumber(videoDir, scene, clip);
        var fileName = string.IsNullOrWhiteSpace(mp4FileName)
            ? $"scene_{scene:D2}_clip_{clip:D2}_take_{take:D2}.mp4"
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
        {
            sidecar["source_url"] = sourceUrl.Trim();
            sidecar["source_provider"] = string.IsNullOrWhiteSpace(sourceProvider) ? "" : sourceProvider.Trim();
        }

        // xAI Files API file_id (permanent unless we set expires_after at generate time).
        // Forks skip .mp4s; playback/Easy Start streams this id from xAI.
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
        _log.LogInformation("Written clip sidecar manifest → {Path}", sidecarPath);
        _autoGit?.QueueCommitAndPush(projectDir, projectId, $"Generate S{scene:D2}C{clip:D2} clip sidecar");
        return sidecarPath;
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
        CancellationToken ct = default)
    {
        var videoDir = Path.Combine(projectDir, "assets", "video");
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

        await WriteSidecarStreamAsync(sidecarPath, sidecar, ct).ConfigureAwait(false);
        _log.LogInformation("Written clip sidecar manifest → {Path}", sidecarPath);
        return sidecarPath;
    }

    /// <summary>
    /// One-time conversion method to rename all video clips in assets/video/ to the long-term format:
    /// scene_{S:D2}_clip_{C:D2}_take_{T:D2}_{timestamp}.mp4 (and write matching .clip.json sidecar).
    /// </summary>
    public async Task<int> ConvertProjectClipsToNewFormatAsync(string projectDir, CancellationToken ct = default)
    {
        var videoDir = Path.Combine(projectDir, "assets", "video");
        if (!Directory.Exists(videoDir))
            return 0;

        var videoFiles = Directory.EnumerateFiles(videoDir, "*", SearchOption.AllDirectories)
            .Where(IsConvertibleVideoFile)
            .ToList();

        if (videoFiles.Count == 0)
            return 0;

        var parsedFiles = ParseVideoFiles(videoFiles);
        var groups = parsedFiles.GroupBy(ClipGroupKey);
        var convertedCount = 0;

        foreach (var group in groups)
            convertedCount += await ConvertClipGroupAsync(projectDir, videoDir, group, ct).ConfigureAwait(false);

        return convertedCount;
    }

    private static bool IsConvertibleVideoFile(string f) =>
        (f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
         f.EndsWith(".mp4.client.json", StringComparison.OrdinalIgnoreCase) ||
         f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
         f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
        && !f.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase);

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

    private async Task<int> ConvertClipGroupAsync(
        string projectDir,
        string videoDir,
        IGrouping<(int Scene, int Clip), (string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite)> group,
        CancellationToken ct)
    {
        var take = 1;
        var convertedCount = 0;
        var sorted = group.OrderBy(GetLastWrite).ToList();

        foreach (var item in sorted)
        {
            await ConvertOneClipItemAsync(projectDir, videoDir, item, take, ct).ConfigureAwait(false);
            take++;
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
        var stamp = item.LastWrite.ToString("yyyyMMdd_HHmmss");

        var isClientMarker = item.OriginalName.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase);
        var baseClean = isClientMarker ? item.OriginalName[..^12] : item.OriginalName;
        var ext = Path.GetExtension(baseClean);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        var newBaseName = $"scene_{item.Scene:D2}_clip_{item.Clip:D2}_take_{take:D2}_{stamp}";
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
