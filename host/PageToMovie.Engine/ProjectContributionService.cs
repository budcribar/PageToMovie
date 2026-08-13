using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

public sealed class ProjectContributionService
{
    private readonly ILogger<ProjectContributionService> _logger;
    private readonly MediaRegistryService? _mediaRegistry;

    public ProjectContributionService(
        ILogger<ProjectContributionService> logger,
        MediaRegistryService? mediaRegistry = null)
    {
        _logger = logger;
        _mediaRegistry = mediaRegistry;
    }

    public async Task<ContributionDiffDto> ComputeDiffAsync(
        string projectId,
        string parentProjectId,
        string targetDir,
        string originDir,
        CancellationToken ct = default)
    {
        var result = new ContributionDiffDto
        {
            ProjectId = projectId,
            ParentProjectId = parentProjectId,
        };

        if (!Directory.Exists(targetDir) || !Directory.Exists(originDir))
        {
            _logger.LogWarning("Cannot compute diff: target or origin directory missing. Target: {Target}, Origin: {Origin}", targetDir, originDir);
            return result;
        }

        var filesToCompare = BuildFilesToCompare(targetDir);

        bool overallHasConflicts = false;

        foreach (var (relPath, category) in filesToCompare)
        {
            var item = await DiffOneFileAsync(targetDir, originDir, relPath, category, ct).ConfigureAwait(false);
            if (item is null) continue;
            if (item.Value.HasConflicts) overallHasConflicts = true;
            result.FileDiffs.Add(item.Value.Dto);
        }

        result.HasConflicts = overallHasConflicts;

        // Compute media clip status across target vs origin
        result.MediaClips = await ComputeMediaClipsAsync(targetDir, originDir, ct).ConfigureAwait(false);

        return result;
    }

    private static List<(string RelPath, string Category)> BuildFilesToCompare(string targetDir)
    {
        var filesToCompare = new List<(string RelPath, string Category)>
        {
            ("source/screenplay.fountain", "Screenplay"),
            ("cast_seeds.json", "Cast Seeds"),
            ("blueprint.clips.grok.json", "Shot Plan"),
            ("project_rules.json", "Rules")
        };

        // Also check any additional .fountain files in source/
        var sourceDirTarget = Path.Combine(targetDir, "source");
        if (!Directory.Exists(sourceDirTarget))
            return filesToCompare;

        foreach (var f in Directory.GetFiles(sourceDirTarget, "*.fountain"))
        {
            var rel = Path.Combine("source", Path.GetFileName(f)).Replace('\\', '/');
            if (!filesToCompare.Any(x => string.Equals(x.RelPath, rel, StringComparison.OrdinalIgnoreCase)))
                filesToCompare.Add((rel, "Screenplay"));
        }

        return filesToCompare;
    }

    private static async Task<(ContributionDiffItemDto Dto, bool HasConflicts)?> DiffOneFileAsync(
        string targetDir, string originDir, string relPath, string category, CancellationToken ct)
    {
        var oursFile = Path.Combine(targetDir, relPath);
        var theirsFile = Path.Combine(originDir, relPath);

        var oursExists = File.Exists(oursFile);
        var theirsExists = File.Exists(theirsFile);

        if (!oursExists && !theirsExists) return null;

        var oursContent = oursExists ? await File.ReadAllTextAsync(oursFile, ct).ConfigureAwait(false) : "";
        var theirsContent = theirsExists ? await File.ReadAllTextAsync(theirsFile, ct).ConfigureAwait(false) : "";

        var status = ResolveDiffStatus(oursExists, theirsExists, oursContent, theirsContent);
        var lines = ComputeLineDiff(oursContent, theirsContent, out bool fileHasConflicts);

        var dto = new ContributionDiffItemDto
        {
            FilePath = relPath,
            Category = category,
            Status = status,
            OursContent = oursContent,
            TheirsContent = theirsContent,
            Lines = lines
        };
        return (dto, fileHasConflicts);
    }

    private static string ResolveDiffStatus(bool oursExists, bool theirsExists, string oursContent, string theirsContent)
    {
        if (!oursExists) return "deleted";
        if (!theirsExists) return "added";
        if (string.Equals(oursContent, theirsContent, StringComparison.Ordinal)) return "identical";
        return "modified";
    }

