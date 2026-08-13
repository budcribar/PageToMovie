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

        var filesToCompare = new List<(string RelPath, string Category)>
        {
            ("source/screenplay.fountain", "Screenplay"),
            ("cast_seeds.json", "Cast Seeds"),
            ("blueprint.clips.grok.json", "Shot Plan"),
            ("project_rules.json", "Rules")
        };

        // Also check any additional .fountain files in source/
        var sourceDirTarget = Path.Combine(targetDir, "source");
        if (Directory.Exists(sourceDirTarget))
        {
            foreach (var f in Directory.GetFiles(sourceDirTarget, "*.fountain"))
            {
                var rel = Path.Combine("source", Path.GetFileName(f)).Replace('\\', '/');
                if (!filesToCompare.Any(x => string.Equals(x.RelPath, rel, StringComparison.OrdinalIgnoreCase)))
                {
                    filesToCompare.Add((rel, "Screenplay"));
                }
            }
        }

        bool overallHasConflicts = false;

        foreach (var (relPath, category) in filesToCompare)
        {
            var oursFile = Path.Combine(targetDir, relPath);
            var theirsFile = Path.Combine(originDir, relPath);

            var oursExists = File.Exists(oursFile);
            var theirsExists = File.Exists(theirsFile);

            if (!oursExists && !theirsExists) continue;

            var oursContent = oursExists ? await File.ReadAllTextAsync(oursFile, ct).ConfigureAwait(false) : "";
            var theirsContent = theirsExists ? await File.ReadAllTextAsync(theirsFile, ct).ConfigureAwait(false) : "";

            string status;
            if (!oursExists) status = "deleted";
            else if (!theirsExists) status = "added";
            else if (string.Equals(oursContent, theirsContent, StringComparison.Ordinal)) status = "identical";
            else status = "modified";

            var lines = ComputeLineDiff(oursContent, theirsContent, out bool fileHasConflicts);
            if (fileHasConflicts) overallHasConflicts = true;

            result.FileDiffs.Add(new ContributionDiffItemDto
            {
                FilePath = relPath,
                Category = category,
                Status = status,
                OursContent = oursContent,
                TheirsContent = theirsContent,
                Lines = lines
            });
        }

        result.HasConflicts = overallHasConflicts;

        // Compute media clip status across target vs origin
        result.MediaClips = await ComputeMediaClipsAsync(targetDir, originDir, ct).ConfigureAwait(false);

        return result;
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
            var originDirName = Path.GetDirectoryName(originFilePath);
            if (!string.IsNullOrEmpty(originDirName))
                Directory.CreateDirectory(originDirName);

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
                        continue;
                    }
                }
                else
                {
                    result.VerifiedCount++;
                    continue;
                }
            }

            bool synced = false;

            // Path A: Try Provider CDN
            if (!string.IsNullOrWhiteSpace(clip.ProviderCdnUrl) &&
                Uri.TryCreate(clip.ProviderCdnUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
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
                                synced = true;
                            }
                        }
                        else
                        {
                            await File.WriteAllBytesAsync(originFilePath, bytes, ct).ConfigureAwait(false);
                            result.CdnDownloadCount++;
                            result.SyncedCount++;
                            synced = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "CDN download failed for clip {Path}, falling back to local proxy", clip.RelativePath);
                }
            }

            // Path B: Fallback to local target proxy copy
            if (!synced)
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
                        synced = true;
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
            }

            if (synced && _mediaRegistry is not null && !string.IsNullOrWhiteSpace(clip.Sha256))
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

        return result;
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
                {
                    int sceneIdx = 1;
                    if (scene.TryGetProperty("scene_index", out var sIdx)) sceneIdx = sIdx.GetInt32();
                    else if (scene.TryGetProperty("scene", out var sIdx2)) sceneIdx = sIdx2.GetInt32();

                    if (scene.TryGetProperty("veo_clips", out var vClips) && vClips.ValueKind == JsonValueKind.Array)
                    {
                        int cIndex = 1;
                        foreach (var clip in vClips.EnumerateArray())
                        {
                            var dto = ParseClipElement(clip, sceneIdx, cIndex++, projectDir);
                            if (dto is not null) clips.Add(dto);
                        }
                    }
                    else if (scene.TryGetProperty("clips", out var sClips) && sClips.ValueKind == JsonValueKind.Array)
                    {
                        int cIndex = 1;
                        foreach (var clip in sClips.EnumerateArray())
                        {
                            var dto = ParseClipElement(clip, sceneIdx, cIndex++, projectDir);
                            if (dto is not null) clips.Add(dto);
                        }
                    }
                }
            }
        }
        catch { /* best effort */ }

        return clips;
    }

    private static MediaClipContributionDto? ParseClipElement(JsonElement clip, int defaultScene, int defaultClip, string projectDir)
    {
        int scene = defaultScene;
        if (clip.TryGetProperty("scene_index", out var s)) scene = s.GetInt32();
        else if (clip.TryGetProperty("scene", out s)) scene = s.GetInt32();

        int clipIdx = defaultClip;
        if (clip.TryGetProperty("clip_index", out var c)) clipIdx = c.GetInt32();
        else if (clip.TryGetProperty("clip", out c)) clipIdx = c.GetInt32();

        string relPath = $"assets/video/scene_{scene:D2}_clip_{clipIdx:D2}.mp4";
        if (clip.TryGetProperty("relative_path", out var rp) && !string.IsNullOrWhiteSpace(rp.GetString()))
            relPath = rp.GetString().Replace('\\', '/');

        string? cdnUrl = null;
        if (clip.TryGetProperty("video_url", out var vu)) cdnUrl = vu.GetString();
        else if (clip.TryGetProperty("source_video_url", out var svu)) cdnUrl = svu.GetString();
        if (cdnUrl != null && !cdnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            cdnUrl = null;

        string sha = "";
        if (clip.TryGetProperty("sha256", out var sh)) sha = sh.GetString() ?? "";
        else if (clip.TryGetProperty("sha", out sh)) sha = sh.GetString() ?? "";

        long size = 0;
        var fullPath = Path.Combine(projectDir, relPath);
        if (File.Exists(fullPath))
        {
            var fi = new FileInfo(fullPath);
            size = fi.Length;
            if (string.IsNullOrWhiteSpace(sha))
            {
                try
                {
                    using var fs = File.OpenRead(fullPath);
                    var hashBytes = System.Security.Cryptography.SHA256.HashData(fs);
                    sha = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }
                catch (Exception)
                {
                    // Hash is optional metadata; leave empty if the file cannot be read.
                    sha = "";
                }
            }
        }

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
