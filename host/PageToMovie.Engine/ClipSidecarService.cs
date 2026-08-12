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
        CancellationToken ct = default)
    {
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var fileName = string.IsNullOrWhiteSpace(mp4FileName)
            ? $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4"
            : mp4FileName.Trim();

        var mp4Path = Path.Combine(videoDir, fileName);
        var sidecarPath = GetSidecarPathForMp4(mp4Path);

        var projectId = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var sidecar = BuildSidecar(
            projectId, scene, clip, take: null,
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

        // xAI Files API reference for this exact clip (only present when the provider requested
        // storage at generation time and it succeeded). Lets a later "AI Edit" reuse the file
        // instead of re-uploading — see IVideoEditClient. Never required; absent means the edit
        // path falls back to a base64 upload of the local file.
        if (!string.IsNullOrWhiteSpace(sourceFileId))
        {
            sidecar["source_file_id"] = sourceFileId.Trim();
            if (sourceFileExpiresAtUnixSeconds is { } exp)
                sidecar["source_file_expires_at"] = exp;
        }

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
            .Where(f => (f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".mp4.client.json", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                        && !f.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (videoFiles.Count == 0)
            return 0;

        var parsedFiles = new List<(string FullPath, string OriginalName, int Scene, int Clip, DateTime LastWrite)>();

        foreach (var file in videoFiles)
        {
            var name = Path.GetFileName(file);
            var cleanName = name.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase) ? name[..^12] : name;

            var match = CommonRegex.Match(
                cleanName, @"scene_?(\d+)(?:_clip_?(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var scene = match.Success && int.TryParse(match.Groups[1].Value, out var s) ? s : 1;
            var clip = match.Success && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var c) ? c : 1;

            var fi = new FileInfo(file);
            parsedFiles.Add((file, name, scene, clip, fi.LastWriteTimeUtc));
        }

        var groups = parsedFiles.GroupBy(x => (x.Scene, x.Clip));
        var convertedCount = 0;

        foreach (var group in groups)
        {
            var take = 1;
            var sorted = group.OrderBy(x => x.LastWrite).ToList();

            foreach (var item in sorted)
            {
                ct.ThrowIfCancellationRequested();
                var stamp = item.LastWrite.ToString("yyyyMMdd_HHmmss");

                var isClientMarker = item.OriginalName.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase);
                var baseClean = isClientMarker ? item.OriginalName[..^12] : item.OriginalName;
                var ext = Path.GetExtension(baseClean);
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";

                var newBaseName = $"scene_{item.Scene:D2}_clip_{item.Clip:D2}_take_{take:D2}_{stamp}";
                var newMp4Name = $"{newBaseName}{ext}";
                var dir = Path.GetDirectoryName(item.FullPath)!;

                var newMp4Path = Path.Combine(dir, newMp4Name);

                // Rename file / marker if name has changed
                if (!string.Equals(item.OriginalName, newMp4Name, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (isClientMarker)
                        {
                            var targetMarker = Path.Combine(dir, $"{newMp4Name}.client.json");
                            if (!File.Exists(targetMarker))
                                await Task.Run(() => File.Move(item.FullPath, targetMarker), ct).ConfigureAwait(false);
                        }
                        else if (!File.Exists(newMp4Path))
                        {
                            await Task.Run(() => File.Move(item.FullPath, newMp4Path), ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed renaming clip {Old} → {New}", item.OriginalName, newMp4Name);
                    }
                }

                // Load prompt text if exists
                var promptPath = Path.Combine(videoDir, "prompts", $"S{item.Scene:D2}C{item.Clip:D2}.txt");
                var promptText = "";
                if (File.Exists(promptPath))
                {
                    try { promptText = await File.ReadAllTextAsync(promptPath, ct).ConfigureAwait(false); }
                    catch { /* ignore */ }
                }

                var targetFileForHash = File.Exists(newMp4Path) ? newMp4Path : item.FullPath;
                var fi = new FileInfo(targetFileForHash);
                var sha256 = File.Exists(newMp4Path) ? await MediaRegistryService.HashFileAsync(newMp4Path, ct).ConfigureAwait(false) : "";

                // Write new .clip.json sidecar with take and timestamp
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

                take++;
                convertedCount++;
            }
        }

        return convertedCount;
    }

    /// <summary>
    /// Alias for ConvertProjectClipsToNewFormatAsync for backward compatibility.
    /// </summary>
    public Task<int> EnsureAllSidecarsExistAsync(string projectDir, CancellationToken ct = default) =>
        ConvertProjectClipsToNewFormatAsync(projectDir, ct);
}