    public async Task<MediaSyncResultDto> SyncContributionMediaAsync(
        string targetDir,
        string originDir,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var result = new MediaSyncResultDto();
        if (!Directory.Exists(targetDir) || !Directory.Exists(originDir))
        {
            result.Errors.Add("Target or origin directory missing.");
            return result;
        }

        var targetBlueprint = Path.Combine(targetDir, "blueprint.clips.grok.json");
        var targetClips = await ExtractClipsFromBlueprintAsync(targetBlueprint, targetDir, ct).ConfigureAwait(false);

        using var clientOwned = httpClient is null ? new HttpClient() : null;
        var http = httpClient ?? clientOwned;

        foreach (var clip in targetClips)
        {
            ct.ThrowIfCancellationRequested();
            var originFilePath = Path.Combine(originDir, clip.RelativePath);
            EnsureParentDirectory(originFilePath);

            if (await OriginAlreadyVerifiedAsync(originFilePath, clip, result, ct).ConfigureAwait(false))
                continue;

            var synced = await TryDownloadClipFromCdnAsync(clip, originFilePath, http!, result, ct).ConfigureAwait(false);

            // Path B: Fallback to local target proxy copy
            if (!synced)
                synced = await TryCopyClipFromTargetAsync(clip, targetDir, originFilePath, result, ct).ConfigureAwait(false);

            if (synced)
                await TryUpsertMediaRegistryAsync(clip, originFilePath, originDir, ct).ConfigureAwait(false);
        }

        return result;
    }

    private static void EnsureParentDirectory(string originFilePath)
    {
        var originDirName = Path.GetDirectoryName(originFilePath);
        if (!string.IsNullOrEmpty(originDirName))
            Directory.CreateDirectory(originDirName);
    }

    private static async Task<bool> OriginAlreadyVerifiedAsync(
        string originFilePath,
        MediaClipContributionDto clip,
        MediaSyncResultDto result,
        CancellationToken ct)
    {
        bool originExists = File.Exists(originFilePath);
        if (originExists)
        {
            if (!string.IsNullOrWhiteSpace(clip.Sha256))
            {
                var hashMatches = false;
                try
                {
                    var existingHash = await MediaRegistryService.HashFileAsync(originFilePath, ct).ConfigureAwait(false);
                    hashMatches = string.Equals(existingHash, clip.Sha256, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    // Unreadable origin file is treated as a mismatch so we re-copy.
                    hashMatches = false;
                }
                if (hashMatches)
                {
                    result.VerifiedCount++;
                    return true;
                }
            }
            else
            {
                result.VerifiedCount++;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryDownloadClipFromCdnAsync(
        MediaClipContributionDto clip,
        string originFilePath,
        HttpClient http,
        MediaSyncResultDto result,
        CancellationToken ct)
    {
        // Path A: Try Provider CDN
        if (!string.IsNullOrWhiteSpace(clip.ProviderCdnUrl) &&
            Uri.TryCreate(clip.ProviderCdnUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
                return await TryWriteCdnBytesIfValidAsync(clip, originFilePath, bytes, result, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "CDN download failed for clip {Path}, falling back to local proxy", clip.RelativePath);
            }
        }

        return false;
    }

    private static async Task<bool> TryWriteCdnBytesIfValidAsync(
        MediaClipContributionDto clip,
        string originFilePath,
        byte[] bytes,
        MediaSyncResultDto result,
        CancellationToken ct)
    {
        if (bytes.Length > 0)
        {
            if (!string.IsNullOrWhiteSpace(clip.Sha256))
            {
                var downloadedHash = MediaRegistryService.HashBytes(bytes);
                if (string.Equals(downloadedHash, clip.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await File.WriteAllBytesAsync(originFilePath, bytes, ct).ConfigureAwait(false);
                    result.CdnDownloadCount++;
                    result.SyncedCount++;
                    result.VerifiedCount++;
                    return true;
                }
            }
            else
            {
                await File.WriteAllBytesAsync(originFilePath, bytes, ct).ConfigureAwait(false);
                result.CdnDownloadCount++;
                result.SyncedCount++;
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> TryCopyClipFromTargetAsync(
        MediaClipContributionDto clip,
        string targetDir,
        string originFilePath,
        MediaSyncResultDto result,
        CancellationToken ct)
    {
        var targetFilePath = Path.Combine(targetDir, clip.RelativePath);
        if (File.Exists(targetFilePath))
        {
            try
            {
                File.Copy(targetFilePath, originFilePath, overwrite: true);
                result.LocalCopyCount++;
                result.SyncedCount++;

                if (!string.IsNullOrWhiteSpace(clip.Sha256))
                {
                    var copiedHash = await MediaRegistryService.HashFileAsync(originFilePath, ct).ConfigureAwait(false);
                    if (string.Equals(copiedHash, clip.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        result.VerifiedCount++;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to copy local clip {clip.RelativePath}: {ex.Message}");
            }
        }
        else
        {
            result.Errors.Add($"Clip file {clip.RelativePath} not found in target or CDN.");
        }

        return false;
    }

    private async Task TryUpsertMediaRegistryAsync(
        MediaClipContributionDto clip,
        string originFilePath,
        string originDir,
        CancellationToken ct)
    {
        if (_mediaRegistry is not null && !string.IsNullOrWhiteSpace(clip.Sha256))
        {
            try
            {
                var fi = new FileInfo(originFilePath);
                var originProjId = Path.GetFileName(originDir);
                await _mediaRegistry.UpsertAsync(
                    originProjId,
                    clip.RelativePath,
                    clip.Sha256,
                    fi.Length,
                    "clip",
                    clip.SceneIndex,
                    clip.ClipIndex,
                    userId: null,
                    ct: ct).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
    }

    private static async Task<List<MediaClipContributionDto>> ComputeMediaClipsAsync(string targetDir, string originDir, CancellationToken ct = default)
    {
        var targetBlueprint = Path.Combine(targetDir, "blueprint.clips.grok.json");
        var clips = await ExtractClipsFromBlueprintAsync(targetBlueprint, targetDir, ct).ConfigureAwait(false);

        foreach (var clip in clips)
        {
            var originPath = Path.Combine(originDir, clip.RelativePath);
            var targetPath = Path.Combine(targetDir, clip.RelativePath);

            bool inOrigin = File.Exists(originPath);
            bool inTarget = File.Exists(targetPath);

            if (inOrigin)
            {
                clip.Status = "Present";
                clip.IsVerified = true;
            }
            else if (!string.IsNullOrWhiteSpace(clip.ProviderCdnUrl))
            {
                clip.Status = "CdnAvailable";
            }
            else if (inTarget)
            {
                clip.Status = "ProxyNeeded";
            }
            else
            {
                clip.Status = "Missing";
            }
        }

        return clips;
    }

    private static async Task<List<MediaClipContributionDto>> ExtractClipsFromBlueprintAsync(string blueprintPath, string projectDir, CancellationToken ct = default)
    {
        var clips = new List<MediaClipContributionDto>();
        if (!File.Exists(blueprintPath)) return clips;

        try
        {
            var json = await File.ReadAllTextAsync(blueprintPath, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
            {
                foreach (var scene in scenes.EnumerateArray())
                    AppendClipsFromScene(scene, projectDir, clips);
            }
        }
        catch { /* best effort */ }

        return clips;
    }

    private static void AppendClipsFromScene(JsonElement scene, string projectDir, List<MediaClipContributionDto> clips)
    {
        int sceneIdx = ReadFirstProperty(scene, 1, static e => e.GetInt32(), "scene_index", "scene");

        if (scene.TryGetProperty("veo_clips", out var vClips) && vClips.ValueKind == JsonValueKind.Array)
            AppendClipsFromJsonArray(vClips, sceneIdx, projectDir, clips);
        else if (scene.TryGetProperty("clips", out var sClips) && sClips.ValueKind == JsonValueKind.Array)
            AppendClipsFromJsonArray(sClips, sceneIdx, projectDir, clips);
    }

    private static void AppendClipsFromJsonArray(JsonElement clipsArray, int sceneIdx, string projectDir, List<MediaClipContributionDto> clips)
    {
        int cIndex = 1;
        foreach (var clip in clipsArray.EnumerateArray())
        {
            var dto = ParseClipElement(clip, sceneIdx, cIndex++, projectDir);
            if (dto is not null) clips.Add(dto);
        }
    }

    private static MediaClipContributionDto? ParseClipElement(JsonElement clip, int defaultScene, int defaultClip, string projectDir)
    {
        int scene = ReadFirstProperty(clip, defaultScene, static e => e.GetInt32(), "scene_index", "scene");
        int clipIdx = ReadFirstProperty(clip, defaultClip, static e => e.GetInt32(), "clip_index", "clip");
        string relPath = ReadRelativeVideoPath(clip, scene, clipIdx);
        string? cdnUrl = ReadHttpUrl(clip, "video_url", "source_video_url");
        var (size, sha) = FileSizeAndSha(
            projectDir,
            relPath,
            ReadFirstProperty(clip, "", static e => e.GetString() ?? "", "sha256", "sha"));

        return new MediaClipContributionDto
        {
            SceneIndex = scene,
            ClipIndex = clipIdx,
            RelativePath = relPath,
            Sha256 = sha,
            SizeBytes = size,
            ProviderCdnUrl = cdnUrl,
        };
    }

    private static T ReadFirstProperty<T>(
        JsonElement clip,
        T fallback,
        Func<JsonElement, T> read,
        string primary,
        string secondary)
    {
        if (clip.TryGetProperty(primary, out var el)) return read(el);
        if (clip.TryGetProperty(secondary, out el)) return read(el);
        return fallback;
    }

    private static string ReadRelativeVideoPath(JsonElement clip, int scene, int clipIdx)
    {
        var relPath = $"assets/video/scene_{scene:D2}_clip_{clipIdx:D2}.mp4";
        if (!clip.TryGetProperty("relative_path", out var rp))
            return relPath;
        var raw = rp.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return relPath;
        return raw.Replace('\\', '/');
    }

    private static string? ReadHttpUrl(JsonElement clip, string primary, string secondary)
    {
        var url = ReadFirstProperty<string?>(clip, null, static e => e.GetString(), primary, secondary);
        if (url != null && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;
        return url;
    }

    private static (long Size, string Sha) FileSizeAndSha(string projectDir, string relPath, string sha)
    {
        var fullPath = Path.Combine(projectDir, relPath);
        if (!File.Exists(fullPath))
            return (0, sha);
        var size = new FileInfo(fullPath).Length;
        if (!string.IsNullOrWhiteSpace(sha))
            return (size, sha);
        return (size, HashFileSha256OrEmpty(fullPath));
    }

    private static string HashFileSha256OrEmpty(string fullPath)
    {
        try
        {
            using var fs = File.OpenRead(fullPath);
            var hashBytes = System.Security.Cryptography.SHA256.HashData(fs);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception)
        {
            // Hash is optional metadata; leave empty if the file cannot be read.
            return "";
        }
    }

    private static List<DiffLineDto> ComputeLineDiff(string ours, string theirs, out bool hasConflicts)
    {
        hasConflicts = false;
        var oursLines = (ours ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var theirsLines = (theirs ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var result = new List<DiffLineDto>();
        int i = 0, j = 0;
        int lineOurs = 1, lineTheirs = 1;

        while (i < oursLines.Length || j < theirsLines.Length)
        {
            if (i < oursLines.Length && j < theirsLines.Length && string.Equals(oursLines[i], theirsLines[j], StringComparison.Ordinal))
            {
                result.Add(new DiffLineDto
                {
                    Kind = "unchanged",
                    LineNumberOurs = lineOurs++,
                    LineNumberTheirs = lineTheirs++,
                    Content = oursLines[i]
                });
                i++;
                j++;
            }
            else if (i < oursLines.Length && (j >= theirsLines.Length || !theirsLines.Contains(oursLines[i])))
            {
                result.Add(new DiffLineDto
                {
                    Kind = "added",
                    LineNumberOurs = lineOurs++,
                    LineNumberTheirs = null,
                    Content = oursLines[i]
                });
                i++;
            }
            else if (j < theirsLines.Length)
            {
                result.Add(new DiffLineDto
                {
                    Kind = "deleted",
                    LineNumberOurs = null,
                    LineNumberTheirs = lineTheirs++,
                    Content = theirsLines[j]
                });
                j++;
            }
        }

        return result;
    }
}
