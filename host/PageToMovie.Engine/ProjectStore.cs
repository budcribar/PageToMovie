using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Fountain;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

public sealed partial class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;

    /// <summary>CA1861: avoid allocating split separator arrays on every book-text sample.</summary>
    private static readonly char[] WordSplitChars = { ' ', '\n', '\r', '\t' };

    /// <summary>
    /// Read a project meta json file (project.json) into a case-insensitive dictionary,
    /// returning an empty case-insensitive dictionary if the file is missing or unparseable.
    /// </summary>
    private static async Task<Dictionary<string, object?>> ReadMetaOrEmptyAsync(string metaPath, CancellationToken ct)
    {
        if (File.Exists(metaPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOpts)
                       ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        }
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Locate the character-seed JSON object for <paramref name="charKey"/> (case-insensitive)
    /// within a character_seed_tokens object, returning the matched seed and its actual key,
    /// or (null, null) if no matching object-valued seed is present.
    /// </summary>
    private static (System.Text.Json.Nodes.JsonObject? seed, string? foundKey) FindSeedByCharKey(
        System.Text.Json.Nodes.JsonObject seeds, string charKey)
    {
        foreach (var (k, v) in seeds)
        {
            if (string.Equals(k, charKey, StringComparison.OrdinalIgnoreCase) &&
                v is System.Text.Json.Nodes.JsonObject jo)
            {
                return (jo, k);
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Load a cast_seeds / blueprint file, locate its character_seed_tokens object (direct or
    /// nested under global_production_variables), apply <paramref name="patchSeeds"/>, and write
    /// the result back. No-op (non-fatal) when the file is missing/unparseable or has no seeds.
    /// </summary>
    private static void PatchCharacterSeedsFile(string path, Action<System.Text.Json.Nodes.JsonObject> patchSeeds)
    {
        try
        {
            if (!File.Exists(path)) return;
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                       as System.Text.Json.Nodes.JsonObject;
            if (root is null) return;
            System.Text.Json.Nodes.JsonObject? seeds = null;
            if (root["character_seed_tokens"] is System.Text.Json.Nodes.JsonObject direct)
                seeds = direct;
            else if (root["global_production_variables"] is System.Text.Json.Nodes.JsonObject gpv &&
                     gpv["character_seed_tokens"] is System.Text.Json.Nodes.JsonObject nested)
                seeds = nested;
            if (seeds is null) return;
            patchSeeds(seeds);
            File.WriteAllText(path, root.ToJsonString(JsonDefaults.Indented) + "\n");
        }
        catch { /* non-fatal */ }
    }

    private readonly PageToMovieOptions _opts;
    private readonly MediaDurationProbe? _duration;
    private readonly SceneListCache? _sceneListCache;
    private readonly ProjectAutoGitService? _autoGit;
    private readonly ProjectReadCache _readCache;
    // Written only via ClipDialogueVerificationService.SaveVerificationAsync's atomic
    // write-then-rename — mtime/length alone is self-correcting, no external writer to
    // coordinate a read against.
    private readonly MtimeValidatedFileCache<ClipDialogueVerificationResult, NoOpSemaphore> _dialogueVerificationCache = new();
    private readonly IUserApiKeyProvider? _keyProvider;
    private readonly MediaRegistryService? _mediaRegistry;
    private readonly string _workspaceRoot;
    private string _activeProjectId = "";

    public ProjectStore(
        IOptions<PageToMovieOptions> opts,
        MediaDurationProbe? duration = null,
        SceneListCache? sceneListCache = null,
        ProjectReadCache? readCache = null,
        IUserApiKeyProvider? keyProvider = null,
        ProjectAutoGitService? autoGit = null,
        MediaRegistryService? mediaRegistry = null)
    {
        _opts = opts.Value;
        _duration = duration;
        _keyProvider = keyProvider;
        _autoGit = autoGit;
        _mediaRegistry = mediaRegistry;
        // A/B: PageToMovie__EnableReadCaches=false disables scene-list + project/blueprint/dir caches
        _sceneListCache = _opts.EnableReadCaches ? sceneListCache : null;
        _readCache = readCache ?? new ProjectReadCache();
        _readCache.Enabled = _opts.EnableReadCaches;
        _workspaceRoot = ResolveWorkspaceRoot();
        var ws = Path.Combine(_workspaceRoot, "projects", "workspace.json");
        if (File.Exists(ws))
        {
            try
            {
                var state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(ws), JsonOpts);
                _activeProjectId = state?.ActiveProject ?? "";
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Trigger non-blocking background Git commit &amp; push for a project change.</summary>
    public void TriggerAutoGitCommit(string projectId, string message, string? author = null)
    {
        if (_autoGit is null || string.IsNullOrWhiteSpace(projectId)) return;
        try
        {
            var dir = GetProjectDir(projectId);
            _autoGit.QueueCommitAndPush(dir, projectId, message, author);
        }
        catch { /* non-fatal background hook */ }
    }

    /// <summary>Undoes the last committed change in a project repository (reverts to HEAD~1).</summary>
    public async Task<GitCommitInfo?> UndoLastProjectChangeAsync(string projectId, string? author = null, ProjectGitRepositoryService? gitRepo = null)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return null;

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var who = string.IsNullOrWhiteSpace(author) ? "Operator" : author;
        var result = await git.UndoLastCommitAsync(dir, who).ConfigureAwait(false);
        if (result is not null)
        {
            InvalidateSceneListCache(projectId);
        }
        return result;
    }

    /// <summary>Reverts project state to a specific Git commit hash.</summary>
    public async Task<GitCommitInfo?> RevertProjectToCommitAsync(string projectId, string commitHash, string? author = null, ProjectGitRepositoryService? gitRepo = null)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(commitHash)) return null;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return null;

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var who = string.IsNullOrWhiteSpace(author) ? "Operator" : author;
        var result = await git.RevertToCommitAsync(dir, commitHash, who).ConfigureAwait(false);
        if (result is not null)
        {
            InvalidateSceneListCache(projectId);
        }
        return result;
    }

    /// <summary>Gets recent Git history for a project.</summary>
    public ProjectGitStatus GetProjectGitStatus(string projectId, ProjectGitRepositoryService? gitRepo = null)
    {
        var dir = GetProjectDir(projectId);
        var git = gitRepo ?? new ProjectGitRepositoryService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        return git.GetStatus(dir, projectId);
    }

    public async Task<IReadOnlyList<GitCommitInfo>> GetProjectGitHistoryAsync(string projectId, int limit = 20, ProjectGitRepositoryService? gitRepo = null)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Array.Empty<GitCommitInfo>();
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return Array.Empty<GitCommitInfo>();

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        return await git.GetCommitHistoryAsync(dir, limit).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets scene-specific Git commit history and change diff details for a scene.
    /// </summary>
    public async Task<IReadOnlyList<SceneCommitHistoryItem>> GetSceneGitHistoryAsync(
        string projectId,
        int sceneNumber,
        int limit = 20,
        ProjectGitRepositoryService? gitRepo = null)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Array.Empty<SceneCommitHistoryItem>();
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return Array.Empty<SceneCommitHistoryItem>();

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var commits = await git.GetCommitHistoryAsync(dir, limit).ConfigureAwait(false);
        if (commits.Count == 0) return Array.Empty<SceneCommitHistoryItem>();

        var bpName = "blueprint.clips.grok.json";
        var result = new List<SceneCommitHistoryItem>();

        for (int i = 0; i < commits.Count; i++)
        {
            var c = commits[i];
            var historicalBp = git.GetFileContentAtCommit(dir, c.CommitHash, bpName);
            if (string.IsNullOrWhiteSpace(historicalBp)) continue;

            string? parentBp = null;
            if (i + 1 < commits.Count)
            {
                parentBp = git.GetFileContentAtCommit(dir, commits[i + 1].CommitHash, bpName);
            }

            var changes = CompareSceneInBlueprints(historicalBp, parentBp, sceneNumber);
            if (changes.Count > 0 || i == 0)
            {
                result.Add(new SceneCommitHistoryItem
                {
                    CommitHash = c.CommitHash,
                    Author = c.Author,
                    Message = c.Message,
                    CommittedAt = c.CommittedAt,
                    Changes = changes.Count > 0 ? changes : new List<string> { "Initial scene snapshot" },
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Reverts only the specified scene (and its clips) back to how it was in target commit.
    /// </summary>
    public async Task<bool> RevertSceneToCommitAsync(
        string projectId,
        int sceneNumber,
        string commitHash,
        string? author = null,
        ProjectGitRepositoryService? gitRepo = null)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(commitHash)) return false;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return false;

        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is null || !File.Exists(bpPath)) return false;

        var bpName = Path.GetFileName(bpPath);
        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);

        var historicalBpStr = git.GetFileContentAtCommit(dir, commitHash, bpName);
        if (string.IsNullOrWhiteSpace(historicalBpStr)) return false;

        try
        {
            var currentRoot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(bpPath)) as System.Text.Json.Nodes.JsonObject;
            var historicalRoot = System.Text.Json.Nodes.JsonNode.Parse(historicalBpStr) as System.Text.Json.Nodes.JsonObject;
            if (currentRoot is null || historicalRoot is null) return false;

            var currentScenes = currentRoot["scenes"] as System.Text.Json.Nodes.JsonArray;
            var historicalScenes = historicalRoot["scenes"] as System.Text.Json.Nodes.JsonArray;
            if (currentScenes is null || historicalScenes is null) return false;

            System.Text.Json.Nodes.JsonObject? targetHistSceneNode = null;
            foreach (var hNode in historicalScenes)
            {
                if (hNode is System.Text.Json.Nodes.JsonObject hObj && ReadJsonNodeInt(hObj["scene_number"]) == sceneNumber)
                {
                    targetHistSceneNode = hObj.DeepClone() as System.Text.Json.Nodes.JsonObject;
                    break;
                }
            }

            if (targetHistSceneNode is null) return false;

            int targetIndex = -1;
            for (int i = 0; i < currentScenes.Count; i++)
            {
                if (currentScenes[i] is System.Text.Json.Nodes.JsonObject cObj && ReadJsonNodeInt(cObj["scene_number"]) == sceneNumber)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex >= 0)
            {
                currentScenes[targetIndex] = targetHistSceneNode;
            }
            else
            {
                currentScenes.Add(targetHistSceneNode);
            }

            File.WriteAllText(bpPath, currentRoot.ToJsonString(JsonOpts));
            InvalidateSceneListCache(projectId);

            var who = string.IsNullOrWhiteSpace(author) ? "Operator" : author;
            TriggerAutoGitCommit(projectId, $"Reverted Scene {sceneNumber} to commit {commitHash[..Math.Min(8, commitHash.Length)]}", who);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Package Git status: HEAD tip + uncommitted scene/clip summary for Home "Last saved".
    /// </summary>
    public Task<UncommittedStatusDto> GetProjectUncommittedStatusAsync(string projectId, ProjectGitRepositoryService? gitRepo = null)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Task.FromResult(new UncommittedStatusDto());
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return Task.FromResult(new UncommittedStatusDto());

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var head = git.GetStatus(dir, projectId);
        var dto = new UncommittedStatusDto
        {
            GitAvailable = head.Available,
            SkipReason = head.SkipReason,
            RemoteConfigured = head.RemoteConfigured,
            LastCommitHash = head.LastCommitHash,
            LastCommitMessage = head.LastCommitMessage,
            LastCommitAuthor = head.LastCommitAuthor,
            LastCommitAtUtc = head.LastCommitAtUtc,
            HistoryUrl = head.HistoryUrl,
            HasUncommittedChanges = head.HasUncommittedChanges,
        };

        if (!head.Available || !head.HasUncommittedChanges)
        {
            if (head.Available && !string.IsNullOrWhiteSpace(head.LastCommitHash))
                dto.Summary = "Package up to date with last save.";
            else if (!head.Available)
                dto.Summary = head.SkipReason ?? "Package history not available.";
            return Task.FromResult(dto);
        }

        var (_, files) = git.GetUncommittedStatus(dir);
        var modScenes = new HashSet<int>();
        var modClips = new HashSet<string>();

        foreach (var f in files)
        {
            var m = System.Text.RegularExpressions.Regex.Match(f, @"scene_?(\d+)(?:_clip_?(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var s))
            {
                modScenes.Add(s);
                if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var c))
                {
                    modClips.Add($"{s}-{c}");
                }
            }
            else if (f.EndsWith("blueprint.clips.grok.json", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("scenes.json", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("screenplay.fountain", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("cast_seeds.json", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("project.json", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("pipeline_config.json", StringComparison.OrdinalIgnoreCase))
            {
                // Text package change without scene token in path
            }
        }

        dto.ModifiedScenes = modScenes.OrderBy(n => n).ToList();
        dto.ModifiedClipKeys = modClips.ToList();
        dto.Summary = dto.ModifiedScenes.Count > 0
            ? $"{dto.ModifiedScenes.Count} scene(s) modified since last save."
            : "Package has uncommitted changes.";
        return Task.FromResult(dto);
    }

    /// <summary>
    /// Manually commits uncommitted working directory changes.
    /// </summary>
    public async Task<GitCommitInfo?> CommitProjectChangesAsync(
        string projectId, string message, string? author = null, ProjectGitRepositoryService? gitRepo = null,
        bool forceCommit = false)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return null;

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var who = string.IsNullOrWhiteSpace(author) ? "Operator" : author;
        var msg = string.IsNullOrWhiteSpace(message) ? "Manual scene/clip updates" : message.Trim();
        var result = await git.CommitProjectStateAsync(dir, who, msg, forceCommit).ConfigureAwait(false);
        InvalidateSceneListCache(projectId);
        return result;
    }

    /// <summary>
    /// Lists all historical video versions/takes for a clip for comparison.
    /// </summary>
    public async Task<IReadOnlyList<ClipVersionItem>> GetClipVersionsAsync(string projectId, int scene, int clip)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Array.Empty<ClipVersionItem>();
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return Array.Empty<ClipVersionItem>();

        var videoDir = Path.Combine(dir, "assets", "video");
        if (!Directory.Exists(videoDir)) return Array.Empty<ClipVersionItem>();

        var result = new List<ClipVersionItem>();
        var prefix = $"scene_{scene:D2}_clip_{clip:D2}";

        var activeMp4 = Path.Combine(videoDir, $"{prefix}.mp4");
        var activeSidecar = Path.Combine(videoDir, $"{prefix}.clip.json");
        if (File.Exists(activeMp4))
        {
            var fi = new FileInfo(activeMp4);
            var item = ParseClipSidecarOrMeta(activeSidecar, activeMp4, scene, clip, take: 1, isCurrent: true, fi.LastWriteTimeUtc);
            result.Add(item);
        }

        var searchDirs = new[] { videoDir, Path.Combine(videoDir, "history") };
        foreach (var sDir in searchDirs)
        {
            if (!Directory.Exists(sDir)) continue;
            foreach (var mp4 in Directory.EnumerateFiles(sDir, $"{prefix}*.mp4"))
            {
                if (string.Equals(mp4, activeMp4, StringComparison.OrdinalIgnoreCase)) continue;
                var sidecar = Path.ChangeExtension(mp4, ".clip.json");
                if (!File.Exists(sidecar)) sidecar = Path.ChangeExtension(mp4, ".meta.json");
                var fi = new FileInfo(mp4);
                var item = ParseClipSidecarOrMeta(sidecar, mp4, scene, clip, take: result.Count + 1, isCurrent: false, fi.LastWriteTimeUtc);
                result.Add(item);
            }
        }

        // Takes that synced to the client and were pruned server-side (client-storage is the primary
        // path — see ServerMediaPruningService) have no bytes left to scan for above, only a
        // MediaRegistryService row. Without this, a clip the UI correctly shows as "on disk" (via
        // ClipOnDisk's marker check) would list zero versions here — "Takes (2)" but an empty compare
        // modal. Merge in registry rows the physical scan didn't already find.
        if (_mediaRegistry is not null)
        {
            var registered = await _mediaRegistry.ListForClipAsync(projectId, scene, clip).ConfigureAwait(false);
            if (registered.Count > 0)
            {
                var activeRelPath = MediaRegistryService.ClipRelativePath(scene, clip);
                var hasPhysicalActive = File.Exists(activeMp4);
                var knownFileNames = new HashSet<string>(result.Select(r => r.Mp4FileName), StringComparer.OrdinalIgnoreCase);

                foreach (var reg in registered)
                {
                    var fileName = Path.GetFileName(reg.RelativePath);
                    if (knownFileNames.Contains(fileName)) continue;

                    var isActive = !hasPhysicalActive &&
                        string.Equals(reg.RelativePath, activeRelPath, StringComparison.OrdinalIgnoreCase);
                    var createdUtc = DateTimeOffset.TryParse(
                        reg.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
                        ? dto.UtcDateTime
                        : DateTime.UtcNow;

                    result.Add(new ClipVersionItem
                    {
                        VersionId = fileName,
                        Scene = scene,
                        Clip = clip,
                        Take = isActive ? 1 : result.Count + 1,
                        IsCurrent = isActive,
                        CreatedAtUtc = createdUtc,
                        Mp4FileName = fileName,
                        Sha256 = reg.Sha256,
                        ClientOnly = true,
                        RelativePath = reg.RelativePath,
                    });
                    knownFileNames.Add(fileName);
                }
            }
        }

        return await Task.FromResult(result.OrderByDescending(x => x.CreatedAtUtc).ToList()).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes a historical clip version/take to be the active clip for that scene.
    /// </summary>
    public async Task<bool> PromoteClipVersionAsync(string projectId, int scene, int clip, string versionId, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId)) return false;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return false;

        var versions = await GetClipVersionsAsync(projectId, scene, clip).ConfigureAwait(false);
        var target = versions.FirstOrDefault(v => string.Equals(v.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsCurrent) return false;

        var videoDir = Path.Combine(dir, "assets", "video");
        var targetMp4Path = Path.Combine(videoDir, target.Mp4FileName);
        if (!File.Exists(targetMp4Path))
        {
            targetMp4Path = Path.Combine(videoDir, "history", target.Mp4FileName);
        }

        if (!File.Exists(targetMp4Path)) return false;

        var activeMp4Path = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");

        if (File.Exists(activeMp4Path))
        {
            var historyDir = Path.Combine(videoDir, "history");
            Directory.CreateDirectory(historyDir);
            var archiveStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var archiveMp4 = Path.Combine(historyDir, $"scene_{scene:D2}_clip_{clip:D2}_{archiveStamp}.mp4");
            try { File.Copy(activeMp4Path, archiveMp4, overwrite: true); } catch { }
        }

        File.Copy(targetMp4Path, activeMp4Path, overwrite: true);

        if (!string.IsNullOrWhiteSpace(target.VisualPrompt))
        {
            try { UpdateClipVisualPrompt(projectId, scene, clip, target.VisualPrompt); } catch { }
        }

        InvalidateSceneListCache(projectId);
        var who = string.IsNullOrWhiteSpace(author) ? "Operator" : author;
        TriggerAutoGitCommit(projectId, $"Restored clip S{scene:D2}C{clip:D2} to version {target.Mp4FileName}", who);
        return true;
    }

    /// <summary>
    /// Archives the current active clip into assets/video/history/ (same convention
    /// <see cref="PromoteClipVersionAsync"/> uses) then writes <paramref name="newBytes"/> as the
    /// new active clip — for a video-edit result, which is fresh bytes rather than an existing take
    /// to restore. Returns the active file name (always the un-suffixed
    /// <c>scene_XX_clip_XX.mp4</c> — the archived copy gets the timestamp suffix, not the new one).
    /// </summary>
    public string ArchiveActiveAndReplaceClipBytesAsync(string projectId, int scene, int clip, byte[] newBytes)
    {
        var dir = GetProjectDir(projectId);
        var videoDir = Path.Combine(dir, "assets", "video");
        Directory.CreateDirectory(videoDir);
        var activeMp4Path = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");

        if (File.Exists(activeMp4Path))
        {
            var historyDir = Path.Combine(videoDir, "history");
            Directory.CreateDirectory(historyDir);
            var archiveStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var archiveMp4 = Path.Combine(historyDir, $"scene_{scene:D2}_clip_{clip:D2}_{archiveStamp}.mp4");
            try { File.Copy(activeMp4Path, archiveMp4, overwrite: true); } catch { /* best effort, matches PromoteClipVersionAsync */ }
            var activeSidecar = Path.ChangeExtension(activeMp4Path, ".clip.json");
            if (File.Exists(activeSidecar))
            {
                var archiveSidecar = Path.ChangeExtension(archiveMp4, ".clip.json");
                try { File.Copy(activeSidecar, archiveSidecar, overwrite: true); } catch { /* best effort */ }
            }
        }

        File.WriteAllBytes(activeMp4Path, newBytes);
        InvalidateSceneListCache(projectId);
        return Path.GetFileName(activeMp4Path);
    }

    private static ClipVersionItem ParseClipSidecarOrMeta(string sidecarPath, string mp4Path, int scene, int clip, int take, bool isCurrent, DateTime lastWriteUtc)
    {
        var item = new ClipVersionItem
        {
            VersionId = Path.GetFileName(mp4Path),
            Scene = scene,
            Clip = clip,
            Take = take,
            IsCurrent = isCurrent,
            CreatedAtUtc = lastWriteUtc,
            Mp4FileName = Path.GetFileName(mp4Path),
        };

        if (File.Exists(sidecarPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecarPath));
                var root = doc.RootElement;
                item.VisualPrompt = root.TryGetProperty("visual_prompt", out var vp) ? vp.GetString() ?? "" : root.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                item.ScriptText = root.TryGetProperty("script_text", out var st) ? st.GetString() ?? "" : "";
                item.Model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                item.Resolution = root.TryGetProperty("resolution", out var r) ? r.GetString() ?? "" : "";
                if (root.TryGetProperty("duration_seconds", out var d) && d.TryGetDouble(out var dur)) item.DurationSeconds = dur;
                if (root.TryGetProperty("sha256", out var sha)) item.Sha256 = sha.GetString() ?? "";
                if (root.TryGetProperty("edited_from_take", out var eft) && eft.TryGetInt32(out var eftVal)) item.EditedFromTake = eftVal;
                if (root.TryGetProperty("source_file_id", out var sfid)) item.SourceFileId = sfid.GetString();
                if (root.TryGetProperty("source_file_expires_at", out var sfexp) && sfexp.TryGetInt64(out var sfexpVal)) item.SourceFileExpiresAtUnixSeconds = sfexpVal;
            }
            catch { /* best effort sidecar parse */ }
        }

        return item;
    }

    /// <summary>
    /// Soft-deletes a take version by moving its .mp4 and sidecar files into assets/video/.trash/
    /// </summary>
    public async Task<bool> SoftDeleteClipVersionAsync(string projectId, int scene, int clip, string versionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId)) return false;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return false;

        var videoDir = Path.Combine(dir, "assets", "video");
        var versions = await GetClipVersionsAsync(projectId, scene, clip).ConfigureAwait(false);
        var target = versions.FirstOrDefault(v => string.Equals(v.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsCurrent) return false;

        var targetMp4 = Path.Combine(videoDir, target.Mp4FileName);
        if (!File.Exists(targetMp4))
        {
            targetMp4 = Path.Combine(videoDir, "history", target.Mp4FileName);
        }
        if (!File.Exists(targetMp4)) return false;

        var trashDir = Path.Combine(videoDir, ".trash");
        Directory.CreateDirectory(trashDir);

        var trashMp4 = Path.Combine(trashDir, target.Mp4FileName);
        File.Move(targetMp4, trashMp4, overwrite: true);

        var sidecar = Path.ChangeExtension(targetMp4, ".clip.json");
        if (File.Exists(sidecar))
        {
            var trashSidecar = Path.Combine(trashDir, Path.GetFileName(sidecar));
            try { File.Move(sidecar, trashSidecar, overwrite: true); } catch { }
        }

        var meta = Path.ChangeExtension(targetMp4, ".meta.json");
        if (File.Exists(meta))
        {
            var trashMeta = Path.Combine(trashDir, Path.GetFileName(meta));
            try { File.Move(meta, trashMeta, overwrite: true); } catch { }
        }

        InvalidateSceneListCache(projectId);
        return true;
    }

    /// <summary>
    /// Lists soft-deleted clip versions sitting inside assets/video/.trash/
    /// </summary>
    public async Task<IReadOnlyList<ClipVersionItem>> GetTrashClipVersionsAsync(string projectId, int scene, int clip)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Array.Empty<ClipVersionItem>();
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return Array.Empty<ClipVersionItem>();

        var trashDir = Path.Combine(dir, "assets", "video", ".trash");
        if (!Directory.Exists(trashDir)) return Array.Empty<ClipVersionItem>();

        var result = new List<ClipVersionItem>();
        var prefix = $"scene_{scene:D2}_clip_{clip:D2}";

        foreach (var mp4 in Directory.EnumerateFiles(trashDir, $"{prefix}*.mp4"))
        {
            var sidecar = Path.ChangeExtension(mp4, ".clip.json");
            if (!File.Exists(sidecar)) sidecar = Path.ChangeExtension(mp4, ".meta.json");
            var fi = new FileInfo(mp4);
            var item = ParseClipSidecarOrMeta(sidecar, mp4, scene, clip, take: 0, isCurrent: false, fi.LastWriteTimeUtc);
            result.Add(item);
        }

        return await Task.FromResult(result.OrderByDescending(x => x.CreatedAtUtc).ToList()).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores a soft-deleted take version from assets/video/.trash/ back to assets/video/history/
    /// </summary>
    public async Task<bool> RestoreSoftDeletedClipVersionAsync(string projectId, int scene, int clip, string versionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId)) return false;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return false;

        var videoDir = Path.Combine(dir, "assets", "video");
        var trashDir = Path.Combine(videoDir, ".trash");
        var trashMp4 = Path.Combine(trashDir, versionId);
        if (!File.Exists(trashMp4)) return false;

        var historyDir = Path.Combine(videoDir, "history");
        Directory.CreateDirectory(historyDir);

        var restoredMp4 = Path.Combine(historyDir, versionId);
        File.Move(trashMp4, restoredMp4, overwrite: true);

        var trashSidecar = Path.ChangeExtension(trashMp4, ".clip.json");
        if (File.Exists(trashSidecar))
        {
            var restoredSidecar = Path.Combine(historyDir, Path.GetFileName(trashSidecar));
            try { File.Move(trashSidecar, restoredSidecar, overwrite: true); } catch { }
        }

        InvalidateSceneListCache(projectId);
        return true;
    }

    /// <summary>
    /// Permanently deletes all soft-deleted files in assets/video/.trash/ for a clip.
    /// </summary>
    public async Task<int> EmptyClipTrashAsync(string projectId, int scene, int clip)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return 0;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return 0;

        var trashDir = Path.Combine(dir, "assets", "video", ".trash");
        if (!Directory.Exists(trashDir)) return 0;

        var prefix = $"scene_{scene:D2}_clip_{clip:D2}";
        var purgedCount = 0;

        foreach (var file in Directory.EnumerateFiles(trashDir, $"{prefix}*"))
        {
            try
            {
                File.Delete(file);
                if (file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    purgedCount++;
            }
            catch { }
        }

        InvalidateSceneListCache(projectId);
        return await Task.FromResult(purgedCount).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all historical generation runs ("takes") of a scene's background audio for comparison —
    /// the audio equivalent of <see cref="GetClipVersionsAsync"/>. Unlike clips, the audio bytes
    /// themselves are never stored server-side (client-storage-primary — see
    /// <c>ClientMediaFolderService</c>); this scans the small take-metadata sidecars this server does
    /// keep (<c>assets/music/scene_XX.meta.json</c> active, <c>assets/music/history/*.meta.json</c>
    /// archived) and every take is therefore always <see cref="MusicVersionItem.ClientOnly"/>.
    /// </summary>
    public async Task<IReadOnlyList<MusicVersionItem>> GetMusicVersionsAsync(string projectId, int scene)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Array.Empty<MusicVersionItem>();
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return Array.Empty<MusicVersionItem>();

        var musicDir = Path.Combine(dir, "assets", "music");
        var result = new List<MusicVersionItem>();
        var activeSidecar = Path.Combine(musicDir, $"scene_{scene:D2}.meta.json");
        var hasActiveSidecar = File.Exists(activeSidecar);
        if (hasActiveSidecar)
        {
            var item = ParseMusicSidecar(activeSidecar, scene, isCurrent: true);
            if (item is not null) result.Add(item);
        }

        var historyDir = Path.Combine(musicDir, "history");
        if (Directory.Exists(historyDir))
        {
            foreach (var sidecar in Directory.EnumerateFiles(historyDir, $"scene_{scene:D2}_take_*.meta.json"))
            {
                var item = ParseMusicSidecar(sidecar, scene, isCurrent: false);
                if (item is not null) result.Add(item);
            }
        }

        // Music generated before this take-history feature existed has no sidecar at all — without
        // this, a scene whose audio was clearly generated (HasSceneMusicAsync true) would show zero
        // takes. Bucket any registry rows the sidecars above didn't already cover into one synthetic
        // "legacy" current entry — there is no prior take to compare it against, which is honest:
        // regenerating before this feature existed destroyed whatever came before it.
        if (!hasActiveSidecar && _mediaRegistry is not null)
        {
            var registered = await _mediaRegistry.ListForSceneMusicAsync(projectId, scene).ConfigureAwait(false);
            if (registered.Count > 0)
            {
                var activeNames = registered
                    .Where(r => !r.RelativePath.Contains("/history/", StringComparison.OrdinalIgnoreCase))
                    .Select(r => Path.GetFileName(r.RelativePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (activeNames.Count > 0)
                {
                    var earliest = registered
                        .Where(r => activeNames.Contains(Path.GetFileName(r.RelativePath), StringComparer.OrdinalIgnoreCase))
                        .Select(r => DateTimeOffset.TryParse(r.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto.UtcDateTime : DateTime.UtcNow)
                        .DefaultIfEmpty(DateTime.UtcNow)
                        .Min();
                    result.Add(new MusicVersionItem
                    {
                        TakeId = "legacy",
                        Scene = scene,
                        IsCurrent = true,
                        CreatedAtUtc = earliest,
                        Model = "",
                        IsVocal = false,
                        Prompt = "(generated before take history was added)",
                        SegmentFileNames = activeNames,
                        ClientOnly = true,
                        RelativePaths = activeNames.Select(n => $"assets/music/{n}").ToList(),
                    });
                }
            }
        }

        return result.OrderByDescending(x => x.CreatedAtUtc).ToList();
    }

    private static MusicVersionItem? ParseMusicSidecar(string sidecarPath, int scene, bool isCurrent)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var root = doc.RootElement;
            var item = new MusicVersionItem
            {
                TakeId = root.TryGetProperty("take_id", out var tid) ? tid.GetString() ?? "" : "",
                Scene = scene,
                IsCurrent = isCurrent,
                Model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                IsVocal = root.TryGetProperty("is_vocal", out var iv) && iv.ValueKind == JsonValueKind.True,
                Prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "",
                Lyrics = root.TryGetProperty("lyrics", out var ly) ? ly.GetString() : null,
                ClientOnly = true,
            };
            if (root.TryGetProperty("segment_file_names", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    var name = s.GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    item.SegmentFileNames.Add(name);
                    item.RelativePaths.Add(isCurrent ? $"assets/music/{name}" : $"assets/music/history/{name}");
                }
            }
            item.CreatedAtUtc = root.TryGetProperty("created_at_utc", out var ca) &&
                DateTime.TryParse(ca.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime()
                : File.GetLastWriteTimeUtc(sidecarPath);
            if (string.IsNullOrWhiteSpace(item.TakeId))
                item.TakeId = Path.GetFileNameWithoutExtension(sidecarPath);
            return item;
        }
        catch
        {
            return null; // best-effort sidecar parse, same tolerance as ParseClipSidecarOrMeta
        }
    }

    /// <summary>
    /// Marks a historical audio take as the active one for a scene — metadata only (rewriting which
    /// sidecar is "active"). Unlike <see cref="PromoteClipVersionAsync"/>, this cannot copy the
    /// actual .wav bytes itself (they live only in the browser's local media folder, never on this
    /// server) — the caller must also re-save each of the returned segment file names to its active
    /// path via <c>ClientMediaFolderService</c> (which archives whatever's currently active into
    /// history first, same as any other regeneration) before calling this.
    /// </summary>
    /// <summary>
    /// Shared prologue for the music-take mutators: guards the ids, resolves the project directory,
    /// and locates the requested (non-current) historical take. Returns null — matching each caller's
    /// early-return-false semantics — when any guard fails or the take isn't a promotable/deletable
    /// historical version.
    /// </summary>
    private async Task<(string Dir, MusicVersionItem Target)?> TryResolveMusicTakeAsync(string projectId, int scene, string takeId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(takeId)) return null;
        var dir = GetProjectDir(projectId);
        if (!Directory.Exists(dir)) return null;

        var versions = await GetMusicVersionsAsync(projectId, scene).ConfigureAwait(false);
        var target = versions.FirstOrDefault(v => string.Equals(v.TakeId, takeId, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsCurrent) return null;

        return (dir, target);
    }

    public async Task<bool> PromoteMusicVersionAsync(string projectId, int scene, string takeId)
    {
        var resolved = await TryResolveMusicTakeAsync(projectId, scene, takeId).ConfigureAwait(false);
        if (resolved is null) return false;
        var dir = resolved.Value.Dir;

        var musicDir = Path.Combine(dir, "assets", "music");
        var historyDir = Path.Combine(musicDir, "history");
        var activeSidecar = Path.Combine(musicDir, $"scene_{scene:D2}.meta.json");

        if (File.Exists(activeSidecar))
        {
            var current = ParseMusicSidecar(activeSidecar, scene, isCurrent: true);
            if (current is not null && !string.Equals(current.TakeId, "legacy", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(historyDir);
                var archivePath = Path.Combine(historyDir, $"scene_{scene:D2}_take_{current.TakeId}.meta.json");
                try { File.Copy(activeSidecar, archivePath, overwrite: true); } catch { }
            }
        }

        var targetSidecar = Path.Combine(historyDir, $"scene_{scene:D2}_take_{takeId}.meta.json");
        if (!File.Exists(targetSidecar)) return false;
        Directory.CreateDirectory(musicDir);
        File.Copy(targetSidecar, activeSidecar, overwrite: true);

        InvalidateSceneListCache(projectId);
        return true;
    }

    /// <summary>
    /// Soft-deletes a historical audio take's sidecar by moving it into assets/music/.trash/ — mirrors
    /// <see cref="SoftDeleteClipVersionAsync"/>. As with promote, this only touches the server-side
    /// metadata; any actual local segment bytes are left for the browser's own storage to manage.
    /// </summary>
    public async Task<bool> SoftDeleteMusicVersionAsync(string projectId, int scene, string takeId)
    {
        var resolved = await TryResolveMusicTakeAsync(projectId, scene, takeId).ConfigureAwait(false);
        if (resolved is null) return false;
        var dir = resolved.Value.Dir;

        var musicDir = Path.Combine(dir, "assets", "music");
        var historySidecar = Path.Combine(musicDir, "history", $"scene_{scene:D2}_take_{takeId}.meta.json");
        if (!File.Exists(historySidecar)) return false;

        var trashDir = Path.Combine(musicDir, ".trash");
        Directory.CreateDirectory(trashDir);
        var trashSidecar = Path.Combine(trashDir, $"scene_{scene:D2}_take_{takeId}.meta.json");
        File.Move(historySidecar, trashSidecar, overwrite: true);

        InvalidateSceneListCache(projectId);
        return true;
    }

    /// <summary>Soft-deleted audio takes for one scene — mirrors <see cref="GetTrashClipVersionsAsync"/>.</summary>
    public Task<IReadOnlyList<MusicVersionItem>> GetTrashMusicVersionsAsync(string projectId, int scene)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Task.FromResult<IReadOnlyList<MusicVersionItem>>(Array.Empty<MusicVersionItem>());
        var dir = GetProjectDir(projectId);
        var trashDir = Path.Combine(dir, "assets", "music", ".trash");
        if (!Directory.Exists(trashDir)) return Task.FromResult<IReadOnlyList<MusicVersionItem>>(Array.Empty<MusicVersionItem>());

        var result = new List<MusicVersionItem>();
        foreach (var sidecar in Directory.EnumerateFiles(trashDir, $"scene_{scene:D2}_take_*.meta.json"))
        {
            var item = ParseMusicSidecar(sidecar, scene, isCurrent: false);
            if (item is not null) result.Add(item);
        }
        return Task.FromResult<IReadOnlyList<MusicVersionItem>>(result.OrderByDescending(x => x.CreatedAtUtc).ToList());
    }

    /// <summary>Restores a soft-deleted audio take's sidecar back to assets/music/history/ — mirrors
    /// <see cref="RestoreSoftDeletedClipVersionAsync"/>.</summary>
    public Task<bool> RestoreSoftDeletedMusicVersionAsync(string projectId, int scene, string takeId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(takeId)) return Task.FromResult(false);
        var dir = GetProjectDir(projectId);
        var musicDir = Path.Combine(dir, "assets", "music");
        var trashSidecar = Path.Combine(musicDir, ".trash", $"scene_{scene:D2}_take_{takeId}.meta.json");
        if (!File.Exists(trashSidecar)) return Task.FromResult(false);

        var historyDir = Path.Combine(musicDir, "history");
        Directory.CreateDirectory(historyDir);
        var restoredSidecar = Path.Combine(historyDir, Path.GetFileName(trashSidecar));
        File.Move(trashSidecar, restoredSidecar, overwrite: true);

        InvalidateSceneListCache(projectId);
        return Task.FromResult(true);
    }

    private static List<string> CompareSceneInBlueprints(string currentBpJson, string? parentBpJson, int sceneNumber)
    {
        var changes = new List<string>();
        try
        {
            var curRoot = System.Text.Json.Nodes.JsonNode.Parse(currentBpJson) as System.Text.Json.Nodes.JsonObject;
            if (curRoot is null) return changes;
            var curScenes = curRoot["scenes"] as System.Text.Json.Nodes.JsonArray;
            if (curScenes is null) return changes;

            System.Text.Json.Nodes.JsonObject? curScene = null;
            foreach (var s in curScenes)
            {
                if (s is System.Text.Json.Nodes.JsonObject sObj && ReadJsonNodeInt(sObj["scene_number"]) == sceneNumber)
                {
                    curScene = sObj;
                    break;
                }
            }

            if (curScene is null) return changes;

            if (string.IsNullOrWhiteSpace(parentBpJson))
            {
                changes.Add("Scene created");
                return changes;
            }

            var parRoot = System.Text.Json.Nodes.JsonNode.Parse(parentBpJson) as System.Text.Json.Nodes.JsonObject;
            var parScenes = parRoot?["scenes"] as System.Text.Json.Nodes.JsonArray;
            System.Text.Json.Nodes.JsonObject? parScene = null;
            if (parScenes is not null)
            {
                foreach (var s in parScenes)
                {
                    if (s is System.Text.Json.Nodes.JsonObject sObj && ReadJsonNodeInt(sObj["scene_number"]) == sceneNumber)
                    {
                        parScene = sObj;
                        break;
                    }
                }
            }

            if (parScene is null)
            {
                changes.Add("Scene created");
                return changes;
            }

            var curHeading = curScene["heading"]?.ToString() ?? "";
            var parHeading = parScene["heading"]?.ToString() ?? "";
            if (!string.Equals(curHeading, parHeading, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"Heading updated: \"{curHeading}\"");
            }

            var curClips = curScene["veo_clips"] as System.Text.Json.Nodes.JsonArray ?? curScene["clips"] as System.Text.Json.Nodes.JsonArray;
            var parClips = parScene["veo_clips"] as System.Text.Json.Nodes.JsonArray ?? parScene["clips"] as System.Text.Json.Nodes.JsonArray;

            var curClipDict = (curClips ?? new System.Text.Json.Nodes.JsonArray())
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .ToDictionary(c => ClipKeying.ClipNumber(c));
            var parClipDict = (parClips ?? new System.Text.Json.Nodes.JsonArray())
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .ToDictionary(c => ClipKeying.ClipNumber(c));

            foreach (var (cNum, curC) in curClipDict)
            {
                if (!parClipDict.TryGetValue(cNum, out var parC))
                {
                    changes.Add($"Clip {cNum} added");
                    continue;
                }

                var curPrompt = curC["visual_prompt"]?.ToString() ?? "";
                var parPrompt = parC["visual_prompt"]?.ToString() ?? "";
                if (!string.Equals(curPrompt, parPrompt, StringComparison.Ordinal))
                {
                    changes.Add($"Clip {cNum} prompt modified");
                }

                var curAudio = curC["audio_script"]?.ToString() ?? curC["dialogue"]?.ToString() ?? "";
                var parAudio = parC["audio_script"]?.ToString() ?? parC["dialogue"]?.ToString() ?? "";
                if (!string.Equals(curAudio, parAudio, StringComparison.Ordinal))
                {
                    changes.Add($"Clip {cNum} dialogue modified");
                }

                var curDur = curC["duration_seconds"]?.ToString() ?? "";
                var parDur = parC["duration_seconds"]?.ToString() ?? "";
                if (!string.Equals(curDur, parDur, StringComparison.Ordinal))
                {
                    changes.Add($"Clip {cNum} duration changed to {curDur}s");
                }
            }

            foreach (var (cNum, _) in parClipDict)
            {
                if (!curClipDict.ContainsKey(cNum))
                {
                    changes.Add($"Clip {cNum} removed");
                }
            }
        }
        catch { /* best effort diff */ }

        return changes;
    }

    /// <summary>
    /// Drop scene-list + blueprint/dir read caches for a project (call after gen/remux/stage2).
    /// </summary>
    public void InvalidateSceneListCache(string? projectId)
    {
        _sceneListCache?.Invalidate(projectId);
        InvalidateReadCaches(projectId);
    }

    /// <summary>Invalidate blueprint path/bytes and asset dir indexes (and projects list if projectId null).</summary>
    public void InvalidateReadCaches(string? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _readCache.InvalidateAll();
            return;
        }

        string? dir = null;
        try { dir = GetProjectDir(projectId); }
        catch { /* unknown project — still drop path entry */ }
        _readCache.InvalidateProject(projectId, dir);
    }

    public string WorkspaceRoot => _workspaceRoot;

    public string ActiveProjectId
    {
        get
        {
            // The stored pointer can go stale (project deleted/renamed on disk without updating
            // workspace.json) — self-heal by falling through to the directory scan below instead
            // of returning a dangling id that resolves to no project.
            if (!string.IsNullOrWhiteSpace(_activeProjectId) && ProjectDirExists(_activeProjectId))
                return _activeProjectId;
            // Prefer flat project.json; else first namespaced project found
            var projectsDir = Path.Combine(WorkspaceRoot, "projects");
            if (!Directory.Exists(projectsDir))
                return "";
            foreach (var dir in Directory.GetDirectories(projectsDir)
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, "workspace.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(Path.Combine(dir, "project.json")))
                    return name;
                foreach (var child in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(child, "project.json")))
                        return $"{name}/{Path.GetFileName(child)}";
                }
            }
            return "";
        }
    }

    private bool ProjectDirExists(string projectId)
    {
        try
        {
            var id = NormalizeProjectId(projectId);
            var dir = ResolveProjectDirPath(id);
            return File.Exists(Path.Combine(dir, "project.json"));
        }
        catch { return false; }
    }

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct = default) =>
        _readCache.GetOrBuildProjectsAsync(ListProjectsCoreAsync, ct);

    private async Task<IReadOnlyList<ProjectInfo>> ListProjectsCoreAsync(CancellationToken ct)
    {
        var projectsDir = Path.Combine(WorkspaceRoot, "projects");
        if (!Directory.Exists(projectsDir))
            return Array.Empty<ProjectInfo>();

        var list = new List<ProjectInfo>();
        // Flat: projects/{id}/project.json  OR nested: projects/{user}/{slug}/project.json
        foreach (var dir in Directory.GetDirectories(projectsDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "workspace.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var metaPath = Path.Combine(dir, "project.json");
            if (File.Exists(metaPath))
            {
                var info = await ReadProjectInfoFromDirAsync(dir, idOverride: name, ct).ConfigureAwait(false);
                if (info is not null)
                    list.Add(info);
                continue;
            }

            // Owner namespace folder — scan children for project.json
            foreach (var child in Directory.GetDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                var slug = Path.GetFileName(child);
                var childMeta = Path.Combine(child, "project.json");
                if (!File.Exists(childMeta))
                    continue;
                var compositeId = $"{name}/{slug}";
                var info = await ReadProjectInfoFromDirAsync(child, idOverride: compositeId, ct)
                    .ConfigureAwait(false);
                if (info is not null)
                    list.Add(info);
            }
        }
        return list.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<ProjectInfo?> ReadProjectInfoFromDirAsync(
        string dir, string idOverride, CancellationToken ct)
    {
        var metaPath = Path.Combine(dir, "project.json");
        if (!File.Exists(metaPath))
            return null;
        string? title = null;
        string? label = null;
        string? ownerUserId = null;
        string? parentProjectId = null;
        string? visibilityMode = null;
        string? studioPath = null;
        string? metaId = null;
        try
        {
            await using var stream = File.OpenRead(metaPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, "id", StringComparison.OrdinalIgnoreCase))
                    metaId = p.Value.GetString();
                else if (string.Equals(p.Name, "title", StringComparison.OrdinalIgnoreCase))
                    title = p.Value.GetString();
                else if (string.Equals(p.Name, "label", StringComparison.OrdinalIgnoreCase))
                    label = p.Value.GetString();
                else if (string.Equals(p.Name, "parentProjectId", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.Name, "parent_project_id", StringComparison.OrdinalIgnoreCase))
                    parentProjectId = p.Value.GetString();
                else if (string.Equals(p.Name, "visibilityMode", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.Name, "visibility_mode", StringComparison.OrdinalIgnoreCase))
                    visibilityMode = p.Value.GetString();
                else if (string.Equals(p.Name, "ownerUserId", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.Name, "owner_user_id", StringComparison.OrdinalIgnoreCase))
                    ownerUserId = p.Value.GetString();
                else if (string.Equals(p.Name, "studioPath", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.Name, "studio_path", StringComparison.OrdinalIgnoreCase))
                    studioPath = p.Value.GetString();
            }
        }
        catch
        {
            return null;
        }

        // idOverride (derived from the actual folder path) always wins over project.json's own
        // embedded "id" field — GetProjectDir resolves purely from the folder-path string and
        // never consults project.json, so if the two ever disagree (e.g. a project folder copied
        // or renamed on disk without updating its project.json), trusting metaId here would report
        // an id whose GetProjectDir/GetProjectAsync round-trip lands back on a DIFFERENT physical
        // folder — the one the stale metaId happens to name — silently misdirecting reads/writes
        // for "this" project into whatever unrelated project already owns that folder.
        var id = idOverride;
        return new ProjectInfo
        {
            Id = id,
            Title = title,
            Label = label ?? title ?? id,
            Path = dir,
            OwnerUserId = string.IsNullOrWhiteSpace(ownerUserId) ? null : ownerUserId.Trim(),
            ParentProjectId = string.IsNullOrWhiteSpace(parentProjectId) ? null : parentProjectId.Trim(),
            VisibilityMode = string.IsNullOrWhiteSpace(visibilityMode) ? "Private" : visibilityMode.Trim(),
            StudioPath = ProjectStudioPaths.Normalize(studioPath),
        };
    }

    public async Task<ProjectInfo?> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var want = NormalizeProjectId(projectId);
        var list = await ListProjectsAsync(ct).ConfigureAwait(false);
        return list.FirstOrDefault(p =>
            string.Equals(NormalizeProjectId(p.Id), want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validates a project exists, without touching the process-global active-project
    /// pointer or workspace.json. Use this from background job execution instead of
    /// <see cref="ActivateAsync"/> — background jobs run concurrently across projects/users
    /// and must not race each other (or the UI) for the shared "active project" preference.
    /// </summary>
    public async Task<ProjectInfo> RequireProjectAsync(string projectId, CancellationToken ct = default) =>
        await GetProjectAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown project: {projectId}");

    /// <summary>
    /// UI-only preference: sets the process-global active project and persists it to
    /// workspace.json. Do not call this from background job execution — see
    /// <see cref="RequireProjectAsync"/>.
    /// </summary>
    public async Task<ProjectInfo> ActivateAsync(string projectId, CancellationToken ct = default)
    {
        var p = await RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        _activeProjectId = p.Id;
        var wsPath = Path.Combine(WorkspaceRoot, "projects", "workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(wsPath)!);
        await File.WriteAllTextAsync(
            wsPath,
            JsonSerializer.Serialize(new WorkspaceState { ActiveProject = p.Id }, JsonOpts),
            ct).ConfigureAwait(false);
        return p;
    }

    /// <summary>
    /// Create project folder with project.json + source/, then activate it.
    /// When <paramref name="ownerUserId"/> is set: <c>projects/{owner}/{slug}/</c> and id <c>owner/slug</c>
    /// (plan namespacing). Legacy flat <c>projects/{slug}/</c> when owner is empty.
    /// </summary>
    /// <remarks>Id is sanitized; owner segment is sanitized separately.
    /// </summary>
    public async Task<ProjectInfo> CreateProjectAsync(
        string idOrTitle,
        string? title = null,
        CancellationToken ct = default,
        string? ownerUserId = null,
        string? studioPath = null)
    {
        var raw = (idOrTitle ?? "").Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("Project name required");

        var slug = SanitizeProjectId(raw);
        if (slug.Length == 0)
            throw new InvalidOperationException("Project name has no usable characters");

        var owner = string.IsNullOrWhiteSpace(ownerUserId) ? null : SanitizeUserSegment(ownerUserId);
        var id = string.IsNullOrEmpty(owner) ? slug : $"{owner}/{slug}";
        var dir = string.IsNullOrEmpty(owner)
            ? Path.Combine(WorkspaceRoot, "projects", slug)
            : Path.Combine(WorkspaceRoot, "projects", owner, slug);
        if (Directory.Exists(dir))
        {
            var metaFile = Path.Combine(dir, "project.json");
            if (File.Exists(metaFile))
            {
                try
                {
                    var existing = await GetProjectAsync(id, ct).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        if (string.IsNullOrWhiteSpace(existing.OwnerUserId) && !string.IsNullOrWhiteSpace(ownerUserId))
                        {
                            var metaExisting = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                await File.ReadAllTextAsync(metaFile, ct).ConfigureAwait(false), JsonOpts)
                                ?? new Dictionary<string, object?>();
                            metaExisting["ownerUserId"] = ownerUserId.Trim();
                            await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(metaExisting, JsonOpts) + "\n", ct).ConfigureAwait(false);
                            InvalidateReadCaches(null);
                        }
                        return await ActivateAsync(existing.Id, ct).ConfigureAwait(false);
                    }
                }
                catch { /* fall through to clean up leftover folder */ }
            }
            try { Directory.Delete(dir, recursive: true); } catch { /* non-fatal */ }
        }

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "source"));
        Directory.CreateDirectory(Path.Combine(dir, "assets", "characters"));
        Directory.CreateDirectory(Path.Combine(dir, "assets", "scenes"));
        Directory.CreateDirectory(Path.Combine(dir, "assets", "video"));

        var displayTitle = string.IsNullOrWhiteSpace(title) ? raw : title.Trim();
        var meta = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = displayTitle,
            ["blueprint_file"] = "blueprint.clips.grok.json",
            ["scenes_file"] = "scenes.json",
            ["config_file"] = "pipeline_config.json",
            ["state_file"] = "pipeline_state.json",
            ["description"] = "",
            ["ownerUserId"] = string.IsNullOrWhiteSpace(ownerUserId)
                ? owner
                : ownerUserId.Trim(),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["studioPath"] = ProjectStudioPaths.Normalize(studioPath),
            // Format version for export/import converters (ProjectMigrationService).
            ["schema_version"] = ProjectFormatVersions.ProjectSchemaVersion,
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "project.json"),
            JsonSerializer.Serialize(meta, JsonOpts) + "\n",
            ct).ConfigureAwait(false);

        // Git package (text only) — initial commit for history foundation
        try
        {
            ProjectGitRepositoryService.EnsureRepositoryAt(dir);
            var git = new ProjectGitRepositoryService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
            await git.CommitProjectStateAsync(dir, owner ?? "PageToMovie", "Initial project state").ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal: nested-repo or git unavailable must not block create
        }

        InvalidateReadCaches(null); // projects list

        // Copy studio model picks from another project the same owner already configured.
        // New projects used to start with empty pipeline_config → "no model selected" on first import.
        try
        {
            await SeedModelConfigFromOwnerAsync(id, ownerUserId, ct).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal — import page still blocks until Settings is filled.
        }

        return await ActivateAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Model selection keys that should travel with a new empty project from a sibling
    /// project owned by the same user (Settings is per-project today).
    /// </summary>
    private static readonly string[] ModelConfigSeedKeys =
    {
        "planning_model_name", "chat_model_name",
        "vision_model_name",
        "quality_model_name", "video_review_model_name",
        "model_name", "image_model_name",
        "audio_model_name", "voice_model_name",
        "planning_provider", "video_provider", "image_provider",
        "vision_provider", "audio_provider", "voice_provider",
        "providers", // nested map of capability → model id when present
    };

    private async Task SeedModelConfigFromOwnerAsync(
        string newProjectId,
        string? ownerUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            return;

        var all = await ListProjectsAsync(ct).ConfigureAwait(false);
        var siblings = all
            .Where(p =>
                !string.Equals(p.Id, newProjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.OwnerUserId, ownerUserId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => string.Equals(p.Id, ActiveProjectId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sib in siblings)
        {
            Dictionary<string, JsonElement> src;
            try { src = await GetConfigAsync(sib.Id, ct).ConfigureAwait(false); }
            catch { continue; }
            if (src.Count == 0) continue;

            // Prefer a sibling that already has script/planning configured.
            var hasPlanning =
                ProjectModelSelection.TryGet(src, ProjectModelSelection.PlanningConfigKey, ProjectModelSelection.ChatConfigKey)
                is { Length: > 0 };
            if (!hasPlanning)
                continue;

            var seed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in ModelConfigSeedKeys)
            {
                if (!src.TryGetValue(key, out var el)) continue;
                if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
                seed[key] = el.Deserialize<object>();
            }
            if (seed.Count == 0) continue;

            var path = ConfigPath(newProjectId);
            var json = JsonSerializer.Serialize(seed, JsonDefaults.Indented);
            await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
            InvalidateReadCaches(newProjectId);
            return;
        }
    }

    /// <summary>
    /// Rewrite <c>ownerUserId</c> in project.json to the stable account id without moving the folder.
    /// Used when list discovers a project under an alias path (identity drift).
    /// </summary>
    public async Task RepairProjectOwnerAsync(
        string projectId,
        string ownerUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(ownerUserId))
            return;
        var dir = GetProjectDir(projectId);
        var metaPath = Path.Combine(dir, "project.json");
        if (!File.Exists(metaPath))
            return;

        Dictionary<string, object?> meta;
        try
        {
            meta = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                       await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false), JsonOpts)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            return;
        }

        var want = ownerUserId.Trim();
        if (meta.TryGetValue("ownerUserId", out var existing) &&
            existing is string s &&
            string.Equals(s.Trim(), want, StringComparison.OrdinalIgnoreCase))
            return;

        meta["ownerUserId"] = want;
        await File.WriteAllTextAsync(
            metaPath,
            JsonSerializer.Serialize(meta, JsonOpts) + "\n",
            ct).ConfigureAwait(false);
        InvalidateReadCaches(projectId);
    }

    /// <summary>Video/audio binaries never copy into a fork — the new owner regenerates or syncs media separately.</summary>
    private static readonly string[] ForkSkipExtensions = { ".mp4", ".webm", ".mov", ".wav", ".avi" };

    /// <summary>
    /// Create a lightweight fork of <paramref name="sourceProjectId"/> under a new owner: copies
    /// screenplay/cast/blueprint/rules/character-reference text and images, excluding video/audio
    /// binaries (kept out of Git for the same reason — see <see cref="ProjectGitRepositoryService"/>).
    /// Unlike <see cref="CreateProjectAsync"/>, does not touch the process-global active-project
    /// pointer — forking on one user's behalf must never steal another user's active project.
    /// </summary>
    public async Task<ProjectInfo> ForkProjectAsync(
        string sourceProjectId,
        string newOwnerUserId,
        bool isInvite = false,
        CancellationToken ct = default)
    {
        var source = await RequireProjectAsync(sourceProjectId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(newOwnerUserId))
            throw new InvalidOperationException("newOwnerUserId required");

        // Allow forking if project is Open (Public Forkable), unowned/legacy, requested via explicit invite, or if caller is owner/admin
        var isOwnerOrLegacy = string.IsNullOrWhiteSpace(source.OwnerUserId) || string.Equals(source.OwnerUserId, newOwnerUserId, StringComparison.OrdinalIgnoreCase);
        var isForkable = string.Equals(source.VisibilityMode, "Open", StringComparison.OrdinalIgnoreCase) || string.Equals(source.VisibilityMode, "PublicForkable", StringComparison.OrdinalIgnoreCase);
        if (!isInvite && !isOwnerOrLegacy && !isForkable)
        {
            throw new InvalidOperationException($"Forking disabled for this project (Visibility mode: {source.VisibilityMode}). Only 'Open' (Public Forkable) projects can be forked by community members.");
        }

        // Idempotent per (source, user): if this owner already has a fork of this source, reopen it
        // rather than creating a duplicate. Otherwise each Easy Start "read in my voice" pick piled up
        // a new fork (Buster-fork-xxxx, Buster-fork-yyyy, …).
        var forkOwnerSeg = SanitizeUserSegment(newOwnerUserId);
        var existingFork = (await ListProjectsAsync(ct).ConfigureAwait(false)).FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.ParentProjectId)
            && string.Equals(p.ParentProjectId, source.Id, StringComparison.OrdinalIgnoreCase)
            && (p.Id ?? "").Contains('/')
            && string.Equals((p.Id ?? "").Split('/')[0], forkOwnerSeg, StringComparison.OrdinalIgnoreCase));
        if (existingFork is not null)
            return existingFork;

        var baseName = source.Title ?? source.Id.Split('/').LastOrDefault() ?? source.Id;
        var slug = SanitizeProjectId($"{baseName}-fork-{Guid.NewGuid().ToString("N")[..6]}");
        if (slug.Length == 0)
            throw new InvalidOperationException("Could not derive a fork id");

        var ownerSeg = SanitizeUserSegment(newOwnerUserId);
        var newId = string.IsNullOrEmpty(ownerSeg) ? slug : $"{ownerSeg}/{slug}";
        var newDir = string.IsNullOrEmpty(ownerSeg)
            ? Path.Combine(WorkspaceRoot, "projects", slug)
            : Path.Combine(WorkspaceRoot, "projects", ownerSeg, slug);
        if (Directory.Exists(newDir))
            throw new InvalidOperationException($"Project already exists: {newId}");

        Directory.CreateDirectory(newDir);
        foreach (var file in Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (ForkSkipExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            var rel = Path.GetRelativePath(source.Path, file);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] == ".git")
                continue; // never copy the source's own Git history into the fork

            var destPath = Path.Combine(newDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }

        // Rewrite project.json for the new owner/id/parent link rather than keeping the source's copy.
        var metaPath = Path.Combine(newDir, "project.json");
        Dictionary<string, object?> meta;
        try
        {
            meta = JsonSerializer.Deserialize<Dictionary<string, object?>>(await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false), JsonOpts)
                    ?? new Dictionary<string, object?>();
        }
        catch
        {
            meta = new Dictionary<string, object?>();
        }
        meta["id"] = newId;
        meta["title"] = $"{source.Title ?? source.Id} (fork)";
        meta["ownerUserId"] = newOwnerUserId.Trim();
        meta["parentProjectId"] = source.Id;
        // A fork is the user's private working copy — don't inherit the source's "Open" visibility,
        // or every fork would show up as its own pickable "story" in the forkable list.
        meta["visibilityMode"] = "Private";
        meta["createdAt"] = DateTimeOffset.UtcNow.ToString("o");
        await File.WriteAllTextAsync(
            metaPath, JsonSerializer.Serialize(meta, JsonOpts) + "\n", ct).ConfigureAwait(false);

        try
        {
            ProjectGitRepositoryService.EnsureRepositoryAt(newDir);
            var git = new ProjectGitRepositoryService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
            await git.CommitProjectStateAsync(newDir, newOwnerUserId, "Initial fork state").ConfigureAwait(false);
        }
        catch { /* non-fatal */ }

        InvalidateReadCaches(null);
        return await RequireProjectAsync(newId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Update project visibility mode ("Private", "Public", or "Open") in project.json.
    /// </summary>

    /// <summary>
    /// Update display title/label in project.json. Does not move the folder (id stays stable).
    /// </summary>
    public async Task<ProjectInfo> RenameProjectAsync(
        string projectId,
        string newTitle,
        CancellationToken ct = default)
    {
        var title = (newTitle ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Project name is required.");
        if (title.Length > 80)
            title = title[..80].Trim();

        var proj = await RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        var metaPath = Path.Combine(proj.Path, "project.json");
        var meta = await ReadMetaOrEmptyAsync(metaPath, ct).ConfigureAwait(false);

        meta["id"] = proj.Id;
        meta["title"] = title;
        meta["label"] = title;
        if (!string.IsNullOrWhiteSpace(proj.OwnerUserId)) meta["ownerUserId"] = proj.OwnerUserId;
        if (!string.IsNullOrWhiteSpace(proj.ParentProjectId)) meta["parentProjectId"] = proj.ParentProjectId;
        if (!string.IsNullOrWhiteSpace(proj.VisibilityMode)) meta["visibilityMode"] = proj.VisibilityMode;

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOpts) + "\n", ct).ConfigureAwait(false);
        InvalidateReadCaches(null);

        proj.Title = title;
        proj.Label = title;
        TriggerAutoGitCommit(projectId, "Rename project");
        return proj;
    }


    public async Task<ProjectInfo> SetProjectVisibilityModeAsync(
        string projectId,
        string visibilityMode,
        CancellationToken ct = default)
    {
        var proj = await RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        var validModes = new[] { "Private", "Public", "Open" };
        var mode = validModes.FirstOrDefault(m => string.Equals(m, visibilityMode, StringComparison.OrdinalIgnoreCase)) ?? "Private";

        var metaPath = Path.Combine(proj.Path, "project.json");
        var meta = await ReadMetaOrEmptyAsync(metaPath, ct).ConfigureAwait(false);

        meta["visibilityMode"] = mode;
        meta["id"] = proj.Id;
        if (!string.IsNullOrWhiteSpace(proj.Title)) meta["title"] = proj.Title;
        if (!string.IsNullOrWhiteSpace(proj.OwnerUserId)) meta["ownerUserId"] = proj.OwnerUserId;
        if (!string.IsNullOrWhiteSpace(proj.ParentProjectId)) meta["parentProjectId"] = proj.ParentProjectId;

        var updatedJson = JsonSerializer.Serialize(meta, JsonOpts) + "\n";
        await File.WriteAllTextAsync(metaPath, updatedJson, ct).ConfigureAwait(false);

        proj.VisibilityMode = mode;
        InvalidateReadCaches(null);
        TriggerAutoGitCommit(projectId, "Update project visibility");
        return proj;
    }

    /// <summary>
    /// Persist product path (full vs simple-voice) on project.json.
    /// </summary>
    public async Task<ProjectInfo> SetProjectStudioPathAsync(
        string projectId,
        string? studioPath,
        CancellationToken ct = default)
    {
        var path = ProjectStudioPaths.Normalize(studioPath);
        var proj = await RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        var metaPath = Path.Combine(proj.Path, "project.json");
        var meta = await ReadMetaOrEmptyAsync(metaPath, ct).ConfigureAwait(false);

        meta["studioPath"] = path;
        meta["id"] = proj.Id;
        if (!string.IsNullOrWhiteSpace(proj.Title)) meta["title"] = proj.Title;
        if (!string.IsNullOrWhiteSpace(proj.OwnerUserId)) meta["ownerUserId"] = proj.OwnerUserId;
        if (!string.IsNullOrWhiteSpace(proj.ParentProjectId)) meta["parentProjectId"] = proj.ParentProjectId;
        if (!string.IsNullOrWhiteSpace(proj.VisibilityMode)) meta["visibilityMode"] = proj.VisibilityMode;

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOpts) + "\n", ct).ConfigureAwait(false);
        proj.StudioPath = path;
        InvalidateReadCaches(null);
        return proj;
    }

    /// <summary>
    /// True when the user may publish this project's media publicly (demo gallery).
    /// Admin always; otherwise must match project.json ownerUserId.
    /// Legacy projects with no owner are admin-only for public publish.
    /// </summary>
    public async Task<bool> CanUserPublishDemoAsync(
        string projectId,
        string? userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (isAdmin)
            return true;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(projectId))
            return false;
        var p = await GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (p is null)
            return false;
        // Use the same alias-aware ownership rule the project LIST uses (ProjectOwnership.IsOwnedBy),
        // not a strict OwnerUserId == userId match. Otherwise a project the user owns and sees in
        // their list — e.g. one whose owner lives only in the "username/slug" folder segment with an
        // empty OwnerUserId field — fails this gate, so the manage/rename affordance vanishes for it
        // while it works for a sibling whose OwnerUserId happens to match exactly.
        return ProjectOwnership.IsOwnedBy(p, ProjectOwnership.CollectAliases(userId));
    }

    private static string SanitizeProjectId(string raw) => SanitizeProjectIdPublic(raw);

    /// <summary>Public sanitize for import/export tooling (safe folder name).</summary>
    public static string SanitizeProjectIdPublic(string raw)
    {
        // Prefer Pascal/camel-ish folder: strip path junk, keep letters/digits/_/-
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '.' or '/')
            {
                if (sb.Length > 0 && sb[^1] != '_')
                    sb.Append('_');
            }
        }
        var id = sb.ToString().Trim('_');
        if (id.Length > 64) id = id[..64].Trim('_');
        return id;
    }

    /// <summary>
    /// Delete <c>projects/{id}/</c> entirely. Clears active project if it was this one.
    /// </summary>
    public async Task DeleteProjectAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("Project id required");

        var id = NormalizeProjectId(projectId);
        ValidateProjectId(id);

        var projectsRoot = Path.GetFullPath(Path.Combine(WorkspaceRoot, "projects"));
        var dir = Path.GetFullPath(ResolveProjectDirPath(id));
        if (!dir.StartsWith(projectsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dir, projectsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid project path: {projectId}");

        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"Unknown project: {id}");

        // Best-effort delete (files may be locked by a running job). Git writes loose-object files
        // read-only by design (immutable objects) — on Windows, Directory.Delete throws
        // UnauthorizedAccessException for a read-only file regardless of how long you wait, so a plain
        // retry loop never helps a project with any git history (every project has at least the
        // "Initial project state" commit). Clear the attribute recursively first, then retry briefly
        // for the separate, genuinely transient case of a file still open from a running job.
        ClearReadOnlyRecursive(dir);
        var attempts = 0;
        while (true)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempts < 5)
            {
                attempts++;
                ClearReadOnlyRecursive(dir);
                await Task.Delay(200 * attempts, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not delete project “{id}”: {ex.Message}. Close any open files or stop jobs and try again.");
            }
        }

        if (string.Equals(_activeProjectId, id, StringComparison.OrdinalIgnoreCase))
            _activeProjectId = "";

        // Update workspace.json active pointer
        var wsPath = Path.Combine(WorkspaceRoot, "projects", "workspace.json");
        try
        {
            string? nextActive = null;
            if (File.Exists(wsPath))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<WorkspaceState>(
                        await File.ReadAllTextAsync(wsPath, ct).ConfigureAwait(false), JsonOpts);
                    if (string.Equals(state?.ActiveProject, id, StringComparison.OrdinalIgnoreCase))
                    {
                        // Pick another remaining project if any
                        if (Directory.Exists(projectsRoot))
                        {
                            nextActive = Directory.GetDirectories(projectsRoot)
                                .Select(Path.GetFileName)
                                .FirstOrDefault(n =>
                                    !string.IsNullOrEmpty(n) &&
                                    !string.Equals(n, id, StringComparison.OrdinalIgnoreCase));
                        }
                        await File.WriteAllTextAsync(
                            wsPath,
                            JsonSerializer.Serialize(
                                new WorkspaceState { ActiveProject = nextActive ?? "" },
                                JsonOpts) + "\n",
                            ct).ConfigureAwait(false);
                        _activeProjectId = nextActive ?? "";
                    }
                }
                catch { /* leave workspace as-is if unreadable */ }
            }
        }
        catch { /* ignore workspace update failures */ }

        InvalidateReadCaches(null);
        InvalidateReadCaches(id);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Clear the read-only attribute on every file under <paramref name="dir"/> (git's loose
    /// object files are written read-only by design) so a recursive delete doesn't fail on Windows.
    /// Best-effort — an individual file's attribute failing to clear surfaces via the delete itself.</summary>
    private static void ClearReadOnlyRecursive(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                catch { /* best-effort per file */ }
            }
        }
        catch { /* best-effort — directory may have changed mid-walk */ }
    }

    /// <summary>
    /// Project folder path without listing all projects (no cache / GetAwaiter).
    /// Layout: legacy <c>projects/{id}/</c> or namespaced <c>projects/{user}/{slug}/</c>
    /// when <paramref name="projectId"/> is <c>user/slug</c>.
    /// </summary>
    public string GetProjectDir(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("Unknown project: (empty)");
        var id = NormalizeProjectId(projectId);
        ValidateProjectId(id);
        var dir = ResolveProjectDirPath(id);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"Unknown project: {projectId}");
        return dir;
    }

    /// <summary>Normalize composite ids: trim, unify slashes, strip leading @ on owner.</summary>
    /// <remarks>
    /// ASP.NET route params often keep a single-segment <c>%2F</c> encoded (so
    /// <c>Uri.EscapeDataString("alice/Buster")</c> arrives as <c>alice%2FBuster</c>).
    /// Decode that before path resolution.
    /// </remarks>
    public static string NormalizeProjectId(string projectId)
    {
        var id = (projectId ?? "").Trim();
        if (id.Contains('%', StringComparison.Ordinal))
        {
            try { id = Uri.UnescapeDataString(id); }
            catch { /* leave encoded if malformed */ }
        }
        id = id.Replace('\\', '/');
        while (id.Contains("//", StringComparison.Ordinal))
            id = id.Replace("//", "/", StringComparison.Ordinal);
        if (id.StartsWith("@", StringComparison.Ordinal))
            id = id[1..];
        // /projects/@alice/Buster style → alice/Buster
        if (id.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
            id = id["projects/".Length..];
        return id.Trim('/');
    }

    private static void ValidateProjectId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            string.Equals(id, "workspace.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid project id: {id}");
        if (id.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid project id: {id}");
        var parts = id.Split('/');
        if (parts.Length is < 1 or > 2)
            throw new InvalidOperationException($"Invalid project id: {id}");
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
                throw new InvalidOperationException($"Invalid project id: {id}");
            // Allow normal slug chars only inside each segment
            foreach (var ch in part)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                    throw new InvalidOperationException($"Invalid project id: {id}");
            }
        }
    }

    /// <summary>Map id → disk path (does not require directory to exist).</summary>
    private string ResolveProjectDirPath(string normalizedId)
    {
        var projects = Path.Combine(WorkspaceRoot, "projects");
        var parts = normalizedId.Split('/');
        if (parts.Length == 2)
            return Path.Combine(projects, parts[0], parts[1]);
        // Legacy flat; also try nested scan if flat missing (caller checks Exists)
        var flat = Path.Combine(projects, parts[0]);
        if (Directory.Exists(flat))
            return flat;
        // Slow path: find projects/*/slug when id is bare slug stored under a user folder
        if (Directory.Exists(projects))
        {
            foreach (var ownerDir in Directory.GetDirectories(projects))
            {
                var candidate = Path.Combine(ownerDir, parts[0]);
                if (File.Exists(Path.Combine(candidate, "project.json")))
                    return candidate;
            }
        }
        return flat;
    }

    /// <summary>
    /// Sanitize a project id for import while preserving an "owner/slug" split, unlike
    /// <see cref="SanitizeProjectIdPublic"/> — which collapses any "/" into "_", flattening a
    /// namespaced id into a single segment that no longer matches the
    /// <c>projects/{owner}/{slug}/</c> layout <see cref="GetProjectDir"/>, <see cref="ListProjectsAsync"/>,
    /// etc. all expect. Also runs the id through <see cref="NormalizeProjectId"/> first, so an
    /// unnormalized "%2F"-encoded id (e.g. from an export filename/zip entry produced before that
    /// was fixed) round-trips to the correct two-segment id instead of "%"/"2F" being mangled.
    /// </summary>
    public static string SanitizeComposeProjectIdPublic(string raw)
    {
        var normalized = NormalizeProjectId(raw ?? "");
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            var owner = SanitizeUserSegment(parts[0]);
            var slug = SanitizeProjectIdPublic(parts[1]);
            if (owner.Length > 0 && slug.Length > 0)
                return $"{owner}/{slug}";
        }
        return SanitizeProjectIdPublic(normalized);
    }

    /// <summary>Sanitize owner handle for a single path segment (no slashes).</summary>
    public static string SanitizeUserSegment(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "";
        var s = userId.Trim();
        if (s.StartsWith('@')) s = s[1..];
        return SanitizeProjectIdPublic(s).ToLowerInvariant();
    }

    public Task<string> GetProjectDirAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetProjectDir(projectId));
    }

    public Task<string?> FindBlueprintPathAsync(string projectId, CancellationToken ct = default) =>
        _readCache.GetOrFindBlueprintPathAsync(
            projectId,
            c => FindBlueprintPathCoreAsync(projectId, c),
            ct);

    private async Task<string?> FindBlueprintPathCoreAsync(string projectId, CancellationToken ct)
    {
        var dir = GetProjectDir(projectId);
        var configPath = Path.Combine(dir, "pipeline_config.json");
        var name = "blueprint.clips.grok.json";
        if (File.Exists(configPath))
        {
            try
            {
                await using var stream = File.OpenRead(configPath);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("blueprint_file", out var bf))
                {
                    var n = bf.GetString();
                    if (!string.IsNullOrWhiteSpace(n))
                        name = n;
                }
            }
            catch { /* ignore */ }
        }
        return ResolveBlueprintCandidate(dir, name);
    }

    /// <summary>
    /// True-sync blueprint path for residual character/WIP helpers (no GetAwaiter, no cache).
    /// Prefer <see cref="FindBlueprintPathAsync"/> on request / job paths.
    /// </summary>
    private string? FindBlueprintPathSync(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var configPath = Path.Combine(dir, "pipeline_config.json");
        var name = "blueprint.clips.grok.json";
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("blueprint_file", out var bf))
                {
                    var n = bf.GetString();
                    if (!string.IsNullOrWhiteSpace(n))
                        name = n;
                }
            }
            catch { /* ignore */ }
        }
        return ResolveBlueprintCandidate(dir, name);
    }

    private static string? ResolveBlueprintCandidate(string dir, string preferredName)
    {
        foreach (var candidate in new[]
                 {
                     preferredName,
                     "blueprint.clips.grok.json",
                 })
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    /// <summary>
    /// Owned blueprint document (caller should dispose). Prefer shared cache on hot paths
    /// when read caches are enabled.
    /// </summary>
    public async Task<JsonDocument?> LoadBlueprintAsync(string projectId, CancellationToken ct = default)
    {
        if (!_opts.EnableReadCaches)
            return await LoadBlueprintUncachedAsync(projectId, ct).ConfigureAwait(false);
        var shared = await LoadBlueprintSharedAsync(projectId, ct).ConfigureAwait(false);
        return ProjectReadCache.CloneBlueprintDocument(shared);
    }

    /// <summary>
    /// Shared cached blueprint — <b>do not dispose</b>. Invalidated on gen/remux/config/blueprint write.
    /// When <see cref="PageToMovieOptions.EnableReadCaches"/> is false, returns null (use owned load).
    /// </summary>
    public async Task<JsonDocument?> LoadBlueprintSharedAsync(
        string projectId,
        CancellationToken ct = default)
    {
        if (!_opts.EnableReadCaches)
            return null;
        var path = await FindBlueprintPathAsync(projectId, ct).ConfigureAwait(false);
        return await _readCache.GetOrLoadBlueprintDocumentAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Load the blueprint for a read pass: the shared cached document when read caches are
    /// enabled (never disposed), otherwise a freshly-parsed owned document. The returned
    /// <c>owned</c> is non-null only when the caller must dispose it (in a finally).
    /// </summary>
    private async Task<(JsonDocument? bp, JsonDocument? owned)> LoadBlueprintForReadAsync(
        string projectId, CancellationToken ct)
    {
        if (_opts.EnableReadCaches)
            return (await LoadBlueprintSharedAsync(projectId, ct).ConfigureAwait(false), null);
        var owned = await LoadBlueprintUncachedAsync(projectId, ct).ConfigureAwait(false);
        return (owned, owned);
    }

    /// <summary>Always disk+parse; caller owns and must dispose.</summary>
    private async Task<JsonDocument?> LoadBlueprintUncachedAsync(string projectId, CancellationToken ct)
    {
        var path = await FindBlueprintPathCoreAsync(projectId, ct).ConfigureAwait(false);
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return JsonDocument.Parse(bytes);
        }
        catch { return null; }
    }

    /// <summary>
    /// True-sync owned blueprint load for residual helpers (no GetAwaiter, no cache).
    /// Caller owns and must dispose. Prefer <see cref="LoadBlueprintAsync"/> elsewhere.
    /// </summary>
    private JsonDocument? LoadBlueprintSync(string projectId)
    {
        var path = FindBlueprintPathSync(projectId);
        if (path is null || !File.Exists(path))
            return null;
        try { return JsonDocument.Parse(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    /// <summary>Whether scene-list + project/blueprint/dir caches are active.</summary>
    public bool ReadCachesEnabled => _opts.EnableReadCaches;
    public ProjectReadCache ReadCache => _readCache;

    public string ConfigPath(string projectId) =>
        Path.Combine(GetProjectDir(projectId), "pipeline_config.json");

    public string GetScreenplayPath(string projectId) =>
        ScreenplayService.GetDraftPath(this, projectId);

    public string GetCastPath(string projectId) =>
        ScreenplayService.GetCastSeedsPath(this, projectId);

    public string GetScenesPath(string projectId) =>
        ResolveScenesJsonPath(projectId);

    /// <summary>
    /// True-sync config read for residual helpers (no GetAwaiter). Prefer <see cref="GetConfigAsync"/>.
    /// </summary>
    private Dictionary<string, JsonElement> GetConfigSync(string projectId)
    {
        var path = ConfigPath(projectId);
        if (!File.Exists(path))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.EnumerateObject())
            dict[p.Name] = p.Value.Clone();
        return dict;
    }

    public async Task<Dictionary<string, JsonElement>> GetConfigAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var path = ConfigPath(projectId);
        if (!File.Exists(path))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var doc = await _readCache.GetOrLoadJsonDocumentAsync(path, ct).ConfigureAwait(false);
        if (doc is null)
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.EnumerateObject())
            dict[p.Name] = p.Value.Clone();
        return dict;
    }

    public async Task<Dictionary<string, JsonElement>> SaveConfigAsync(
        string projectId,
        JsonElement updates,
        CancellationToken ct = default)
    {
        var path = ConfigPath(projectId);
        Dictionary<string, object?> merged = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            var existing = await _readCache.GetOrLoadJsonDocumentAsync(path, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                foreach (var p in existing.RootElement.EnumerateObject())
                    merged[p.Name] = p.Value.Deserialize<object>();
            }
        }

        if (updates.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in updates.EnumerateObject())
                merged[p.Name] = p.Value.Deserialize<object>();
        }

        var json = JsonSerializer.Serialize(merged, JsonDefaults.Indented);
        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
        // Blueprint path may have changed via blueprint_file
        InvalidateSceneListCache(projectId);
        TriggerAutoGitCommit(projectId, "Update pipeline config");
        return await GetConfigAsync(projectId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Character seeds from blueprint, falling back to Stage 1 scenes.json.
    /// </summary>
    /// <summary>
    /// Normalized tokens (e.g. "CHARLOTTE", "BUSTER") of every character who actually speaks a line in the
    /// screenplay — a Fountain Character cue followed by non-empty dialogue. Lets the cast tell talking roles
    /// (including talking animals) from silent ones, so voice is keyed on speaking, not species alone.
    /// </summary>
    private HashSet<string> ReadScreenplaySpeakerTokens(string projectId)
    {
        var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var fountainPath = GetScreenplayPath(projectId);
            if (!File.Exists(fountainPath)) return speakers;
            var parsed = FountainParser.Parse(File.ReadAllText(fountainPath));
            string? pending = null;
            foreach (var el in parsed.Elements)
            {
                switch (el.Type)
                {
                    case FountainParser.ElementType.Character:
                        var name = el.Text ?? "";
                        var paren = name.IndexOf('(');          // drop "(V.O.)" / "(CONT'D)" extension
                        if (paren >= 0) name = name[..paren];
                        pending = CastKindClassifier.NormalizeToken(name);
                        break;
                    case FountainParser.ElementType.Parenthetical:
                        break;                                  // keeps the pending speaker
                    case FountainParser.ElementType.Dialogue:
                        if (!string.IsNullOrWhiteSpace(pending) && !string.IsNullOrWhiteSpace(el.Text))
                            speakers.Add(pending!);
                        break;
                    default:
                        pending = null;
                        break;
                }
            }
        }
        catch { /* no speakers derivable */ }
        return speakers;
    }

    public IReadOnlyList<CharacterSummary> ListCharacters(string projectId)
    {
        var seeds = LoadCharacterSeeds(projectId);
        var projectDir = GetProjectDir(projectId);
        var speakerTokens = ReadScreenplaySpeakerTokens(projectId);
        var rows = new List<CharacterSummary>();
        foreach (var (key, info) in seeds)
        {
            var voiceOnly = IsVoiceOnly(key, info);
            var display = info.TryGetProperty("canonical_given_name", out var cn) &&
                          cn.GetString() is { Length: > 0 } cname
                ? cname
                : (info.TryGetProperty("voice_label", out var vl) && vl.GetString() is { Length: > 0 } lab
                    ? lab
                    : key.Replace("Character_", "").Replace("_", " "));
            var descPreview = info.TryGetProperty("description", out var d0) ? d0.GetString() ?? "" : "";
            var castKindRaw = info.TryGetProperty("cast_kind", out var ck0) ? ck0.GetString() : null;
            var isGroup = !voiceOnly && CastKindClassifier.IsGroup(key, display, castKindRaw, descPreview);
            var castKind = voiceOnly ? "voice_only" : (isGroup ? "group" : "individual");

            var refName = CharacterRefFileName(key);
            var resolvedRef = voiceOnly ? null : ResolveCharacterRefPath(projectId, key, allowNormalizedFallback: false);
            var hasRef = resolvedRef is not null;
            if (hasRef && resolvedRef is not null)
                refName = Path.GetFileName(resolvedRef);

            // Plates come only from seed design_reference_images (scenes.json / mirrored blueprint).
            // Never invent plates from free-form book_images or untracked disk bookrefs.
            var bookRefs = CollectSeedPlatePaths(info);

            var wardrobe = new List<string>();
            if (info.TryGetProperty("wardrobe_always", out var wa) &&
                wa.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in wa.EnumerateArray())
                {
                    var s = x.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        wardrobe.Add(s!);
                }
            }

            var bookRefImages = new List<CharacterImageRef>();
            if (!voiceOnly)
            {
                for (var i = 0; i < bookRefs.Count; i++)
                {
                    var rel = bookRefs[i].Replace('\\', '/');
                    var full = ResolveProjectRelativePath(projectDir, rel);
                    // Same filename under assets/characters if seed path moved
                    if (full is null || !File.Exists(full))
                    {
                        var byName = Path.Combine(projectDir, "assets", "characters", Path.GetFileName(rel));
                        if (File.Exists(byName))
                        {
                            full = byName;
                            rel = Path.GetRelativePath(projectDir, byName).Replace('\\', '/');
                        }
                    }
                    var exists = full is not null && File.Exists(full);
                    bookRefImages.Add(new CharacterImageRef
                    {
                        Index = i,
                        RelativePath = rel,
                        FileName = Path.GetFileName(rel),
                        Exists = exists,
                        Url = exists
                            ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/bookrefs/{i}"
                            : null,
                    });
                }
            }

            var variants = new List<CharacterImageRef>();
            if (!voiceOnly)
            {
                for (var idx = 1; idx <= 3; idx++)
                {
                    var fileName = $"{key.ToLowerInvariant()}_variant_0{idx}.png";
                    var full = Path.Combine(projectDir, "assets", "characters", fileName);
                    var exists = File.Exists(full) && new FileInfo(full).Length > 64;
                    variants.Add(new CharacterImageRef
                    {
                        Index = idx,
                        RelativePath = $"assets/characters/{fileName}",
                        FileName = fileName,
                        Exists = exists,
                        Url = exists
                            ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/variants/{idx}"
                            : null,
                    });
                }
            }

            var hasPreferred = hasRef;
            string? preferredLabel = hasRef ? "locked" : null;
            string? preferredUrl = hasRef
                ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/ref"
                : null;
            if (!hasPreferred && !voiceOnly)
            {
                var v1 = Path.Combine(projectDir, "assets", "characters",
                    $"{key.ToLowerInvariant()}_variant_01.png");
                if (File.Exists(v1) && new FileInfo(v1).Length >= 64)
                {
                    hasPreferred = true;
                    preferredLabel = "best so far (variant 1)";
                    preferredUrl =
                        $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/variants/1";
                }
            }

            rows.Add(new CharacterSummary
            {
                Key = key,
                DisplayName = display,
                Description = info.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                VisualLock = info.TryGetProperty("visual_lock", out var v) ? v.GetString() ?? "" : "",
                VoiceProfile = info.TryGetProperty("voice_profile", out var vp) ? vp.GetString() ?? "" : "",
                VoiceLabel = info.TryGetProperty("voice_label", out var vlab) ? vlab.GetString() ?? "" : "",
                SpeciesKind = info.TryGetProperty("species_kind", out var spk) ? spk.GetString() : null,
                Speaks = speakerTokens.Contains(CastKindClassifier.NormalizeToken(key))
                    || (!string.IsNullOrWhiteSpace(display) && speakerTokens.Contains(CastKindClassifier.NormalizeToken(display))),
                HasVoiceCloneSample = File.Exists(GetVoiceCloneSamplePath(projectId, key)),
                VoiceCloneFileName = File.Exists(GetVoiceCloneSamplePath(projectId, key))
                    ? Path.GetFileName(GetVoiceCloneSamplePath(projectId, key))
                    : null,
                VoiceCloneUrl = File.Exists(GetVoiceCloneSamplePath(projectId, key))
                    ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/voice/clone-sample"
                    : null,
                VoiceProvider = info.TryGetProperty("voice_provider", out var vprov) ? vprov.GetString() : null,
                VoiceProviderVoiceId = info.TryGetProperty("voice_provider_voice_id", out var vpid)
                    ? vpid.GetString()
                    : null,
                VoiceOnly = voiceOnly,
                IsGroup = isGroup,
                CastKind = castKind,
                Locked = voiceOnly
                    ? !string.IsNullOrWhiteSpace(
                        info.TryGetProperty("voice_profile", out var vpr) ? vpr.GetString() : null)
                    : hasRef,
                RefFileName = hasRef ? refName : null,
                RefUrl = hasRef
                    ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/ref"
                    : null,
                HasPreferred = hasPreferred,
                PreferredLabel = preferredLabel,
                PreferredUrl = preferredUrl,
                WardrobeAlways = wardrobe,
                DesignReferenceImages = bookRefs,
                BookRefs = bookRefImages,
                Variants = variants,
                AgeBand = info.TryGetProperty("age_band", out var ab) ? ab.GetString() : null,
                VariantOf = info.TryGetProperty("variant_of", out var vo) ? vo.GetString() : null,
            });
        }

        return rows
            .OrderBy(r => r.Key.EndsWith("_Young") ? 1 : r.Key.EndsWith("_Teen") ? 2 : 0)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Location seeds from Stage‑1 / fountain import (location_seed_tokens in cast_seeds, blueprint, or scenes).
    /// When cast_seeds omits locations (common today), derive from the approved Fountain Stage‑1 model.
    /// </summary>
    public IReadOnlyList<LocationSummary> ListLocations(string projectId)
    {
        var seeds = LoadLocationSeeds(projectId);
        if (seeds.Count == 0)
            seeds = DeriveLocationSeedsFromFountain(projectId);

        var rows = new List<LocationSummary>();
        foreach (var (key, info) in seeds)
        {
            var display = info.TryGetProperty("display_name", out var dn) && dn.GetString() is { Length: > 0 } dname
                ? dname
                : key.Replace('_', ' ').Trim();
            var desc = info.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var vlock = info.TryGetProperty("visual_lock", out var v) ? v.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(desc) && !string.IsNullOrWhiteSpace(vlock))
                desc = vlock;
            if (string.IsNullOrWhiteSpace(vlock) && !string.IsNullOrWhiteSpace(desc))
                vlock = desc;
            rows.Add(new LocationSummary
            {
                Key = key,
                DisplayName = display,
                Description = desc,
                VisualLock = vlock,
            });
        }

        // If seeds were name-only stubs, re-derive from fountain prose and fill blanks.
        if (rows.Count > 0 && rows.All(r =>
                string.IsNullOrWhiteSpace(r.Description)
                || r.Description.Equals(r.DisplayName, StringComparison.OrdinalIgnoreCase)
                || r.Description.Equals(r.Key, StringComparison.OrdinalIgnoreCase)))
        {
            var derived = DeriveLocationSeedsFromFountain(projectId);
            foreach (var row in rows)
            {
                if (!derived.TryGetValue(row.Key, out var el)
                    && !derived.TryGetValue(row.Key.Replace(' ', '_'), out el))
                {
                    // match by display name
                    var hit = derived.FirstOrDefault(kv =>
                        kv.Value.TryGetProperty("display_name", out var dn)
                        && string.Equals(dn.GetString(), row.DisplayName, StringComparison.OrdinalIgnoreCase));
                    if (hit.Key is null) continue;
                    el = hit.Value;
                }
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (el.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } desc2)
                    row.Description = desc2;
                if (el.TryGetProperty("visual_lock", out var v) && v.GetString() is { Length: > 0 } vl2)
                    row.VisualLock = vl2;
            }
        }

        return rows
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Build location_seed_tokens from the current Fountain Stage‑1 model (no extra AI call).
    /// Enriched with action-line prose when available.
    /// </summary>
    public Dictionary<string, JsonElement> DeriveLocationSeedsFromFountain(string projectId)
    {
        try
        {
            var model = ScreenplayService.TryBuildModelFromProject(this, projectId);
            if (model is null) return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            if (model.TryGetValue("global_production_variables", out var gpvObj)
                && gpvObj is Dictionary<string, object?> gpv
                && gpv.TryGetValue("location_seed_tokens", out var locObj)
                && locObj is Dictionary<string, object?> locDict
                && locDict.Count > 0)
            {
                var json = JsonSerializer.Serialize(locDict);
                using var doc = JsonDocument.Parse(json);
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                return dict;
            }
        }
        catch { /* ignore */ }
        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Persist Stage‑1 location seeds into <c>source/cast_seeds.json</c> so
    /// <see cref="ListLocations"/> works after cast extract (characters only used to be written).
    /// Merges without wiping character_seed_tokens.
    /// </summary>
    public bool MergeLocationSeedsIntoCastFile(string projectId, Dictionary<string, object?>? locationSeeds = null)
    {
        try
        {
            locationSeeds ??= ExtractLocationSeedObjects(projectId);
            if (locationSeeds is null || locationSeeds.Count == 0) return false;

            var castPath = Path.Combine(GetProjectDir(projectId), "source", ScreenplayService.CastSeedsFileName);
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(castPath))
            {
                var text = File.ReadAllText(castPath);
                root = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(castPath)!);
                root = new System.Text.Json.Nodes.JsonObject
                {
                    ["schema_version"] = "cast_seeds.v1",
                    ["generation"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["method"] = "MergeLocationSeedsIntoCastFile",
                        ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    },
                };
            }

            var existing = root["location_seed_tokens"] as System.Text.Json.Nodes.JsonObject
                           ?? new System.Text.Json.Nodes.JsonObject();

            foreach (var (key, val) in locationSeeds)
            {
                if (val is not Dictionary<string, object?> incoming) continue;
                var incomingNode = System.Text.Json.Nodes.JsonNode.Parse(
                    JsonSerializer.Serialize(incoming)) as System.Text.Json.Nodes.JsonObject;
                if (incomingNode is null) continue;

                if (existing[key] is not System.Text.Json.Nodes.JsonObject cur)
                {
                    existing[key] = incomingNode;
                    continue;
                }

                var display = cur["display_name"]?.GetValue<string>()
                              ?? incomingNode["display_name"]?.GetValue<string>()
                              ?? key;
                foreach (var field in new[] { "description", "visual_lock" })
                {
                    var curV = cur[field]?.GetValue<string>() ?? "";
                    var inV = incomingNode[field]?.GetValue<string>() ?? "";
                    var stub = string.IsNullOrWhiteSpace(curV)
                               || curV.Equals(display, StringComparison.OrdinalIgnoreCase)
                               || curV.Equals(key, StringComparison.OrdinalIgnoreCase);
                    if (stub && !string.IsNullOrWhiteSpace(inV) && inV.Length > curV.Length)
                        cur[field] = inV;
                }
                if (cur["display_name"] is null && incomingNode["display_name"] is not null)
                    cur["display_name"] = incomingNode["display_name"]!.DeepClone();
                if (cur["location_type"] is null && incomingNode["location_type"] is not null)
                    cur["location_type"] = incomingNode["location_type"]!.DeepClone();
            }

            root["location_seed_tokens"] = existing;
            File.WriteAllText(castPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Dictionary<string, object?>? ExtractLocationSeedObjects(string projectId)
    {
        try
        {
            var model = ScreenplayService.TryBuildModelFromProject(this, projectId);
            if (model is null) return null;
            if (model.TryGetValue("global_production_variables", out var gpvObj)
                && gpvObj is Dictionary<string, object?> gpv
                && gpv.TryGetValue("location_seed_tokens", out var locObj)
                && locObj is Dictionary<string, object?> locDict
                && locDict.Count > 0)
                return locDict;
        }
        catch { /* ignore */ }
        return null;
    }

    public string? ResolveCharacterRefPath(string projectId, string charKey, bool allowNormalizedFallback = true)
    {
        // Voice-only roles never have (or need) a portrait. The enumerate-and-match itself — exact
        // candidate filenames then the normalized-key fallback — is the single implementation in
        // ClipVideoPromptBuilder.ResolveCharacterRefPathPublic, which FilmJobService also calls, so
        // the two gates cannot drift apart. allowNormalizedFallback:false (cast listing) skips the
        // normalized scan so Character_Narrator / Character_The_Narrator don't share one *_ref.png.
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(charKey, info))
            return null;

        return ClipVideoPromptBuilder.ResolveCharacterRefPathPublic(
            GetProjectDir(projectId), charKey, allowNormalizedFallback);
    }

    /// <summary>
    /// On-screen cast keys for a scene that are not voice-only and have no locked ref image.
    /// </summary>
    public IReadOnlyList<string> GetUnlockedOnScreenCharacters(string projectId, int sceneNumber)
    {
        using var bp = LoadBlueprintSync(projectId);
        if (bp is null)
            return Array.Empty<string>();

        JsonElement? sceneEl = null;
        if (bp.RootElement.TryGetProperty("scenes", out var scenes) &&
            scenes.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scenes.EnumerateArray())
            {
                if (s.TryGetProperty("scene_number", out var sn) && sn.TryGetInt32(out var n) && n == sceneNumber)
                {
                    sceneEl = s.Clone();
                    break;
                }
            }
        }

        if (sceneEl is null)
            return Array.Empty<string>();

        var cast = new HashSet<string>(StringComparer.Ordinal);
        if (sceneEl.Value.TryGetProperty("characters_on_screen", out var cos) &&
            cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
            {
                var k = x.GetString();
                if (!string.IsNullOrWhiteSpace(k))
                    cast.Add(k!);
            }
        }

        // Also scan prompts for Character_* mentions (clip-local cast)
        if (sceneEl.Value.TryGetProperty("veo_clips", out var clips) &&
            clips.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clips.EnumerateArray())
            {
                if (!c.TryGetProperty("visual_prompt", out var vp))
                    continue;
                var text = vp.GetString() ?? "";
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(text, @"Character_[A-Za-z0-9_]+"))
                {
                    if (m.Success)
                        cast.Add(m.Value);
                }
            }
        }

        var unlocked = new List<string>();
        foreach (var key in cast.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var seed = GetCharacterSeed(projectId, key);
            if (seed is not null && IsVoiceOnly(key, seed.Value))
                continue;
            // Group / ensemble cast (e.g. "Children", "Crowd") have no single portrait identity —
            // the operator can't pick one image for them and shouldn't be forced to. The video model
            // renders group members freely, so a group never requires a locked reference. This mirrors
            // the client readiness gates, which already skip IsGroup, and uses the same
            // CastKindClassifier signal so it generalizes across books/casts (not a name hardcode).
            if (IsGroupSeed(key, seed))
                continue;
            // Unknown seed still counts as needing a lock if mentioned on-screen
            if (ResolveCharacterRefPath(projectId, key) is null)
                unlocked.Add(key);
        }

        return unlocked;
    }

    /// <summary>
    /// Canonical locked ref: <c>{character_key_lower}_ref.png</c>
    /// e.g. Character_Mom → character_mom_ref.png.
    /// </summary>
    public static string CharacterRefFileName(string charKey)
    {
        var k = (charKey ?? "").Trim().Replace(' ', '_').Replace('\\', '/');
        k = Path.GetFileName(k).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(k) || k is "." or "..")
            k = "unknown_character";
        if (k.EndsWith("_ref.png", StringComparison.OrdinalIgnoreCase))
            return k;
        return $"{k}_ref.png";
    }

    /// <summary>
    /// Candidate on-disk names for a locked ref (canonical + short aliases + common typos).
    /// </summary>
    public static IEnumerable<string> CharacterRefFileCandidates(string charKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileName(name.Trim().Replace(' ', '_')).ToLowerInvariant();
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = name.EndsWith("_ref", StringComparison.OrdinalIgnoreCase) ? name + ".png" : name + "_ref.png";
            if (seen.Add(name))
                list.Add(name);
        }

        Add(CharacterRefFileName(charKey));
        var raw = (charKey ?? "").Trim();
        var bare = raw.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
            ? raw["Character_".Length..]
            : raw;
        Add($"{bare}_ref.png");
        Add(bare);
        if (bare.StartsWith("The_", StringComparison.OrdinalIgnoreCase))
        {
            var noThe = bare["The_".Length..];
            Add($"character_{noThe}_ref.png");
            Add($"{noThe}_ref.png");
        }
        // Dad / Daddy alias
        if (bare.Equals("Dad", StringComparison.OrdinalIgnoreCase) ||
            bare.Equals("Daddy", StringComparison.OrdinalIgnoreCase))
        {
            Add("character_daddy_ref.png");
            Add("character_dad_ref.png");
            Add("daddy_ref.png");
            Add("dad_ref.png");
        }
        if (bare.Equals("Mom", StringComparison.OrdinalIgnoreCase) ||
            bare.Equals("Mum", StringComparison.OrdinalIgnoreCase))
        {
            Add("character_mom_ref.png");
            Add("mom_ref.png");
        }
        return list;
    }

    /// <summary>Character seed token object from blueprint/scenes, or null.</summary>
    public JsonElement? GetCharacterSeed(string projectId, string charKey)
    {
        var seeds = LoadCharacterSeeds(projectId);
        return seeds.TryGetValue(charKey, out var info) ? info : null;
    }

    /// <summary>All character seed tokens for the project (cast_seeds preferred).</summary>
    public IReadOnlyDictionary<string, JsonElement> GetAllCharacterSeeds(string projectId) =>
        LoadCharacterSeeds(projectId);

    /// <summary>
    /// Shared wardrobe/uniform lock token (see <c>wardrobe_lock_tokens</c> in cast_seeds.json) —
    /// lets several characters (e.g. three police officers) point at one costume description
    /// and one shared reference plate instead of each re-describing/re-generating the uniform.
    /// </summary>
    public JsonElement? GetWardrobeLock(string projectId, string wardrobeKey)
    {
        var locks = LoadWardrobeLocks(projectId);
        return locks.TryGetValue(wardrobeKey, out var info) ? info : null;
    }

    /// <summary>All wardrobe lock tokens for the project.</summary>
    public IReadOnlyDictionary<string, JsonElement> GetAllWardrobeLocks(string projectId) =>
        LoadWardrobeLocks(projectId);

    /// <summary>
    /// Resolves a character's <c>wardrobe_lock</c> pointer (if set) to the shared group token.
    /// Null when the character has no wardrobe lock or it points at an unknown key.
    /// </summary>
    public JsonElement? ResolveCharacterWardrobeLock(string projectId, string charKey)
    {
        var seed = GetCharacterSeed(projectId, charKey);
        if (seed is null ||
            !seed.Value.TryGetProperty("wardrobe_lock", out var wl) ||
            wl.ValueKind != JsonValueKind.String)
            return null;
        var key = wl.GetString();
        return string.IsNullOrWhiteSpace(key) ? null : GetWardrobeLock(projectId, key!);
    }

    /// <summary>Character's raw <c>wardrobe_lock</c> key (foreign key into wardrobe_lock_tokens), or null.</summary>
    public static string? GetWardrobeLockKey(JsonElement characterSeed) =>
        characterSeed.TryGetProperty("wardrobe_lock", out var wl) && wl.ValueKind == JsonValueKind.String
            ? wl.GetString()
            : null;

    /// <summary>
    /// Canonical shared costume reference file: <c>wardrobe_{key}_ref.png</c>
    /// e.g. Wardrobe_PoliceOfficer → wardrobe_policeofficer_ref.png.
    /// </summary>
    public static string WardrobeRefFileName(string wardrobeKey)
    {
        var k = (wardrobeKey ?? "").Trim().Replace(' ', '_').Replace('\\', '/');
        k = Path.GetFileName(k).ToLowerInvariant();
        k = k.StartsWith("wardrobe_", StringComparison.OrdinalIgnoreCase) ? k : $"wardrobe_{k}";
        if (string.IsNullOrWhiteSpace(k) || k is "." or "..")
            k = "wardrobe_unknown";
        return k.EndsWith("_ref.png", StringComparison.OrdinalIgnoreCase) ? k : $"{k}_ref.png";
    }

    /// <summary>Existing shared costume reference path for a wardrobe group, or null if not generated yet.</summary>
    public string? ResolveWardrobeRefPath(string projectId, string wardrobeKey)
    {
        var path = Path.Combine(
            GetProjectDir(projectId), "assets", "characters", WardrobeRefFileName(wardrobeKey));
        return File.Exists(path) && new FileInfo(path).Length >= 64 ? path : null;
    }

    /// <summary>
    /// Max multi-ref image seeds for Characters UI, based on image_provider / image_model_name.
    /// </summary>
    public ImageSeedLimits GetImageSeedLimits(string projectId)
    {
        var cfg = GetConfigSync(projectId);
        string? model = null;
        string? provider = null;
        if (cfg.TryGetValue("image_model_name", out var m) && m.ValueKind == JsonValueKind.String)
            model = m.GetString();
        if (cfg.TryGetValue("image_provider", out var p) && p.ValueKind == JsonValueKind.String)
            provider = p.GetString();

        model ??= _opts.DefaultImageModel;
        provider ??= _opts.ImageProvider;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Image seed limits: no image model selected. Open Settings and choose an Image generation model.");
        var resolved = ImageApiLimits.ResolveProvider(provider, model);
        return new ImageSeedLimits
        {
            Provider = resolved,
            ImageModel = model,
            MaxReferenceImages = ImageApiLimits.MaxReferenceImages(resolved, model),
        };
    }

    /// <summary>
    /// Provider voice id from a previous <see cref="PageToMovie.Engine.Abstractions.IVoiceCloneClient.CloneVoiceAsync"/>
    /// call (e.g. MiniMax <c>custom_voice_id</c>), read straight from cast_seeds.json's
    /// <c>voice_clone_provider_id</c> field. Null when no clone has been run yet (or the sample
    /// changed since — callers should re-clone if the sample was replaced). <paramref name="charKey"/>
    /// need not be an on-screen cast member — a narration flow can reuse this same per-character
    /// storage under a caller-chosen pseudo-character key (e.g. "Narrator").
    /// </summary>
    public string? GetVoiceCloneProviderId(string projectId, string charKey)
    {
        try
        {
            var seed = GetCharacterSeed(projectId, charKey);
            if (seed is not { } el) return null;
            if (el.TryGetProperty("voice_clone_provider_id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String &&
                idEl.GetString() is { Length: > 0 } id)
                return id;
            // Interop with ElevenLabs apply-clone path (voice_provider_voice_id).
            if (el.TryGetProperty("voice_provider_voice_id", out var altEl) &&
                altEl.ValueKind == JsonValueKind.String &&
                altEl.GetString() is { Length: > 0 } alt)
                return alt;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Catalog provider id stored on the character seed after clone apply
    /// (<c>voice_provider</c>, e.g. elevenlabs / fal). Null when never cloned.
    /// </summary>
    public string? GetVoiceProviderId(string projectId, string charKey)
    {
        try
        {
            var seed = GetCharacterSeed(projectId, charKey);
            if (seed is not { } el) return null;
            if (el.TryGetProperty("voice_provider", out var pEl) &&
                pEl.ValueKind == JsonValueKind.String &&
                pEl.GetString() is { Length: > 0 } p)
                return p.Trim();
            return null;
        }
        catch
        {
            return null;
        }
    }

    public string GetCharactersDir(string projectId) =>
        Path.Combine(GetProjectDir(projectId), "assets", "characters");

    public string GetCharacterDir(string projectId, string charKey) =>
        Path.Combine(GetCharactersDir(projectId), SanitizeCharKey(charKey));

    /// <summary>Absolute path for optional voice-clone template audio (mic or upload).</summary>
    public string GetVoiceCloneSamplePath(string projectId, string charKey)
    {
        var dir = GetCharacterDir(projectId, charKey);
        foreach (var name in new[] { "voice_clone_sample.webm", "voice_clone_sample.mp3", "voice_clone_sample.wav", "voice_clone_sample.m4a", "voice_clone_sample.ogg" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(dir, "voice_clone_sample.webm");
    }

    private static string SanitizeCharKey(string charKey)
    {
        var k = (charKey ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            k = k.Replace(c, '_');
        return string.IsNullOrEmpty(k) ? "character" : k;
    }

    /// <summary>
    /// Save operator mic/upload audio as the voice-clone template for this character.
    /// Ext from file name; max 15 MB. Updates cast_seeds voice_clone_sample filename.
    /// </summary>
    public async Task<string> SaveVoiceCloneSampleAsync(
        string projectId,
        string charKey,
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(charKey))
            throw new InvalidOperationException("charKey required");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".webm" or ".mp3" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".mp4"))
            throw new InvalidOperationException("Use audio: webm, mp3, wav, m4a, or ogg.");
        if (ext == ".mp4") ext = ".webm"; // browser often labels wrong

        var dir = GetCharacterDir(projectId, charKey);
        Directory.CreateDirectory(dir);
        // Clear previous sample extensions
        foreach (var old in Directory.EnumerateFiles(dir, "voice_clone_sample.*"))
        {
            try { File.Delete(old); } catch { /* ignore */ }
        }
        var dest = Path.Combine(dir, "voice_clone_sample" + ext);
        await using (var fs = File.Create(dest))
            await content.CopyToAsync(fs, ct);

        UpdateCharacterSeedText(
            projectId,
            charKey,
            voiceCloneSample: Path.GetFileName(dest),
            // A new sample invalidates any previously cloned provider voice id — clear it so
            // narration/dialogue synthesis re-clones from the new sample instead of silently
            // reusing a voice built from the old one.
            voiceCloneProviderId: "");
        return dest;
    }

    public bool DeleteVoiceCloneSample(string projectId, string charKey)
    {
        var dir = GetCharacterDir(projectId, charKey);
        var removed = false;
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "voice_clone_sample.*"))
            {
                try { File.Delete(f); removed = true; } catch { /* ignore */ }
            }
        }
        if (removed)
            UpdateCharacterSeedText(projectId, charKey, voiceCloneSample: "", voiceCloneProviderId: "");
        return removed;
    }

    /// <summary>
    /// Update description / visual_lock / voice fields on character seeds in cast_seeds.json
    /// (and blueprint / scenes.json when present). Null args leave that field unchanged.
    /// </summary>
    public void UpdateCharacterSeedText(
        string projectId,
        string charKey,
        string? description = null,
        string? visualLock = null,
        string? voiceProfile = null,
        string? voiceLabel = null,
        string? voiceCloneSample = null,
        string? voiceProvider = null,
        string? voiceProviderVoiceId = null,
        string? voiceCloneProviderId = null)
    {
        void PatchSeedsObject(System.Text.Json.Nodes.JsonObject seeds)
        {
            var (seed, foundKey) = FindSeedByCharKey(seeds, charKey);
            if (seed is null || foundKey is null)
            {
                seed = new System.Text.Json.Nodes.JsonObject();
                foundKey = charKey;
                seeds[foundKey] = seed;
            }
            if (description is not null)
                seed["description"] = CharacterVisualTextScrubber.ScrubVisualProse(description);
            if (visualLock is not null)
                seed["visual_lock"] = CharacterVisualTextScrubber.ScrubVisualProse(visualLock);
            if (voiceProfile is not null)
                seed["voice_profile"] = voiceProfile.Trim();
            if (voiceLabel is not null)
                seed["voice_label"] = voiceLabel.Trim();
            if (voiceCloneSample is not null)
            {
                if (string.IsNullOrWhiteSpace(voiceCloneSample))
                    seed.Remove("voice_clone_sample");
                else
                    seed["voice_clone_sample"] = voiceCloneSample.Trim();
            }
            if (voiceProvider is not null)
            {
                if (string.IsNullOrWhiteSpace(voiceProvider))
                    seed.Remove("voice_provider");
                else
                    seed["voice_provider"] = voiceProvider.Trim();
            }
            if (voiceProviderVoiceId is not null)
            {
                if (string.IsNullOrWhiteSpace(voiceProviderVoiceId))
                    seed.Remove("voice_provider_voice_id");
                else
                    seed["voice_provider_voice_id"] = voiceProviderVoiceId.Trim();
            }
            if (voiceCloneProviderId is not null)
            {
                if (string.IsNullOrWhiteSpace(voiceCloneProviderId))
                    seed.Remove("voice_clone_provider_id");
                else
                    seed["voice_clone_provider_id"] = voiceCloneProviderId.Trim();
            }
            seeds[foundKey] = seed;
        }

        void PatchFile(string path, bool createCastShape)
        {
            try
            {
                System.Text.Json.Nodes.JsonObject root;
                if (File.Exists(path))
                {
                    root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                           as System.Text.Json.Nodes.JsonObject
                           ?? new System.Text.Json.Nodes.JsonObject();
                }
                else if (createCastShape)
                {
                    root = new System.Text.Json.Nodes.JsonObject { ["schema_version"] = "cast_seeds.v1" };
                }
                else return;

                System.Text.Json.Nodes.JsonObject? seeds;
                System.Text.Json.Nodes.JsonObject? gpv = null;
                if (root["character_seed_tokens"] is System.Text.Json.Nodes.JsonObject direct)
                {
                    seeds = direct;
                }
                else
                {
                    gpv = root["global_production_variables"] as System.Text.Json.Nodes.JsonObject
                          ?? new System.Text.Json.Nodes.JsonObject();
                    root["global_production_variables"] = gpv;
                    seeds = gpv["character_seed_tokens"] as System.Text.Json.Nodes.JsonObject
                            ?? new System.Text.Json.Nodes.JsonObject();
                    gpv["character_seed_tokens"] = seeds;
                }

                PatchSeedsObject(seeds);
                // Bug fix (pre-existing, hit by any brand-new cast_seeds.json write — e.g. the
                // first voice/clone call for a narration pseudo-character on a project with no
                // cast_seeds.json yet): a JsonNode instance can only have one parent, so the same
                // `seeds` object can't be assigned directly to both root and global_production_
                // variables. Mirror a separate parsed copy instead of the same reference — root[
                // "character_seed_tokens"] previously threw "node already has a parent" here,
                // which this method's catch-all silently swallowed, so the whole write was
                // dropped with no visible error.
                if (createCastShape && gpv is not null)
                    root["character_seed_tokens"] = System.Text.Json.Nodes.JsonNode.Parse(seeds.ToJsonString());
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, root.ToJsonString(JsonDefaults.Indented) + "\n");
            }
            catch
            {
                /* non-fatal */
            }
        }

        PatchFile(ScreenplayService.GetCastSeedsPath(this, projectId), createCastShape: true);
        var bp = FindBlueprintPathSync(projectId);
        if (bp is not null)
            PatchFile(bp, createCastShape: false);
        var scenesPath = ResolveScenesJsonPath(projectId);
        if (File.Exists(scenesPath))
            PatchFile(scenesPath, createCastShape: false);
        TriggerAutoGitCommit(projectId, "Update character seeds");
    }

    /// <summary>
    /// Update description / visual_lock on a shared wardrobe lock token (wardrobe_lock_tokens
    /// in cast_seeds.json, mirrored into blueprint / scenes.json) — same triple-file-sync
    /// pattern as <see cref="UpdateCharacterSeedText"/>, keyed on the wardrobe group instead of
    /// a single character. Editing this affects every character whose <c>wardrobe_lock</c>
    /// points at <paramref name="wardrobeKey"/>, but only for future generations — it does not
    /// touch already-locked character reference images.
    /// </summary>
    public void UpdateWardrobeLockText(
        string projectId,
        string wardrobeKey,
        string? description = null,
        string? visualLock = null)
    {
        void PatchLockObject(System.Text.Json.Nodes.JsonObject locks)
        {
            System.Text.Json.Nodes.JsonObject? entry = null;
            string? foundKey = null;
            foreach (var (k, v) in locks)
            {
                if (string.Equals(k, wardrobeKey, StringComparison.OrdinalIgnoreCase) &&
                    v is System.Text.Json.Nodes.JsonObject jo)
                {
                    entry = jo;
                    foundKey = k;
                    break;
                }
            }
            if (entry is null || foundKey is null)
            {
                entry = new System.Text.Json.Nodes.JsonObject();
                foundKey = wardrobeKey;
                locks[foundKey] = entry;
            }
            if (description is not null)
                entry["description"] = CharacterVisualTextScrubber.ScrubVisualProse(description);
            if (visualLock is not null)
                entry["visual_lock"] = CharacterVisualTextScrubber.ScrubVisualProse(visualLock);
            locks[foundKey] = entry;
        }

        void PatchFile(string path, bool createCastShape)
        {
            try
            {
                System.Text.Json.Nodes.JsonObject root;
                if (File.Exists(path))
                {
                    root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                           as System.Text.Json.Nodes.JsonObject
                           ?? new System.Text.Json.Nodes.JsonObject();
                }
                else if (createCastShape)
                {
                    root = new System.Text.Json.Nodes.JsonObject { ["schema_version"] = "cast_seeds.v1" };
                }
                else return;

                System.Text.Json.Nodes.JsonObject? locks;
                if (root["wardrobe_lock_tokens"] is System.Text.Json.Nodes.JsonObject direct)
                {
                    locks = direct;
                }
                else
                {
                    var gpv = root["global_production_variables"] as System.Text.Json.Nodes.JsonObject;
                    if (gpv is not null && gpv["wardrobe_lock_tokens"] is System.Text.Json.Nodes.JsonObject gpvLocks)
                    {
                        locks = gpvLocks;
                    }
                    else
                    {
                        locks = new System.Text.Json.Nodes.JsonObject();
                        root["wardrobe_lock_tokens"] = locks;
                    }
                }

                PatchLockObject(locks);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, root.ToJsonString(JsonDefaults.Indented) + "\n");
            }
            catch
            {
                /* non-fatal */
            }
        }

        PatchFile(ScreenplayService.GetCastSeedsPath(this, projectId), createCastShape: true);
        var bp = FindBlueprintPathSync(projectId);
        if (bp is not null)
            PatchFile(bp, createCastShape: false);
        var scenesPath = ResolveScenesJsonPath(projectId);
        if (File.Exists(scenesPath))
            PatchFile(scenesPath, createCastShape: false);
    }

    /// <summary>
    /// Map Character_* → { voice_profile, voice_label } for video prompt VOICE LOCK.
    /// Prefer scenes.json seeds (source of truth for voice edits), fall back to blueprint.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> LoadCharacterVoiceMap(string projectId)
    {
        var profiles = LoadCharacterPromptProfiles(projectId);
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, p) in profiles)
        {
            if (string.IsNullOrWhiteSpace(p.VoiceProfile) && string.IsNullOrWhiteSpace(p.VoiceLabel))
                continue;
            map[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["voice_profile"] = p.VoiceProfile,
                ["voice_label"] = p.VoiceLabel,
            };
        }
        return map;
    }

    /// <summary>
    /// Full character profiles for video CHARACTER VARIABLES (description, visual lock, voice).
    /// Prefer cast_seeds / scenes seeds; fall back to blueprint seeds.
    /// </summary>
    public Dictionary<string, ClipVideoPromptBuilder.CharacterProfile> LoadCharacterPromptProfiles(
        string projectId)
    {
        var map = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase);

        void Ingest(Dictionary<string, JsonElement> seeds, bool overwrite)
        {
            foreach (var (key, info) in seeds)
            {
                if (!overwrite && map.ContainsKey(key)) continue;
                var desc = info.TryGetProperty("description", out var d) ? (d.GetString() ?? "").Trim() : "";
                var castKind = info.TryGetProperty("cast_kind", out var ck) ? (ck.GetString() ?? "").Trim() : "";
                var vlock = info.TryGetProperty("visual_lock", out var vl) ? (vl.GetString() ?? "").Trim() : "";
                var profile = info.TryGetProperty("voice_profile", out var vp) ? (vp.GetString() ?? "").Trim() : "";
                var label = info.TryGetProperty("voice_label", out var vlab) ? (vlab.GetString() ?? "").Trim() : "";
                var display = info.TryGetProperty("canonical_given_name", out var cn)
                    ? (cn.GetString() ?? "").Trim()
                    : "";
                if (display.Length == 0)
                    display = key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
                // Prefer cast seed policy only — never force VOICE ONLY because key is "Narrator"
                // (on-camera confessor / POV roles are common and need locked face refs).
                // Shared mechanism: CastKindClassifier.IsVoiceOnlyPolicy.
                var voiceOnly =
                    info.TryGetProperty("display_name_policy", out var pol) &&
                    CastKindClassifier.IsVoiceOnlyPolicy(pol.GetString());
                if (desc.Length == 0 && vlock.Length == 0 && profile.Length == 0 && label.Length == 0)
                    continue;
                map[key] = new ClipVideoPromptBuilder.CharacterProfile
                {
                    Key = key,
                    DisplayName = display,
                    Description = desc,
                    VisualLock = vlock,
                    VoiceProfile = profile,
                    VoiceLabel = label,
                    VoiceOnly = voiceOnly,
                    CastKind = castKind,
                };
            }
        }

        // cast_seeds.json / scenes first so Characters page edits win
        try
        {
            var all = LoadCharacterSeeds(projectId);
            Ingest(all, overwrite: true);
        }
        catch { /* ignore */ }

        return map;
    }

    /// <summary>
    /// Remove one design_reference_images / book_reference_images entry by 0-based index
    /// from cast_seeds (and blueprint / scenes when present).
    /// </summary>
    public void RemoveCharacterBookRef(string projectId, string charKey, int index)
    {
        void PatchSeedsObject(System.Text.Json.Nodes.JsonObject seeds)
        {
            var (seed, foundKey) = FindSeedByCharKey(seeds, charKey);
            if (seed is null || foundKey is null) return;

            foreach (var prop in new[] { "design_reference_images", "book_reference_images" })
            {
                if (seed[prop] is not System.Text.Json.Nodes.JsonArray arr) continue;
                if (index < 0 || index >= arr.Count) continue;
                arr.RemoveAt(index);
            }
            seeds[foundKey] = seed;
        }

        PatchCharacterSeedsFile(ScreenplayService.GetCastSeedsPath(this, projectId), PatchSeedsObject);
        var bp = FindBlueprintPathSync(projectId);
        if (bp is not null) PatchCharacterSeedsFile(bp, PatchSeedsObject);
        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
    }

    /// <summary>
    /// Update design_reference_images for charKey in cast_seeds.json (and blueprint / scenes when present).
    /// Replaces reference image paths with up to 3 user-selected book image paths.
    /// </summary>
    public void SetCharacterBookRefs(string projectId, string charKey, IReadOnlyList<string> imagePaths)
    {
        var cleanPaths = imagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        void PatchSeedsObject(System.Text.Json.Nodes.JsonObject seeds)
        {
            var (seed, foundKey) = FindSeedByCharKey(seeds, charKey);
            if (seed is null || foundKey is null) return;

            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var p in cleanPaths)
            {
                arr.Add(System.Text.Json.Nodes.JsonValue.Create(p));
            }
            seed["design_reference_images"] = arr;
            seed["book_reference_images"] = arr.DeepClone();
            seeds[foundKey] = seed;
        }

        var dir = GetProjectDir(projectId);
        PatchCharacterSeedsFile(Path.Combine(dir, "source", ScreenplayService.CastSeedsFileName), PatchSeedsObject);
        PatchCharacterSeedsFile(Path.Combine(dir, "scenes.json"), PatchSeedsObject);
        if (FindBlueprintPathSync(projectId) is { } bpPath)
            PatchCharacterSeedsFile(bpPath, PatchSeedsObject);
        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
    }


    private static int ReadJsonNodeInt(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return -1;
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return int.TryParse(node.ToString(), out var n) ? n : -1;
        }
    }

    /// <summary>
    /// Patch <c>visual_prompt</c> on a Stage 2 blueprint clip (for auto-review apply + regen).
    /// </summary>
    public void UpdateClipVisualPrompt(string projectId, int scene, int clip, string visualPrompt)
    {
        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is null || !File.Exists(bpPath))
            throw new InvalidOperationException("Shot plan (blueprint) not found — cannot update clip prompt.");

        var (root, scenes) = ParseBlueprintScenes(bpPath);

        System.Text.Json.Nodes.JsonObject? clipObj = null;
        foreach (var sNode in scenes)
        {
            if (sNode is not System.Text.Json.Nodes.JsonObject s) continue;
            var sn = ReadJsonNodeInt(s["scene_number"]);
            if (sn != scene) continue;
            // Stage 2 blueprint uses veo_clips (canonical)
            var clips = s["veo_clips"] as System.Text.Json.Nodes.JsonArray
                        ?? s["clips"] as System.Text.Json.Nodes.JsonArray;
            if (clips is null) break;
            foreach (var cNode in clips)
            {
                if (cNode is not System.Text.Json.Nodes.JsonObject c) continue;
                var cn = ClipKeying.ClipNumber(c);
                if (cn != clip) continue;
                clipObj = c;
                break;
            }
            break;
        }

        if (clipObj is null)
            throw new InvalidOperationException($"Clip S{scene:D2}C{clip:D2} not found in shot plan.");

        clipObj["visual_prompt"] = (visualPrompt ?? "").Trim();
        File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        TriggerAutoGitCommit(projectId, "Update clip visual prompt");
    }

    private static System.Text.Json.Nodes.JsonArray? FindSceneClipsArray(
        System.Text.Json.Nodes.JsonArray scenes, int scene)
    {
        foreach (var sNode in scenes)
        {
            if (sNode is not System.Text.Json.Nodes.JsonObject s) continue;
            if (ReadJsonNodeInt(s["scene_number"]) != scene) continue;
            return s["veo_clips"] as System.Text.Json.Nodes.JsonArray
                   ?? s["clips"] as System.Text.Json.Nodes.JsonArray;
        }
        return null;
    }

    private static System.Text.Json.Nodes.JsonObject? FindClipNode(
        System.Text.Json.Nodes.JsonArray clips, int clip)
    {
        foreach (var cNode in clips)
        {
            if (cNode is System.Text.Json.Nodes.JsonObject c &&
                ClipKeying.ClipNumber(c) == clip)
                return c;
        }
        return null;
    }

    /// <summary>
    /// Parse a blueprint file (already known to exist) into its root object and required
    /// <c>scenes</c> array, throwing the canonical shape-error messages when either is absent.
    /// </summary>
    private static (System.Text.Json.Nodes.JsonObject root, System.Text.Json.Nodes.JsonArray scenes) ParseBlueprintScenes(string bpPath)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(bpPath))
                   as System.Text.Json.Nodes.JsonObject
                   ?? throw new InvalidOperationException("Invalid blueprint JSON.");
        var scenes = root["scenes"] as System.Text.Json.Nodes.JsonArray
                     ?? throw new InvalidOperationException("Blueprint has no scenes array.");
        return (root, scenes);
    }

    public const int ClipEditVisualPromptMaxChars = 8_000;
    public const int ClipEditNegativePromptMaxChars = 2_000;
    public const int ClipEditDialogueMaxChars = 2_000;
    public const int ClipEditFreeTextMaxChars = 500;
    public const int ClipEditClipNumberMax = 200;

    private static readonly HashSet<string> AllowedDeliveries = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "spoken_on_camera",
        "on_camera",
        "spoken",
        "voiceover_internal",
        "voiceover",
        "voice_over",
        "off_camera",
        "offcamera",
        "internal",
        "narration",
        "vo",
    };

    private static readonly Regex CharacterKeyRx = new(
        @"^Character_[A-Za-z][A-Za-z0-9_]{0,80}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Full validation + normalize for Scenes clip editor fields.
    /// Throws <see cref="InvalidOperationException"/> with a short operator message.
    /// Mutates <paramref name="fields"/> (trim, clamp duration, normalize delivery).
    /// When <paramref name="knownCastKeys"/> is non-empty, speaker / primary / on-screen
    /// must be keys from that cast (no free-text names). Optional bounds (typically from
    /// <see cref="ClipDurationEstimator.ResolveBoundsForModel"/>) validate the manual duration
    /// override against the project's actually-selected video model instead of the global defaults.
    /// </summary>
    public static void ValidateClipEditRequest(
        ClipEditRequest fields,
        IReadOnlyCollection<string>? knownCastKeys = null,
        int minSeconds = ClipDurationEstimator.MinSeconds,
        int absMaxSeconds = ClipDurationEstimator.AbsMaxSeconds)
    {
        ArgumentNullException.ThrowIfNull(fields);

        fields.VisualPrompt = (fields.VisualPrompt ?? "").Trim();
        fields.NegativePrompt = (fields.NegativePrompt ?? "").Trim();
        fields.Dialogue = (fields.Dialogue ?? "").Trim();
        fields.Speaker = string.IsNullOrWhiteSpace(fields.Speaker) ? "" : fields.Speaker.Trim();
        fields.Delivery = string.IsNullOrWhiteSpace(fields.Delivery) ? "" : fields.Delivery.Trim();
        fields.PrimarySubject = (fields.PrimarySubject ?? "").Trim();
        fields.ColorPalette = string.IsNullOrWhiteSpace(fields.ColorPalette) ? null : fields.ColorPalette.Trim();
        fields.FilmStock = string.IsNullOrWhiteSpace(fields.FilmStock) ? null : fields.FilmStock.Trim();
        fields.CharactersOnScreen = (fields.CharactersOnScreen ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Clip number (add path uses this; edit path ignores change)
        if (fields.Clip < 0 || fields.Clip > ClipEditClipNumberMax)
            throw new InvalidOperationException(
                $"Clip number must be between 1 and {ClipEditClipNumberMax}.");

        // Duration: 0 = leave unset/auto; otherwise within provider band
        if (fields.DurationSeconds < 0)
            throw new InvalidOperationException("Duration cannot be negative.");
        if (fields.DurationSeconds > absMaxSeconds)
            throw new InvalidOperationException(
                $"Duration max is {absMaxSeconds}s (video provider limit).");
        if (fields.DurationSeconds > 0 && fields.DurationSeconds < minSeconds)
            throw new InvalidOperationException(
                $"Duration must be at least {minSeconds}s (or 0 to leave unset).");

        // Visual prompt — required for a usable plan
        if (fields.VisualPrompt.Length == 0)
            throw new InvalidOperationException("Visual prompt is required.");
        if (fields.VisualPrompt.Length > ClipEditVisualPromptMaxChars)
            throw new InvalidOperationException(
                $"Visual prompt is too long (max {ClipEditVisualPromptMaxChars:N0} characters).");

        if (fields.NegativePrompt.Length > ClipEditNegativePromptMaxChars)
            throw new InvalidOperationException(
                $"Negative prompt is too long (max {ClipEditNegativePromptMaxChars:N0} characters).");

        if (fields.Dialogue.Length > ClipEditDialogueMaxChars)
            throw new InvalidOperationException(
                $"Dialogue is too long (max {ClipEditDialogueMaxChars:N0} characters).");

        if ((fields.ColorPalette?.Length ?? 0) > ClipEditFreeTextMaxChars)
            throw new InvalidOperationException(
                $"Color palette is too long (max {ClipEditFreeTextMaxChars} characters).");
        if ((fields.FilmStock?.Length ?? 0) > ClipEditFreeTextMaxChars)
            throw new InvalidOperationException(
                $"Film stock is too long (max {ClipEditFreeTextMaxChars} characters).");

        // Delivery allowlist when set
        if (fields.Delivery.Length > 0 && !AllowedDeliveries.Contains(fields.Delivery))
            throw new InvalidOperationException(
                "Delivery must be spoken_on_camera, voiceover_internal, off_camera, or none.");

        var deliveryNone = fields.Delivery.Length == 0 ||
                           string.Equals(fields.Delivery, "none", StringComparison.OrdinalIgnoreCase);

        // Audio consistency
        if (fields.Dialogue.Length > 0 && fields.Speaker.Length == 0)
            throw new InvalidOperationException(
                "Dialogue needs a speaker. Pick who says the line, or clear the dialogue text.");

        if (fields.Dialogue.Length > 0 && deliveryNone)
            throw new InvalidOperationException(
                "Dialogue needs a delivery (spoken_on_camera, voiceover_internal, or off_camera) — not none.");

        if (fields.Speaker.Length > 0 && fields.Dialogue.Length == 0)
            throw new InvalidOperationException(
                "Speaker is set but dialogue is empty. Add the line, or set speaker to none.");

        // Cast identity: Character_* keys only (no free-text display names)
        if (fields.Speaker.Length > 0)
            RequireCharacterKey(fields.Speaker, "Speaker");
        if (fields.PrimarySubject.Length > 0)
            RequireCharacterKey(fields.PrimarySubject, "Primary subject");
        foreach (var ck in fields.CharactersOnScreen)
            RequireCharacterKey(ck, "On-screen character");

        var cast = knownCastKeys?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cast is { Count: > 0 })
        {
            if (fields.Speaker.Length > 0 && !cast.Contains(fields.Speaker))
                throw new InvalidOperationException(
                    $"Speaker must be a cast member (unknown key: {fields.Speaker}).");
            if (fields.PrimarySubject.Length > 0 && !cast.Contains(fields.PrimarySubject))
                throw new InvalidOperationException(
                    $"Primary subject must be a cast member (unknown key: {fields.PrimarySubject}).");
            foreach (var ck in fields.CharactersOnScreen)
            {
                if (!cast.Contains(ck))
                    throw new InvalidOperationException(
                        $"On-screen list has unknown cast key: {ck}.");
            }
        }

        // Auto-include primary + on-camera speaker in on-screen list
        if (fields.PrimarySubject.Length > 0 &&
            !fields.CharactersOnScreen.Any(c =>
                string.Equals(c, fields.PrimarySubject, StringComparison.OrdinalIgnoreCase)))
        {
            fields.CharactersOnScreen.Add(fields.PrimarySubject);
        }

        var onCam = Stage2PlannerService.IsOnCameraDelivery(fields.Delivery);
        if (onCam &&
            fields.Speaker.Length > 0 &&
            !fields.CharactersOnScreen.Any(c =>
                string.Equals(c, fields.Speaker, StringComparison.OrdinalIgnoreCase)))
        {
            fields.CharactersOnScreen.Add(fields.Speaker);
        }
    }

    private static void RequireCharacterKey(string value, string fieldLabel)
    {
        if (!CharacterKeyRx.IsMatch(value))
            throw new InvalidOperationException(
                $"{fieldLabel} must be a Character_* key from the cast (not a free-text name).");
    }

    /// <summary>Backward-compatible name for audio-only checks used by older tests.</summary>
    public static void ValidateClipAudioFields(ClipEditRequest fields) =>
        ValidateClipEditRequest(fields);

    private void ApplyClipFields(System.Text.Json.Nodes.JsonObject clipObj, ClipEditRequest fields, string projectId)
    {
        IReadOnlyCollection<string>? castKeys = null;
        try
        {
            castKeys = LoadCharacterSeeds(projectId).Keys.ToList();
        }
        catch
        {
            /* no cast file yet */
        }

        // Validate the manual duration override against the project's actually-selected video
        // model, not a hardcoded provider assumption — same config key FilmJobService resolves.
        string? modelId = null;
        try
        {
            var cfg = GetConfigSync(projectId);
            if (cfg.TryGetValue("model_name", out var el) && el.ValueKind == JsonValueKind.String)
                modelId = el.GetString();
        }
        catch
        {
            /* use default */
        }
        var (durMinSeconds, _, durAbsMaxSeconds) = ClipDurationEstimator.ResolveBoundsForModel(modelId);

        // Stage2 classifiers sometimes emit article/placeholder key variants (e.g.
        // Character_The_Narrator) that differ from the real cast_seeds.json key
        // (Character_Narrator) but normalize to the same identity. Canonicalize before
        // validating/saving so a clip the user never touched doesn't fail to save with
        // "unknown cast key", and so what's persisted matches the real cast key going forward.
        if (castKeys is { Count: > 0 })
        {
            var byNormalizedKey = castKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .GroupBy(Stage2PlannerService.NormalizeCharacterKey)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            string Canonicalize(string key) =>
                byNormalizedKey.TryGetValue(Stage2PlannerService.NormalizeCharacterKey(key), out var real)
                    ? real
                    : key;

            if (!string.IsNullOrEmpty(fields.Speaker))
                fields.Speaker = Canonicalize(fields.Speaker);
            if (!string.IsNullOrEmpty(fields.PrimarySubject))
                fields.PrimarySubject = Canonicalize(fields.PrimarySubject);
            fields.CharactersOnScreen = fields.CharactersOnScreen
                .Select(Canonicalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        ValidateClipEditRequest(fields, castKeys, durMinSeconds, durAbsMaxSeconds);

        clipObj["visual_prompt"] = fields.VisualPrompt;
        clipObj["negative_prompt"] = fields.NegativePrompt;
        clipObj["primary_subject"] = fields.PrimarySubject;
        clipObj["duration_seconds"] = fields.DurationSeconds;
        clipObj["characters_on_screen"] = new System.Text.Json.Nodes.JsonArray(
            fields.CharactersOnScreen
                .Select(c => System.Text.Json.Nodes.JsonValue.Create(c) as System.Text.Json.Nodes.JsonNode)
                .ToArray());
        clipObj["color_palette"] = fields.ColorPalette;
        clipObj["film_stock"] = fields.FilmStock;

        if (clipObj["audio_payload"] is not System.Text.Json.Nodes.JsonObject audio)
        {
            audio = new System.Text.Json.Nodes.JsonObject();
            clipObj["audio_payload"] = audio;
        }
        audio["dialogue"] = fields.Dialogue;
        audio["speaker"] = string.IsNullOrWhiteSpace(fields.Speaker) ? null : fields.Speaker;
        audio["delivery"] = string.IsNullOrWhiteSpace(fields.Delivery) ? null : fields.Delivery;
        if (!string.IsNullOrWhiteSpace(fields.PronunciationHint))
            audio["pronunciation_hint"] = fields.PronunciationHint;

        // Keep root-level fields in sync if present in blueprint JSON
        if (clipObj.ContainsKey("dialogue"))
            clipObj["dialogue"] = fields.Dialogue;
        if (clipObj.ContainsKey("speaker"))
            clipObj["speaker"] = string.IsNullOrWhiteSpace(fields.Speaker) ? null : fields.Speaker;
        if (clipObj.ContainsKey("delivery"))
            clipObj["delivery"] = string.IsNullOrWhiteSpace(fields.Delivery) ? null : fields.Delivery;
        if (clipObj.ContainsKey("audio_script"))
            clipObj["audio_script"] = fields.Dialogue;
    }

    /// <summary>
    /// Full clip field editor (Scenes "Edit clip" dialog): visual/negative prompt, dialogue +
    /// speaker/delivery, primary subject, characters on screen, and lighting/color fields.
    /// Structural fields (clip_number, veo_continuation_source, timestamp, camera/lens, etc.)
    /// are left untouched — those are pipeline-managed, not hand-edited.
    /// </summary>
    public void UpdateClipFields(string projectId, int scene, int clip, ClipEditRequest fields)
    {
        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is null || !File.Exists(bpPath))
            throw new InvalidOperationException("Shot plan (blueprint) not found — cannot update clip.");

        var (root, scenes) = ParseBlueprintScenes(bpPath);

        var clips = FindSceneClipsArray(scenes, scene)
                    ?? throw new InvalidOperationException($"Scene {scene} not found in shot plan.");
        var clipObj = FindClipNode(clips, clip)
                      ?? throw new InvalidOperationException($"Clip S{scene:D2}C{clip:D2} not found in shot plan.");

        // Self-heal legacy clips keyed on `clip_index` only: stamp the canonical `clip_number` so
        // future reads/finds no longer depend on the fallback above.
        clipObj["clip_number"] = clip;

        ApplyClipFields(clipObj, fields, projectId);

        File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        TriggerAutoGitCommit(projectId, "Edit clip fields");
    }

    /// <summary>
    /// Add a brand-new clip to a scene's shot plan (no video yet — generate it afterward).
    /// Inserted in <c>clip_number</c> order; siblings are untouched. Rejects a duplicate
    /// clip number.
    /// </summary>
    public void AddClip(string projectId, int scene, ClipEditRequest fields)
    {
        if (fields.Clip <= 0)
            throw new InvalidOperationException("Clip number must be positive.");

        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is null || !File.Exists(bpPath))
            throw new InvalidOperationException("Shot plan (blueprint) not found — cannot add clip.");

        var (root, scenes) = ParseBlueprintScenes(bpPath);

        var clips = FindSceneClipsArray(scenes, scene)
                    ?? throw new InvalidOperationException($"Scene {scene} not found in shot plan.");
        if (FindClipNode(clips, fields.Clip) is not null)
            throw new InvalidOperationException($"Clip S{scene:D2}C{fields.Clip:D2} already exists.");

        var clipObj = new System.Text.Json.Nodes.JsonObject
        {
            ["clip_number"] = fields.Clip,
            ["timestamp"] = "",
            ["veo_continuation_source"] = "none",
        };
        ApplyClipFields(clipObj, fields, projectId);

        var insertAt = 0;
        while (insertAt < clips.Count &&
               clips[insertAt] is System.Text.Json.Nodes.JsonObject existing &&
               ClipKeying.ClipNumber(existing) < fields.Clip)
        {
            insertAt++;
        }
        clips.Insert(insertAt, clipObj);

        File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
    }

    /// <summary>
    /// Delete one clip: removes its <c>veo_clips</c> node from the Stage 2 blueprint and
    /// its on-disk video (+ <c>.native</c> sidecar). Clip numbers are not required to be
    /// contiguous — siblings are left untouched, no renumbering. The scene composite /
    /// WIP become stale automatically (existing staleness checks); caller should prompt a
    /// rebuild. Returns false if the clip was not present in the blueprint (still deletes
    /// an orphaned video file if one exists).
    /// </summary>
    public bool DeleteClip(string projectId, int scene, int clip)
    {
        var projectDir = GetProjectDir(projectId);
        var bpPath = FindBlueprintPathSync(projectId);
        var removedFromBlueprint = false;

        if (bpPath is not null && File.Exists(bpPath))
        {
            var (root, scenes) = ParseBlueprintScenes(bpPath);

            foreach (var sNode in scenes)
            {
                if (sNode is not System.Text.Json.Nodes.JsonObject s) continue;
                if (ReadJsonNodeInt(s["scene_number"]) != scene) continue;
                var clips = s["veo_clips"] as System.Text.Json.Nodes.JsonArray
                            ?? s["clips"] as System.Text.Json.Nodes.JsonArray;
                if (clips is null) break;
                for (var i = 0; i < clips.Count; i++)
                {
                    if (clips[i] is not System.Text.Json.Nodes.JsonObject c) continue;
                    if (ClipKeying.ClipNumber(c) != clip) continue;
                    clips.RemoveAt(i);
                    removedFromBlueprint = true;
                    break;
                }
                break;
            }

            if (removedFromBlueprint)
                File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        }

        var videoPath = Path.Combine(projectDir, "assets", "video", $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        var deletedVideo = false;
        if (File.Exists(videoPath))
        {
            File.Delete(videoPath);
            deletedVideo = true;
        }
        var nativePath = videoPath + ".native";
        if (File.Exists(nativePath))
            File.Delete(nativePath);

        // A later Add-clip reuses this clip number (max(existing) + 1) once it's the highest —
        // leaving this file behind would leak the deleted clip's verification status onto
        // whatever brand-new clip happens to land on the same number next.
        var verificationPath = ClipDialogueVerificationService.BuildVerificationPath(projectDir, scene, clip);
        if (File.Exists(verificationPath))
            File.Delete(verificationPath);

        if (!removedFromBlueprint && !deletedVideo)
            throw new InvalidOperationException($"Clip S{scene:D2}C{clip:D2} not found.");

        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        return removedFromBlueprint;
    }

    /// <summary>
    /// Delete a whole scene: removes its node from the blueprint <c>scenes[]</c> and deletes its
    /// clips' on-disk media. Like <see cref="DeleteClip"/> it does NOT renumber the remaining scenes
    /// (renumbering would mean renaming every later scene's video files). Returns true when a scene
    /// was removed from the blueprint.
    /// </summary>
    public bool DeleteScene(string projectId, int scene)
    {
        var projectDir = GetProjectDir(projectId);
        var bpPath = FindBlueprintPathSync(projectId);
        var removed = false;

        if (bpPath is not null && File.Exists(bpPath))
        {
            var (root, scenes) = ParseBlueprintScenes(bpPath);

            for (var i = 0; i < scenes.Count; i++)
            {
                if (scenes[i] is not System.Text.Json.Nodes.JsonObject s) continue;
                if (ReadJsonNodeInt(s["scene_number"]) != scene) continue;
                scenes.RemoveAt(i);
                removed = true;
                break;
            }

            if (removed)
                File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        }

        // Remove this scene's on-disk media (clips, composite, sidecars, client markers).
        var videoDir = Path.Combine(projectDir, "assets", "video");
        if (Directory.Exists(videoDir))
        {
            foreach (var f in Directory.EnumerateFiles(videoDir, $"scene_{scene:D2}*", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }

        if (!removed)
            throw new InvalidOperationException($"Scene {scene} not found.");

        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        return removed;
    }

    /// <summary>
    /// Append a new, empty scene to the blueprint; returns its scene number (max existing + 1).
    /// When an end-credits scene already exists, the new scene is inserted just before it (and the
    /// credits scene is bumped up by one) instead of appended after — credits must stay last, or the
    /// movie plays a stray blank clip past "The End".
    /// </summary>
    public int AddScene(string projectId, string? setting = null)
    {
        var (root, scenes, bpPath) = LoadBlueprintForEdit(projectId);

        var creditsIndex = -1;
        for (var i = 0; i < scenes.Count; i++)
        {
            if (scenes[i] is System.Text.Json.Nodes.JsonObject so &&
                IsCreditsScene(so.Deserialize<JsonElement>()))
            {
                creditsIndex = i;
                break;
            }
        }

        int next;
        if (creditsIndex >= 0)
        {
            var creditsObj = (System.Text.Json.Nodes.JsonObject)scenes[creditsIndex]!;
            next = ReadJsonNodeInt(creditsObj["scene_number"]);
            for (var i = creditsIndex; i < scenes.Count; i++)
                if (scenes[i] is System.Text.Json.Nodes.JsonObject so)
                    so["scene_number"] = ReadJsonNodeInt(so["scene_number"]) + 1;
        }
        else
        {
            next = NextSceneNumber(scenes);
        }

        var sceneObj = new System.Text.Json.Nodes.JsonObject
        {
            ["scene_number"] = next,
            ["setting"] = string.IsNullOrWhiteSpace(setting) ? "INT. NEW SCENE - DAY" : setting.Trim(),
            ["veo_clips"] = new System.Text.Json.Nodes.JsonArray(),
        };

        if (creditsIndex >= 0)
            scenes.Insert(creditsIndex, sceneObj);
        else
            scenes.Add(sceneObj);

        File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        return next;
    }

    /// <summary>
    /// Append a prefilled (editable) end-credits scene; returns its scene number. Content comes from the
    /// single credits-card builder, so a manual re-add matches the scene Stage 2 auto-inserts.
    /// </summary>
    public int AddCreditsScene(string projectId)
    {
        var (root, scenes, bpPath) = LoadBlueprintForEdit(projectId);
        var next = NextSceneNumber(scenes);
        var clip = new System.Text.Json.Nodes.JsonObject
        {
            ["clip_number"] = 1,
            ["timestamp"] = "",
            ["veo_continuation_source"] = "none",
            ["is_credits"] = true,
            ["visual_prompt"] = BuildCreditsVisualPrompt(projectId),
            ["audio_payload"] = new System.Text.Json.Nodes.JsonObject { ["speaker"] = "", ["dialogue"] = "" },
        };
        var sceneObj = new System.Text.Json.Nodes.JsonObject
        {
            ["scene_number"] = next,
            ["setting"] = "END CREDITS",
            ["is_credits"] = true,
            ["veo_clips"] = new System.Text.Json.Nodes.JsonArray { clip },
        };
        scenes.Add(sceneObj);
        File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        return next;
    }

    /// <summary>
    /// Single source of truth for the end-credits card content. Gathers the title + author from the
    /// screenplay title page and the config-wide software name / creator site, so the auto-inserted
    /// credits scene (Stage 2) and a manual re-add produce an identical card.
    /// </summary>
    public string BuildCreditsVisualPrompt(string projectId)
    {
        var c = BuildCreditsContent(projectId);
        return CreditsCardPrompt(c.Title, c.Author, c.SoftwareName, c.SiteUrl);
    }

    /// <summary>
    /// Structured credits-card content (title / author / software / site) — the single source the
    /// client uses to render the card deterministically, and the same inputs
    /// <see cref="BuildCreditsVisualPrompt"/> feeds to <see cref="CreditsCardPrompt"/>. Keeping one
    /// builder means the rendered card and any prompt-based path can never drift apart.
    /// </summary>
    public CreditsContentDto BuildCreditsContent(string projectId)
    {
        var credits = _opts.Credits ?? new CreditsOptions();
        var title = ReadScreenplayTitle(projectId);
        return new CreditsContentDto
        {
            Title = string.IsNullOrWhiteSpace(title) ? "The End" : title.Trim(),
            Author = ReadScreenplayAuthor(projectId),
            SoftwareName = string.IsNullOrWhiteSpace(credits.SoftwareName) ? "PageToMovie" : credits.SoftwareName.Trim(),
            SiteUrl = string.IsNullOrWhiteSpace(credits.SiteUrl) ? "pagetomovie.com" : credits.SiteUrl.Trim(),
        };
    }

    /// <summary>
    /// The one credits-card prompt. Kept short — a video model renders exact strings (a URL especially)
    /// imperfectly, so a concise, legible card reads better than a dense one. Author line only when named.
    /// </summary>
    internal static string CreditsCardPrompt(string? title, string? author, string softwareName, string siteUrl)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "The End" : title.Trim();
        var authorLine = string.IsNullOrWhiteSpace(author)
            ? ""
            : $"then “Based on the story by {author.Trim()}” in a lighter weight, ";
        return "Elegant cinematic end-credits title card, 16:9, locked-off camera, no people, no other logos. "
               + "Deep matte-black background with fine film grain and a soft vignette. "
               + "Centered, high-contrast typography with a slow gentle fade-in and a steady hold: "
               + $"the title “{t}” in a refined serif, {authorLine}"
               + $"then a thin divider, then “Made with {softwareName} · {siteUrl}” in a smaller clean sans-serif. "
               + "Crisp, perfectly legible text, tasteful theatrical end-title look, soft fade to black.";
    }

    /// <summary>Story title for the credits card: Fountain <c>Title:</c>, else blueprint movie_title, else the id.</summary>
    private string ReadScreenplayTitle(string projectId)
    {
        try
        {
            var fountainPath = GetScreenplayPath(projectId);
            if (File.Exists(fountainPath))
            {
                foreach (var line in File.ReadLines(fountainPath).Take(30))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = trimmed[6..].Trim();
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }
        }
        catch { /* fall through */ }

        try
        {
            var bpPath = FindBlueprintPathSync(projectId);
            if (bpPath is not null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(bpPath));
                if (doc.RootElement.TryGetProperty("movie_title", out var mt) &&
                    mt.ValueKind == JsonValueKind.String && mt.GetString() is { Length: > 0 } m)
                    return m.Trim();
            }
        }
        catch { /* fall through */ }

        return projectId;
    }

    /// <summary>
    /// Read the screenplay Author from the Fountain title page for the credits card. Returns empty when
    /// there is no author line or it is a generic public-domain placeholder (so the card doesn't credit
    /// "the story by Public Domain"). General: reads whatever the screenplay declares, nothing per-title.
    /// </summary>
    private string ReadScreenplayAuthor(string projectId)
    {
        try
        {
            var fountainPath = GetScreenplayPath(projectId);
            if (!File.Exists(fountainPath)) return "";
            foreach (var line in File.ReadLines(fountainPath).Take(30))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Author:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Authors:", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = trimmed.IndexOf(':');
                    var val = idx >= 0 ? trimmed[(idx + 1)..].Trim() : "";
                    if (string.IsNullOrWhiteSpace(val) ||
                        val.Contains("public domain", StringComparison.OrdinalIgnoreCase))
                        return "";
                    return val;
                }
            }
        }
        catch { /* no author line — card omits it */ }
        return "";
    }

    private (System.Text.Json.Nodes.JsonObject Root, System.Text.Json.Nodes.JsonArray Scenes, string Path) LoadBlueprintForEdit(string projectId)
    {
        var bpPath = FindBlueprintPathSync(projectId)
                     ?? throw new InvalidOperationException("Shot plan (blueprint) not found.");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(bpPath))
                   as System.Text.Json.Nodes.JsonObject
                   ?? throw new InvalidOperationException("Invalid blueprint JSON.");
        var scenes = root["scenes"] as System.Text.Json.Nodes.JsonArray;
        if (scenes is null)
        {
            scenes = new System.Text.Json.Nodes.JsonArray();
            root["scenes"] = scenes;
        }
        return (root, scenes, bpPath);
    }

    private static int NextSceneNumber(System.Text.Json.Nodes.JsonArray scenes)
    {
        var next = 1;
        foreach (var s in scenes)
            if (s is System.Text.Json.Nodes.JsonObject so)
                next = Math.Max(next, ReadJsonNodeInt(so["scene_number"]) + 1);
        return next;
    }

    public void UpdateCharacterSeedPlaceholder(string projectId, string charKey, string refFileName)
    {
        // Prefer explicit name when provided (e.g. clear on delete); else canonical lock name.
        var placeholder = string.IsNullOrWhiteSpace(refFileName)
            ? ""
            : CharacterRefFileName(charKey);

        // cast_seeds.json is the primary seed source for ListCharacters — update it first.
        try
        {
            var castPath = GetCastPath(projectId);
            if (File.Exists(castPath))
                PatchCharacterSeedPlaceholderInJsonFile(castPath, charKey, placeholder);
        }
        catch { /* non-fatal */ }

        try
        {
            var scenesPath = GetScenesPath(projectId);
            if (File.Exists(scenesPath))
                PatchCharacterSeedPlaceholderInJsonFile(scenesPath, charKey, placeholder);
        }
        catch { /* non-fatal */ }

        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is not null && File.Exists(bpPath))
        {
            try
            {
                PatchCharacterSeedPlaceholderInJsonFile(bpPath, charKey, placeholder);
                InvalidateSceneListCache(projectId);
            }
            catch
            {
                // Non-fatal: lock file still written
            }
        }

        InvalidateReadCaches(projectId);
    }

    /// <summary>
    /// Set <c>reference_image_placeholder</c> on a character seed inside cast_seeds / scenes / blueprint JSON.
    /// </summary>
    private static void PatchCharacterSeedPlaceholderInJsonFile(
        string path, string charKey, string placeholder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var tree = doc.RootElement.Deserialize<Dictionary<string, object?>>()
                   ?? new Dictionary<string, object?>();

        Dictionary<string, object?>? seeds = null;
        Action? commit = null;

        if (tree.TryGetValue("character_seed_tokens", out var direct) && direct is not null)
        {
            var seedsJson = JsonSerializer.Serialize(direct);
            seeds = JsonSerializer.Deserialize<Dictionary<string, object?>>(seedsJson)
                    ?? new Dictionary<string, object?>();
            commit = () => tree["character_seed_tokens"] = seeds;
        }
        else if (tree.TryGetValue("global_production_variables", out var gpvObj) && gpvObj is not null)
        {
            var gpvJson = JsonSerializer.Serialize(gpvObj);
            var gpv = JsonSerializer.Deserialize<Dictionary<string, object?>>(gpvJson)
                      ?? new Dictionary<string, object?>();
            if (!gpv.TryGetValue("character_seed_tokens", out var seedsObj) || seedsObj is null)
                return;
            var seedsJson = JsonSerializer.Serialize(seedsObj);
            seeds = JsonSerializer.Deserialize<Dictionary<string, object?>>(seedsJson)
                    ?? new Dictionary<string, object?>();
            commit = () =>
            {
                gpv["character_seed_tokens"] = seeds;
                tree["global_production_variables"] = gpv;
            };
        }
        else
            return;

        // Case-insensitive key match (Character_Narrator vs character_narrator)
        string? matchKey = null;
        foreach (var k in seeds!.Keys)
        {
            if (string.Equals(k, charKey, StringComparison.OrdinalIgnoreCase))
            {
                matchKey = k;
                break;
            }
        }
        if (matchKey is null)
            return;

        var seedJson = JsonSerializer.Serialize(seeds[matchKey]);
        var seed = JsonSerializer.Deserialize<Dictionary<string, object?>>(seedJson)
                   ?? new Dictionary<string, object?>();
        seed["reference_image_placeholder"] = placeholder;
        seeds[matchKey] = seed;
        commit!();

        var outJson = JsonSerializer.Serialize(tree, JsonDefaults.Indented);
        File.WriteAllText(path, outJson + "\n");
    }

    /// <summary>Resolve pipeline_state.json path (honors project.json state_file).</summary>
    public string ResolvePipelineStatePath(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var stateName = "pipeline_state.json";
        var metaPath = Path.Combine(dir, "project.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (meta.RootElement.TryGetProperty("state_file", out var sf) &&
                    sf.GetString() is { Length: > 0 } n)
                    stateName = n;
            }
            catch { /* ignore */ }
        }
        return Path.Combine(dir, stateName);
    }

    /// <summary>
    /// Whether book images have been sorted onto character seeds
    /// (pipeline_state.character_plates.sorted_by_character).
    /// </summary>
    public CharacterPlatesState GetCharacterPlatesState(string projectId)
    {
        var path = ResolvePipelineStatePath(projectId);
        var state = new CharacterPlatesState();
        if (!File.Exists(path)) return state;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            // Nested object preferred
            if (doc.RootElement.TryGetProperty("character_plates", out var cp) &&
                cp.ValueKind == JsonValueKind.Object)
            {
                state.SortedByCharacter = ReadJsonBool(cp, "sorted_by_character");
                if (cp.TryGetProperty("sorted_at", out var at) && at.ValueKind == JsonValueKind.String)
                    state.SortedAt = at.GetString();
                if (cp.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String &&
                    src.GetString() is { Length: > 0 } ss)
                    state.Source = ss;
                if (cp.TryGetProperty("characters_updated", out var cu) && cu.TryGetInt32(out var n))
                    state.CharactersUpdated = n;
                if (cp.TryGetProperty("method", out var meth) && meth.ValueKind == JsonValueKind.String)
                    state.Method = meth.GetString();
                return state;
            }
            if (ReadJsonBool(doc.RootElement, "character_plates_sorted"))
            {
                state.SortedByCharacter = true;
                if (doc.RootElement.TryGetProperty("character_plates_sorted_at", out var at2) &&
                    at2.ValueKind == JsonValueKind.String)
                    state.SortedAt = at2.GetString();
                if (doc.RootElement.TryGetProperty("character_plates_method", out var m2) &&
                    m2.ValueKind == JsonValueKind.String)
                    state.Method = m2.GetString();
            }
        }
        catch { /* ignore */ }
        return state;
    }

    private static bool ReadJsonBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) && b,
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            _ => false,
        };
    }

    /// <summary>Record that character plates were sorted into scenes.json seeds.</summary>
    public void MarkCharacterPlatesSorted(string projectId, int charactersUpdated, string method = "heuristic")
    {
        var path = ResolvePipelineStatePath(projectId);
        var merged = LoadPipelineStateDict(path);
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        merged["character_plates"] = new Dictionary<string, object?>
        {
            ["sorted_by_character"] = true,
            ["sorted_at"] = now,
            ["source"] = "scenes.json#character_seed_tokens.design_reference_images",
            ["characters_updated"] = charactersUpdated,
            ["method"] = method,
        };
        // Keep flat keys in sync for simple greps / older tools
        merged["character_plates_sorted"] = true;
        merged["character_plates_sorted_at"] = now;
        merged["character_plates_method"] = method;
        var json = JsonSerializer.Serialize(merged, JsonDefaults.Indented);
        File.WriteAllText(path, json + "\n");
    }

    /// <summary>Clear the sorted flag (e.g. after book re-import invalidates plates).</summary>
    public void ClearCharacterPlatesSorted(string projectId)
    {
        var path = ResolvePipelineStatePath(projectId);
        if (!File.Exists(path)) return;
        var merged = LoadPipelineStateDict(path);
        merged["character_plates"] = new Dictionary<string, object?>
        {
            ["sorted_by_character"] = false,
            ["sorted_at"] = null,
            ["source"] = "scenes.json#character_seed_tokens.design_reference_images",
            ["characters_updated"] = 0,
            ["method"] = null,
        };
        merged["character_plates_sorted"] = false;
        merged.Remove("character_plates_sorted_at");
        merged.Remove("character_plates_method");
        var json = JsonSerializer.Serialize(merged, JsonDefaults.Indented);
        File.WriteAllText(path, json + "\n");
    }

    /// <summary>Stable stage keys for the optional Book sub-steps whose "done" state the sub-strip shows.</summary>
    public static class BookSubstepKeys
    {
        public const string Look = "look";
        public const string Enrich = "enrich";
        public const string FitLength = "trim";
    }

    /// <summary>
    /// Record that an optional Book sub-step (Look / Enrich / Fit length) was applied to the current
    /// screenplay, so the Book sub-strip can show a "done" check. Fit length also records the target
    /// minutes it fit toward. Merged into pipeline_state.json under <c>book_substeps</c>. Idempotent.
    /// </summary>
    public void MarkBookSubstepDone(string projectId, string stage, double? targetMinutes = null)
    {
        stage = (stage ?? "").Trim().ToLowerInvariant();
        if (stage is not (BookSubstepKeys.Look or BookSubstepKeys.Enrich or BookSubstepKeys.FitLength))
            return;

        var path = ResolvePipelineStatePath(projectId);
        var merged = LoadPipelineStateDict(path);

        var subs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (merged.TryGetValue("book_substeps", out var existing) && existing is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(existing));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        subs[p.Name] = p.Value.Deserialize<object>();
            }
            catch { /* start fresh */ }
        }

        var entry = new Dictionary<string, object?>
        {
            ["done"] = true,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        if (stage == BookSubstepKeys.FitLength && targetMinutes is > 0)
            entry["target_minutes"] = targetMinutes;
        subs[stage] = entry;

        merged["book_substeps"] = subs;
        File.WriteAllText(path, JsonSerializer.Serialize(merged, JsonDefaults.Indented) + "\n");
    }

    /// <summary>
    /// Clear all Book sub-step "done" markers. Called when a fresh full-length screenplay base is
    /// generated, so Look/Enrich/Fit length checks never carry over to newly generated text.
    /// </summary>
    public void ClearBookSubsteps(string projectId)
    {
        var path = ResolvePipelineStatePath(projectId);
        if (!File.Exists(path)) return;
        var merged = LoadPipelineStateDict(path);
        if (!merged.Remove("book_substeps")) return;
        File.WriteAllText(path, JsonSerializer.Serialize(merged, JsonDefaults.Indented) + "\n");
    }

    /// <summary>Read which optional Book sub-steps have been applied (for the sub-strip "done" checks).</summary>
    public BookSubstepStatus ReadBookSubsteps(string projectId)
    {
        var status = new BookSubstepStatus();
        try
        {
            var path = ResolvePipelineStatePath(projectId);
            if (!File.Exists(path)) return status;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("book_substeps", out var subs) ||
                subs.ValueKind != JsonValueKind.Object)
                return status;

            status.LookDone = SubstepDone(subs, BookSubstepKeys.Look);
            status.EnrichDone = SubstepDone(subs, BookSubstepKeys.Enrich);
            status.FitLengthDone = SubstepDone(subs, BookSubstepKeys.FitLength);
            if (subs.TryGetProperty(BookSubstepKeys.FitLength, out var t) && t.ValueKind == JsonValueKind.Object &&
                t.TryGetProperty("target_minutes", out var tm) && tm.TryGetDouble(out var mins) && mins > 0)
                status.FitLengthTargetMinutes = mins;
        }
        catch { /* defaults */ }
        return status;

        static bool SubstepDone(JsonElement subs, string stage) =>
            subs.TryGetProperty(stage, out var el) &&
            el.ValueKind == JsonValueKind.Object &&
            ReadJsonBool(el, "done");
    }

    private static Dictionary<string, object?> LoadPipelineStateDict(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var rawDoc = JsonDocument.Parse(File.ReadAllText(path));
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in rawDoc.RootElement.EnumerateObject())
            merged[p.Name] = p.Value.Deserialize<object>();
        return merged;
    }

    /// <summary>Bump character revision in pipeline_state (cascade stale marker).</summary>
    public void MarkCharacterChanged(string projectId, string charKey, string reason)
    {
        var path = ResolvePipelineStatePath(projectId);
        var merged = LoadPipelineStateDict(path);

        var revs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (merged.TryGetValue("character_revisions", out var crObj) && crObj is not null)
        {
            try
            {
                using var crDoc = JsonDocument.Parse(JsonSerializer.Serialize(crObj));
                if (crDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in crDoc.RootElement.EnumerateObject())
                        revs[p.Name] = p.Value.Deserialize<object>();
                }
            }
            catch { /* ignore */ }
        }

        var prevRev = 0;
        if (revs.TryGetValue(charKey, out var prev) && prev is not null)
        {
            try
            {
                using var prevDoc = JsonDocument.Parse(JsonSerializer.Serialize(prev));
                if (prevDoc.RootElement.TryGetProperty("revision", out var r) && r.TryGetInt32(out var rv))
                    prevRev = rv;
            }
            catch { /* ignore */ }
        }

        revs[charKey] = new Dictionary<string, object?>
        {
            ["revision"] = prevRev + 1,
            ["updated_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["reason"] = reason,
        };
        merged["character_revisions"] = revs;
        merged["characters_designed"] = true;

        var json = JsonSerializer.Serialize(merged, JsonDefaults.Indented);
        File.WriteAllText(path, json + "\n");
    }

    public string? ResolveCharacterVariantPath(string projectId, string charKey, int variantIndex)
    {
        if (variantIndex is < 1 or > 3)
            return null;
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(charKey, info))
            return null;
        var fileName = $"{charKey.ToLowerInvariant()}_variant_0{variantIndex}.png";
        var full = Path.Combine(GetProjectDir(projectId), "assets", "characters", fileName);
        return File.Exists(full) && new FileInfo(full).Length >= 64 ? full : null;
    }

    public string? ResolveCharacterBookRefPath(string projectId, string charKey, int bookIndex)
    {
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(charKey, info))
            return null;

        var projectDir = GetProjectDir(projectId);
        // Only seed-tracked plates (scenes.json design_reference_images) — no free disk scan
        var bookRefs = seeds.TryGetValue(charKey, out info)
            ? CollectSeedPlatePaths(info)
            : new List<string>();

        if (bookIndex < 0 || bookIndex >= bookRefs.Count)
            return null;
        var rel = bookRefs[bookIndex];
        var full = ResolveProjectRelativePath(projectDir, rel);
        if (full is not null) return full;
        var byName = Path.Combine(projectDir, "assets", "characters", Path.GetFileName(rel));
        return File.Exists(byName) ? byName : null;
    }

    /// <summary>
    /// Paths from character seed design_reference_images (book_reference_images alias).
    /// Skips text-only / sampled layout filenames so they are never shown as plates.
    /// </summary>
    private static List<string> CollectSeedPlatePaths(JsonElement info)
    {
        var bookRefs = new List<string>();
        foreach (var prop in new[] { "design_reference_images", "book_reference_images" })
        {
            if (!info.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var x in arr.EnumerateArray())
            {
                var s = x.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (IsTextOnlyPlatePath(s)) continue;
                if (!bookRefs.Contains(s!, StringComparer.OrdinalIgnoreCase))
                    bookRefs.Add(s!);
            }
            if (bookRefs.Count > 0)
                break; // prefer design_reference_images when present
        }
        return bookRefs;
    }

    /// <summary>True for sampled/OCR/text-page paths that must never be character plates.</summary>
    public static bool IsTextOnlyPlatePath(string pathOrName)
    {
        var n = Path.GetFileName(pathOrName);
        if (n.Contains("sampled", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("text_page", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("ocr", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? ResolveProjectRelativePath(string projectDir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        var norm = relative.Replace('\\', '/').TrimStart('/');
        // Reject path traversal
        if (norm.Contains("..", StringComparison.Ordinal))
            return null;
        var full = Path.GetFullPath(Path.Combine(projectDir, norm.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(projectDir);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Scene list from Stage 2 blueprint + on-disk clip counts.
    /// When <paramref name="probeDurations"/> is false (LoadSim), skip ffprobe — much faster under concurrency.
    /// Results are cached briefly (single-flight) when <see cref="SceneListCache"/> is registered.
    /// </summary>
    /// <summary>
    /// True when a blueprint scene is the end-credits card, however it happens to be stored across
    /// blueprint generations. Single source of truth for credits detection so the Scenes list
    /// (<see cref="SceneSummary.IsCredits"/> — which hides the "Add credits" button) and the
    /// cast-readiness / video-gen gate (<see cref="FilmJobService.IsCreditsScene"/>) always agree.
    /// Generic — no story-specific strings or scene numbers.
    /// Detects, in order: the scene-level <c>is_credits</c> flag; a <c>CREDITS</c> setting or
    /// scene_heading; or — for older/auto-inserted scenes whose scene-level marker is absent — the
    /// <c>is_credits</c> flag on any of the scene's clips. Both the Stage 2 auto-insert and the
    /// manual "Add credits" write clip-level <c>is_credits</c>, so the clip flag is the durable
    /// structural signal even when the scene-level flag/heading was dropped by a later edit.
    /// </summary>
    internal static bool IsCreditsScene(JsonElement? sceneEl)
    {
        if (sceneEl is not { ValueKind: JsonValueKind.Object } s)
            return false;
        if (ReadJsonBool(s, "is_credits"))
            return true;
        if (s.TryGetProperty("setting", out var set) &&
            (set.GetString() ?? "").Contains("CREDITS", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.TryGetProperty("scene_heading", out var sh) &&
            (sh.GetString() ?? "").Contains("CREDITS", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.TryGetProperty("veo_clips", out var clips) && clips.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clips.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && ReadJsonBool(c, "is_credits"))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Dictionary-shape overload of <see cref="IsCreditsScene(JsonElement?)"/> for Stage 2 planning
    /// and aggregate validation, which build/hold scenes as <c>Dictionary&lt;string, object?&gt;</c>
    /// before serialization. Same rule, same order — including the clip-level <c>is_credits</c> flag —
    /// so a credits card is recognized however it happens to be stored (older auto-inserts marked it
    /// on the setting; the durable structural signal is the clip flag both writers set).
    /// </summary>
    internal static bool IsCreditsScene(IReadOnlyDictionary<string, object?>? scene)
    {
        if (scene is null) return false;
        if (DictBoolTrue(scene, "is_credits")) return true;
        if (DictContains(scene, "setting", "CREDITS")) return true;
        if (DictContains(scene, "scene_heading", "CREDITS")) return true;
        if (scene.TryGetValue("veo_clips", out var clipsObj) && clipsObj is IEnumerable<object?> clips)
        {
            foreach (var c in clips)
                if (c is IReadOnlyDictionary<string, object?> cd && DictBoolTrue(cd, "is_credits"))
                    return true;
        }
        return false;
    }

    private static bool DictBoolTrue(IReadOnlyDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) &&
        (v is true || (v?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool DictContains(IReadOnlyDictionary<string, object?> d, string key, string needle) =>
        d.TryGetValue(key, out var v) &&
        (v?.ToString()?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);

    public Task<IReadOnlyList<SceneSummary>> ListScenesAsync(
        string projectId,
        bool probeDurations = true,
        CancellationToken ct = default)
    {
        if (_sceneListCache is null)
            return ListScenesCoreAsync(projectId, probeDurations, ct);

        return _sceneListCache.GetOrBuildAsync(
            projectId,
            probeDurations,
            c => ListScenesCoreAsync(projectId, probeDurations, c),
            ct);
    }

    private async Task<IReadOnlyList<SceneSummary>> ListScenesCoreAsync(
        string projectId,
        bool probeDurations,
        CancellationToken ct)
    {
        var (bp, owned) = await LoadBlueprintForReadAsync(projectId, ct).ConfigureAwait(false);

        try
        {
        if (bp is null ||
            !bp.RootElement.TryGetProperty("scenes", out var scenesEl) ||
            scenesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SceneSummary>();
        }

        var projectDir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var scenesDir = Path.Combine(projectDir, "assets", "scenes");
        var videoIndex = await GetVideoIndexWithParentFallbackAsync(projectId, videoDir, ct).ConfigureAwait(false);
        var scenesIndex = await GetDirIndexAsync(scenesDir, ct).ConfigureAwait(false);

        HashSet<string>? approvedScenes = null;
        var stateFile = Path.Combine(projectDir, "pipeline_state.json");
        if (File.Exists(stateFile))
        {
            try
            {
                var stateText = await File.ReadAllTextAsync(stateFile, ct).ConfigureAwait(false);
                using var stateDoc = JsonDocument.Parse(stateText);
                if (stateDoc.RootElement.TryGetProperty("scene_review", out var sr) &&
                    sr.ValueKind == JsonValueKind.Object)
                {
                    approvedScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in sr.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object &&
                            prop.Value.TryGetProperty("status", out var stEl) &&
                            string.Equals(stEl.GetString(), "approved", StringComparison.OrdinalIgnoreCase))
                        {
                            approvedScenes.Add(prop.Name);
                        }
                    }
                }
            }
            catch { /* non-fatal */ }
        }

        // Which scenes already have a background-music take — one registry query for the whole list
        // (rather than a per-scene HasSceneMusicAsync call) drives the overview's Audio-Takes affordance.
        var musicScenes = new HashSet<int>();
        if (_mediaRegistry is not null)
        {
            foreach (var mo in await _mediaRegistry.ListProjectAsync(projectId, ct).ConfigureAwait(false))
                if (string.Equals(mo.Kind, "music", StringComparison.OrdinalIgnoreCase) && mo.Scene is int msc)
                    musicScenes.Add(msc);
        }

        var rows = new List<SceneSummary>();
        foreach (var s in scenesEl.EnumerateArray())
        {
            if (!s.TryGetProperty("scene_number", out var snEl) || !snEl.TryGetInt32(out var sn))
                continue;

            var clips = s.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array
                ? vc.EnumerateArray().ToList()
                : new List<JsonElement>();
            var nClips = clips.Count;
            var onDisk = 0;
            foreach (var c in clips)
            {
                var cn = ClipKeying.ClipNumber(c);
                if (cn <= 0) continue;
                if (ClipOnDisk(videoIndex, sn, cn))
                    onDisk++;
            }

            var compositeOk =
                HasCompositeFile(videoIndex, scenesIndex, sn);

            double? planned = null;
            if (s.TryGetProperty("total_estimated_duration_seconds", out var dEl))
            {
                if (dEl.TryGetDouble(out var dd)) planned = dd;
                else if (dEl.TryGetInt32(out var di)) planned = di;
            }

            double? actual = null;
            if (probeDurations && _duration is not null)
            {
                var compositePath = ResolveCompositePath(projectId, sn);
                var clipPaths = new List<string>();
                foreach (var c in clips)
                {
                    var cn = ClipKeying.ClipNumber(c);
                    if (cn <= 0) continue;
                    var cp = ResolveClipVideoPath(projectId, sn, cn);
                    if (cp is not null) clipPaths.Add(cp);
                }
                actual = await _duration.GetSceneActualDurationSecondsAsync(compositePath, clipPaths, ct).ConfigureAwait(false);
            }

            var chars = new List<string>();
            void AddChar(string? name)
            {
                if (!string.IsNullOrWhiteSpace(name) && !chars.Contains(name, StringComparer.OrdinalIgnoreCase))
                    chars.Add(name!);
            }
            if (s.TryGetProperty("characters_on_screen", out var cos) && cos.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in cos.EnumerateArray())
                    AddChar(x.GetString());
            }
            // Scene-level characters_on_screen can lag clip-level casts (e.g. a character who
            // only appears mid-scene in specific clips) — union in each clip's own list too.
            foreach (var c in clips)
            {
                if (c.TryGetProperty("characters_on_screen", out var clipCos) && clipCos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in clipCos.EnumerateArray())
                        AddChar(x.GetString());
                }
            }

            var locs = new List<string>();
            if (s.TryGetProperty("location_ids", out var lids) && lids.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in lids.EnumerateArray())
                {
                    var name = x.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        locs.Add(name!);
                }
            }

            string? primaryLoc = null;
            if (s.TryGetProperty("primary_location_id", out var pl) &&
                pl.GetString() is { Length: > 0 } plId)
            {
                primaryLoc = plId;
                if (!locs.Contains(plId, StringComparer.OrdinalIgnoreCase))
                    locs.Insert(0, plId);
            }

            var complete = nClips > 0 && onDisk >= nClips;
            var status = nClips == 0 || onDisk == 0
                ? "empty"
                : complete ? "complete" : "partial";
            var isApproved = approvedScenes?.Contains($"S{sn:D2}") == true;

            var settingText = s.TryGetProperty("setting", out var set) ? set.GetString() ?? "" : "";
            var headingText = s.TryGetProperty("scene_heading", out var shd) ? shd.GetString() ?? "" : "";
            var isCredits = IsCreditsScene(s);

            rows.Add(new SceneSummary
            {
                SceneNumber = sn,
                // Credits scenes carry a scene_heading, not a setting — show a clear label instead of a blank cell.
                Setting = !string.IsNullOrWhiteSpace(settingText) ? settingText
                    : isCredits ? "END CREDITS"
                    : headingText,
                IsCredits = isCredits,
                ClipCount = nClips,
                ClipsOnDisk = onDisk,
                ClipsComplete = complete,
                PlannedDurationSeconds = planned,
                ActualDurationSeconds = actual,
                DurationSeconds = actual ?? planned,
                CompositeExists = compositeOk,
                CharactersOnScreen = chars,
                LocationIds = locs,
                PrimaryLocationId = primaryLoc,
                Status = status,
                IsApproved = isApproved,
                HasBackgroundMusic = musicScenes.Contains(sn),
            });
        }

        return rows.OrderBy(r => r.SceneNumber).ToList();
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public async Task<SceneDetail?> GetSceneDetailAsync(
        string projectId,
        int sceneNumber,
        bool probeDurations = true,
        CancellationToken ct = default)
    {
        var (bp, owned) = await LoadBlueprintForReadAsync(projectId, ct).ConfigureAwait(false);

        try
        {
        if (bp is null)
            return null;

        JsonElement? sceneEl = null;
        if (bp.RootElement.TryGetProperty("scenes", out var scenesEl) &&
            scenesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scenesEl.EnumerateArray())
            {
                if (s.TryGetProperty("scene_number", out var snEl) &&
                    snEl.TryGetInt32(out var sn) &&
                    sn == sceneNumber)
                {
                    sceneEl = s.Clone();
                    break;
                }
            }
        }

        if (sceneEl is null)
            return null;

        var sEl = sceneEl.Value;
        var projectDir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var scenesDir = Path.Combine(projectDir, "assets", "scenes");
        var videoIndex = await GetVideoIndexWithParentFallbackAsync(projectId, videoDir, ct).ConfigureAwait(false);
        var scenesIndex = await GetDirIndexAsync(scenesDir, ct).ConfigureAwait(false);

        var clips = new List<ClipSummary>();
        var duplicateClipNumbers = new List<int>();
        var seenClipNumbers = new HashSet<int>();
        if (sEl.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in vc.EnumerateArray())
            {
                var cn = ClipKeying.ClipNumber(c);
                if (cn <= 0) continue;

                // Malformed shot plan: the same clip_number twice would double the scene when stitched
                // (one file per veo_clips entry). Keep the first, drop the rest so existing movies still
                // work, and flag it (SceneDetail.DuplicateClipNumbers) so an admin surface can show it
                // rather than hiding it. The shot-plan write path throws on this at the source.
                if (!seenClipNumbers.Add(cn))
                {
                    if (!duplicateClipNumbers.Contains(cn)) duplicateClipNumbers.Add(cn);
                    Console.WriteLine($"[blueprint] {projectId} scene {sceneNumber}: duplicate clip_number {cn} in veo_clips — deduped (kept first).");
                    continue;
                }

                var fileName = $"scene_{sceneNumber:D2}_clip_{cn:D2}.mp4";
                var onDisk = ClipOnDisk(videoIndex, sceneNumber, cn);
                long size = 0;
                if (onDisk)
                {
                    if (videoIndex.TryGetValue(fileName, out var sz))
                        size = sz;
                    else
                    {
                        var prefix = $"scene_{sceneNumber:D2}_clip_{cn:D2}_take_";
                        var takeMatch = videoIndex.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(takeMatch.Key)) size = takeMatch.Value;
                    }
                }

                var dialogue = "";
                string? speaker = null;
                string? delivery = null;
                string? pronunciationHint = null;
                string? secondarySpeaker = null;
                string? secondaryDialogue = null;
                var hasAp = c.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object;
                if (hasAp)
                {
                    if (ap.TryGetProperty("dialogue", out var d))
                        dialogue = d.GetString() ?? "";
                    if (ap.TryGetProperty("speaker", out var sp))
                        speaker = sp.GetString();
                    if (ap.TryGetProperty("delivery", out var del))
                        delivery = del.GetString();
                    if (ap.TryGetProperty("pronunciation_hint", out var ph))
                        pronunciationHint = ph.GetString();
                    // Second speaker's line in a cross-speaker two-hander clip (else absent).
                    if (ap.TryGetProperty("secondary_speaker", out var ssp))
                        secondarySpeaker = ssp.GetString();
                    if (ap.TryGetProperty("secondary_dialogue", out var sdlg))
                        secondaryDialogue = sdlg.GetString();
                }
                if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty("dialogue", out var rootD))
                {
                    dialogue = rootD.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty("audio_script", out var rootAS))
                {
                    dialogue = rootAS.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(speaker) && c.TryGetProperty("speaker", out var rootSp))
                {
                    speaker = rootSp.GetString();
                }
                if (string.IsNullOrWhiteSpace(delivery) && c.TryGetProperty("delivery", out var rootDel))
                {
                    delivery = rootDel.GetString();
                }
                if (string.IsNullOrWhiteSpace(pronunciationHint) && c.TryGetProperty("pronunciation_hint", out var rootPh))
                {
                    pronunciationHint = rootPh.GetString();
                }
                // Speech-safe form for operator UI (same helper as video gen payload)
                dialogue = ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue);

                var dur = 0;
                if (c.TryGetProperty("duration_seconds", out var dEl) && dEl.TryGetInt32(out var ds))
                    dur = ds;

                var clipPath = onDisk ? ResolveClipVideoPath(projectId, sceneNumber, cn) : null;
                var resolvedFileName = clipPath is not null ? Path.GetFileName(clipPath) : fileName;

                double? actualClip = null;
                if (probeDurations && onDisk && _duration is not null && clipPath is not null)
                {
                    actualClip = await _duration.GetDurationSecondsAsync(clipPath, ct).ConfigureAwait(false);
                }

                var visualPrompt = c.TryGetProperty("visual_prompt", out var vp) ? vp.GetString() ?? "" : "";
                visualPrompt = ClipVideoPromptBuilder.SanitizeSpokenQuotesInVisual(visualPrompt);

                clips.Add(new ClipSummary
                {
                    ClipNumber = cn,
                    Timestamp = c.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "",
                    DurationSeconds = dur,
                    ActualDurationSeconds = actualClip,
                    Continuation = c.TryGetProperty("veo_continuation_source", out var cont)
                        ? cont.GetString() ?? "none"
                        : "none",
                    PrimarySubject = c.TryGetProperty("primary_subject", out var ps)
                        ? ps.GetString() ?? ""
                        : "",
                    VisualPrompt = visualPrompt,
                    NegativePrompt = c.TryGetProperty("negative_prompt", out var np) ? np.GetString() ?? "" : "",
                    Dialogue = dialogue,
                    Speaker = speaker,
                    Delivery = delivery,
                    SecondarySpeaker = secondarySpeaker,
                    SecondaryDialogue = string.IsNullOrWhiteSpace(secondaryDialogue)
                        ? secondaryDialogue
                        : ClipVideoPromptBuilder.SanitizeSpokenDialogue(secondaryDialogue),
                    PronunciationHint = pronunciationHint,
                    CharactersOnScreen = c.TryGetProperty("characters_on_screen", out var clipCos) &&
                                         clipCos.ValueKind == JsonValueKind.Array
                        ? clipCos.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x!)
                            .ToList()
                        : new List<string>(),
                    ColorPalette = c.TryGetProperty("color_palette", out var cp) ? cp.GetString() : null,
                    FilmStock = c.TryGetProperty("film_stock", out var fs) ? fs.GetString() : null,
                    OnDisk = onDisk,
                    SizeBytes = size,
                    FileName = onDisk ? resolvedFileName : null,
                    VideoUrl = onDisk
                        ? $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{cn}/video"
                        : null,
                    DialogueVerification = await LoadClipDialogueVerificationAsync(projectDir, sceneNumber, cn, ct).ConfigureAwait(false),
                    Stage1BeatId = c.TryGetProperty("stage1_beat_id", out var s1b) ? s1b.GetString() : null,
                    Stage1BeatIds = c.TryGetProperty("stage1_beat_ids", out var s1bs) &&
                                    s1bs.ValueKind == JsonValueKind.Array
                        ? s1bs.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x!)
                            .ToList()
                        : new List<string>(),
                });
                var last = clips[^1];
                if (last.Stage1BeatIds.Count == 0 && !string.IsNullOrWhiteSpace(last.Stage1BeatId))
                    last.Stage1BeatIds.Add(last.Stage1BeatId!);
            }
        }

        clips = clips.OrderBy(c => c.ClipNumber).ToList();
        var onDiskCount = clips.Count(c => c.OnDisk);

        var compositeOk = HasCompositeFile(videoIndex, scenesIndex, sceneNumber);

        double? planned = null;
        if (sEl.TryGetProperty("total_estimated_duration_seconds", out var td))
        {
            if (td.TryGetDouble(out var dd)) planned = dd;
            else if (td.TryGetInt32(out var di)) planned = di;
        }

        double? actual = null;
        if (probeDurations && _duration is not null)
        {
            var compositePath = ResolveCompositePath(projectId, sceneNumber);
            var clipPaths = clips
                .Where(c => c.OnDisk)
                .Select(c => ResolveClipVideoPath(projectId, sceneNumber, c.ClipNumber))
                .Where(p => p is not null)
                .Cast<string>();
            actual = await _duration.GetSceneActualDurationSecondsAsync(compositePath, clipPaths, ct).ConfigureAwait(false);
        }

        var chars = new List<string>();
        if (sEl.TryGetProperty("characters_on_screen", out var cos) && cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
            {
                var name = x.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    chars.Add(name!);
            }
        }

        var locs = new List<string>();
        if (sEl.TryGetProperty("location_ids", out var lids) && lids.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in lids.EnumerateArray())
            {
                var name = x.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    locs.Add(name!);
            }
        }

        // Music now lives client-side only (see MediaSyncLocator note on ClipOnDisk) — the
        // registry row is the source of truth, not a server-side file that no longer exists.
        var hasMusic = _mediaRegistry is not null &&
            await _mediaRegistry.HasSceneMusicAsync(projectId, sceneNumber, ct).ConfigureAwait(false);

        MusicScoreInfo? musicScore = null;
        if (sEl.TryGetProperty("music_score", out var msEl) && msEl.ValueKind == JsonValueKind.Object)
        {
            musicScore = new MusicScoreInfo
            {
                Prompt = msEl.TryGetProperty("prompt", out var msp) ? msp.GetString() ?? "" : "",
                Genre = msEl.TryGetProperty("genre", out var msg) ? msg.GetString() : null,
                Mood = msEl.TryGetProperty("mood", out var msm) ? msm.GetString() : null,
                Tempo = msEl.TryGetProperty("tempo", out var mst) ? mst.GetString() : null,
            };
        }
        else if (sEl.TryGetProperty("music_prompt", out var mpEl) && mpEl.ValueKind == JsonValueKind.String)
        {
            musicScore = new MusicScoreInfo
            {
                Prompt = mpEl.GetString() ?? ""
            };
        }

        return new SceneDetail
        {
            SceneNumber = sceneNumber,
            Setting = sEl.TryGetProperty("setting", out var set) ? set.GetString() ?? "" : "",
            PlannedDurationSeconds = planned,
            ActualDurationSeconds = actual,
            DurationSeconds = actual ?? planned,
            ClipCount = clips.Count,
            ClipsOnDisk = onDiskCount,
            CompositeExists = compositeOk,
            HasBackgroundMusic = hasMusic,
            MusicScore = musicScore,
            CompositeUrl = compositeOk
                ? $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/composite"
                : null,
            CharactersOnScreen = chars,
            LocationIds = locs,
            PrimaryLocationId = sEl.TryGetProperty("primary_location_id", out var pl)
                ? pl.GetString()
                : null,
            Clips = clips,
            DuplicateClipNumbers = duplicateClipNumbers,
        };
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private Task<ClipDialogueVerificationResult?> LoadClipDialogueVerificationAsync(
        string projectDir, int sceneNumber, int clipNumber, CancellationToken ct)
    {
        // Used to point at assets/review/*.verification.json, a path nothing ever wrote to (the
        // real writer, ClipDialogueVerificationService.SaveVerificationAsync, writes
        // assets/qa/*_dialogue_verification.json) — this field was always null in production
        // regardless of whether verification had actually run. Uses the service's own path
        // builder now instead of a second inline copy of the naming convention.
        var verPath = ClipDialogueVerificationService.BuildVerificationPath(projectDir, sceneNumber, clipNumber);
        return _dialogueVerificationCache.GetOrLoadAsync(
            verPath,
            (bytes, _) =>
            {
                var parsed = JsonSerializer.Deserialize<ClipDialogueVerificationResult>(
                    bytes, JsonDefaults.IndentedCaseInsensitive);
                if (parsed is null)
                    throw new InvalidOperationException("Empty dialogue verification JSON");
                return Task.FromResult(parsed);
            },
            ct);
    }

    public string? ResolveClipVideoPath(string projectId, int sceneNumber, int clipNumber)
    {
        var videoDir = Path.Combine(
            GetProjectDir(projectId),
            "assets",
            "video");

        if (!Directory.Exists(videoDir)) return null;

        // Match any take file starting with scene_XX_clip_YY (newest valid take file)
        var pattern = $"scene_{sceneNumber:D2}_clip_{clipNumber:D2}*.mp4";
        var latestTake = new DirectoryInfo(videoDir)
            .EnumerateFiles(pattern)
            .Where(fi => fi.Length >= 1024 && !fi.Name.StartsWith("_"))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault();

        return latestTake?.FullName;
    }

    /// <summary>
    /// WIP full-movie path from config <c>wip_movie_path</c> (default assets/movie_wip.mp4).
    /// Returns null if the file is missing or empty.
    /// </summary>
    public string? ResolveWipMoviePath(string projectId)
    {
        var projectDir = GetProjectDir(projectId);
        var cfg = GetConfigSync(projectId);
        var wipRel = "assets/movie_wip.mp4";
        if (cfg.TryGetValue("wip_movie_path", out var w) &&
            w.ValueKind == JsonValueKind.String &&
            w.GetString() is { Length: > 0 } s)
            wipRel = s.Replace('\\', '/').TrimStart('/');

        if (wipRel.Contains("..", StringComparison.Ordinal))
            return null;

        var full = Path.IsPathRooted(wipRel)
            ? wipRel
            : Path.GetFullPath(Path.Combine(projectDir, wipRel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(projectDir);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) && new FileInfo(full).Length >= 1024 ? full : null;
    }

    /// <summary>
    /// Multi-scene stitched preview path (legacy; play uses browser stitch)
    /// (assets/movie_preview.mp4). Null if missing/empty.
    /// </summary>
    public string? ResolvePreviewMoviePath(string projectId)
    {
        var path = Path.Combine(GetProjectDir(projectId), "assets", "movie_preview.mp4");
        return File.Exists(path) && new FileInfo(path).Length >= 1024 ? path : null;
    }

    /// <summary>Last successful YouTube upload for a project, or null if never uploaded.</summary>
    public async Task<YouTubeUploadInfo?> GetYouTubeUploadInfoAsync(string projectId, CancellationToken ct = default)
    {
        var path = Path.Combine(GetProjectDir(projectId), "assets", "youtube_upload.json");
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<YouTubeUploadInfo>(stream, JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveYouTubeUploadInfoAsync(string projectId, YouTubeUploadInfo info, CancellationToken ct = default)
    {
        var dir = Path.Combine(GetProjectDir(projectId), "assets");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "youtube_upload.json"),
            JsonSerializer.Serialize(info, JsonOpts),
            ct).ConfigureAwait(false);
    }

    public string ResolveScenesJsonPath(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var preferred = "scenes.json";
        var metaPath = Path.Combine(dir, "project.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("scenes_file", out var sf))
                {
                    var n = sf.GetString();
                    if (!string.IsNullOrWhiteSpace(n))
                        preferred = n!;
                }
            }
            catch { /* ignore */ }
        }

        var full = Path.Combine(dir, preferred);
        if (File.Exists(full))
            return full;
        if (!string.Equals(preferred, "scenes.json", StringComparison.OrdinalIgnoreCase))
        {
            var standard = Path.Combine(dir, "scenes.json");
            if (File.Exists(standard))
                return standard;
        }
        return Path.Combine(dir, preferred);
    }

    /// <summary>
    /// True when this user has a personal studio key (BYOK). Server env keys do not count
    /// unless <see cref="PageToMovieOptions.AllowServerApiKeyFallback"/> is on.
    /// </summary>
    public bool IsAnyStudioKeyConfigured(string? userId = null)
    {
        // Fakes mode uses key-free fake providers, so a key is effectively always "configured".
        // Mirrors the /health endpoint (xaiConfigured = ... || useFakes) so the fully-faked
        // pipeline is self-sufficient for offline dev/testing. Real mode is unaffected — the
        // BYOK setup gate still requires a real key.
        if (_opts.UseFakes) return true;

        if (_keyProvider is not null && !string.IsNullOrWhiteSpace(userId))
        {
            foreach (var provider in new[] { "grok", "gemini", "anthropic", "openai", "fal" })
            {
                if (_keyProvider.HasKey(userId, provider))
                    return true;
            }
        }

        // Ambient scope only after personal keys were loaded into the request — still OK
        // because GetKey no longer injects server env for signed-in users under BYOK.
        if (!string.IsNullOrWhiteSpace(ApiKeyScope.Current)
            || !string.IsNullOrWhiteSpace(ApiKeyScope.CurrentGemini)
            || !string.IsNullOrWhiteSpace(ApiKeyScope.CurrentAnthropic)
            || !string.IsNullOrWhiteSpace(ApiKeyScope.Get("openai")))
            return true;

        if (_opts.AllowServerApiKeyFallback)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"))
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FAL_API_KEY"))
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FAL_KEY")))
                return true;
            if (_keyProvider is not null)
            {
                foreach (var provider in new[] { "grok", "gemini", "anthropic", "fal", "openai" })
                {
                    if (_keyProvider.HasKey(null, provider))
                        return true;
                }
            }
        }

        return false;
    }

    public AdaptationStatus GetAdaptationStatus(string projectId, string? userId = null)
    {
        var dir = GetProjectDir(projectId);
        var book = ReadBookSourceStatus(projectId, dir);
        var stage1 = ReadStage1Status(projectId, dir);
        var screenplay = ScreenplayService.ReadStatus(this, projectId, stage1);
        var stage2 = ReadStage2PlanStatus(projectId, dir, stage1);
        // Fountain re-sign that changed Stage 1 makes an existing shot plan stale
        if (screenplay.DraftExists && screenplay.Dirty && stage2.Stage2Ready)
            stage2.Stage2Stale = true;
        // Any planning/gen key is enough for import→screenplay. Prefer ambient scope
        // (request middleware already loaded this user's personal keys), then provider
        // lookup for the real userId — never HasKey("grok") as a *user id* (old bug).
        var xai = IsAnyStudioKeyConfigured(userId);

        var cfg = GetConfigSync(projectId);
        var planningModel = cfg.TryGetValue("planning_model_name", out var pmEl) &&
                             pmEl.ValueKind == JsonValueKind.String &&
                             pmEl.GetString() is { Length: > 0 } pm
            ? pm
            : (cfg.TryGetValue("chat_model_name", out var cmEl) &&
               cmEl.ValueKind == JsonValueKind.String &&
               cmEl.GetString() is { Length: > 0 } cm
                ? cm
                : "");

        var cast = ReadCastStatus(projectId);

        // Fountain is the screenplay source of truth.
        // Flow: import → draft/approve → pin characters → shot plan → generate clips (Scenes).
        var next = "done";
        var hasSource = book.PdfExists || book.BookTextExists || screenplay.DraftExists ||
                        (stage1.Present && stage1.SceneCount > 0);
        if (!hasSource)
            next = "import_book";
        else if ((!stage1.Present || stage1.SceneCount == 0) && book.BookTextExists && !book.ReadyForStage1 &&
                 !screenplay.DraftExists)
            next = "fix_book_text";
        else if (!screenplay.DraftExists && book.BookTextExists)
            next = "draft_screenplay";
        else if (screenplay.DraftExists && (!screenplay.Signed || screenplay.Dirty))
            next = "sign_screenplay";
        else if (!stage1.Present || stage1.SceneCount == 0)
            next = screenplay.DraftExists ? "sign_screenplay" : "import_book";
        else if (!cast.ReadyForShots)
            next = "pin_characters";
        else if (!stage2.Stage2Ready)
            next = "run_stage2";
        else if (stage2.Stage2Stale)
            next = "replan_stage2";
        else
            next = "generate_clips";

        return new AdaptationStatus
        {
            ProjectId = projectId,
            Book = book,
            Screenplay = screenplay,
            Stage1 = stage1,
            Stage2 = stage2,
            Cast = cast,
            XaiConfigured = xai,
            PlanningModel = planningModel,
            NextStep = next,
            BookSubsteps = ReadBookSubsteps(projectId),
        };
    }

    /// <summary>
    /// Cast is ready when every member has a voice profile and (if a single on-screen face)
    /// a <em>locked</em> ref image — not merely a variant draft (<see cref="CharacterSummary.HasPreferred"/>).
    /// <see cref="CharacterSummary.VoiceOnly"/> skips the locked-image requirement.
    /// <see cref="CharacterSummary.IsGroup"/> is ignored for readiness (hidden on Characters UI).
    /// Empty cast (no seeds yet) is not ready. Used for next-step gating and video-gen spend protection.
    /// </summary>
    public CastStatus ReadCastStatus(string projectId)
    {
        var status = new CastStatus();
        try
        {
            var rows = ListCharacters(projectId);
            status.Total = rows.Count;
            if (rows.Count == 0)
                return status;

            var ready = 0;
            var missing = new List<string>();
            foreach (var c in rows)
            {
                var hasVoice = !string.IsNullOrWhiteSpace(c.VoiceProfile);
                // Voice-only: need voice profile, no portrait.
                // Group/chorus: production extras — not shown on Characters UI; never block readiness.
                if (c.IsGroup)
                {
                    ready++;
                    continue;
                }
                if (c.VoiceOnly)
                {
                    if (hasVoice)
                        ready++;
                    else
                        missing.Add(c.Key);
                    continue;
                }

                // A SILENT non-human seed (animal that never speaks) does NOT require a voice — only a locked
                // image if it appears on screen. A talking animal (has dialogue) is a speaking role and needs
                // a voice like any speaker, so it falls through below. Mirrors GetCastNotReadyForVideo so this
                // readiness gate (Scenes "Cast incomplete" banner + Generate button) agrees with the spend gate.
                var isNonHuman = c.SpeciesKind is { Length: > 0 } sk
                    && !sk.Trim().Equals("human", StringComparison.OrdinalIgnoreCase);
                if (isNonHuman && !c.Speaks && !hasVoice)
                {
                    if (c.Locked)
                        ready++;
                    else
                        missing.Add(c.Key);
                    continue;
                }

                // Locked only — HasPreferred can be unlocked variant_01 and is not enough to spend on video.
                if (c.Locked && hasVoice)
                    ready++;
                else
                    missing.Add(c.Key);
            }

            status.Ready = ready;
            status.ReadyForShots = ready == rows.Count && rows.Count > 0;
            status.Missing = missing;
        }
        catch
        {
            // leave defaults
        }

        return status;
    }

    /// <summary>
    /// Project-wide cast gate before any video spend: every <em>speaking</em> seed needs a voice profile;
    /// every single on-screen face needs a locked ref image (not just a variant draft).
    /// <see cref="CharacterSummary.VoiceOnly"/> skips the locked-image requirement.
    /// <see cref="CharacterSummary.IsGroup"/> never blocks (not shown for operator pin).
    /// A non-speaking seed (no voice profile, no voice label, no clone sample — e.g. an animal like the
    /// Lamb) does NOT require a voice; it still needs a locked image if it appears on screen.
    /// Empty cast is not ready. Returns human-readable missing items (empty when ready).
    /// </summary>
    public IReadOnlyList<string> GetCastNotReadyForVideo(string projectId)
    {
        var missing = new List<string>();
        IReadOnlyList<CharacterSummary> rows;
        try
        {
            rows = ListCharacters(projectId);
        }
        catch
        {
            return new[] { "could not load cast" };
        }

        if (rows.Count == 0)
        {
            missing.Add("no cast seeds yet — extract or define characters first");
            return missing;
        }

        foreach (var c in rows)
        {
            var hasVoice = !string.IsNullOrWhiteSpace(c.VoiceProfile);
            // Groups are not operator-pinned; never block video gen.
            if (c.IsGroup)
                continue;
            if (c.VoiceOnly)
            {
                if (!hasVoice)
                    missing.Add($"{c.Key}: voice profile");
                continue;
            }

            // A SILENT non-human seed (animal that never speaks) does NOT require a voice — only a locked
            // image if it appears on screen. A talking animal (has dialogue) is a speaking role and needs a
            // voice like any speaker, so it falls through to the voice+image requirement below.
            var isNonHuman = c.SpeciesKind is { Length: > 0 } sk
                && !sk.Trim().Equals("human", StringComparison.OrdinalIgnoreCase);
            if (isNonHuman && !c.Speaks && !hasVoice)
            {
                if (!c.Locked)
                    missing.Add($"{c.Key}: locked image");
                continue;
            }

            if (!hasVoice && !c.Locked)
                missing.Add($"{c.Key}: voice + locked image");
            else if (!hasVoice)
                missing.Add($"{c.Key}: voice profile");
            else if (!c.Locked)
                missing.Add($"{c.Key}: locked image");
        }

        return missing;
    }

    public async Task<string> SaveBookUploadAsync(
        string projectId,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        var dir = GetProjectDir(projectId);
        var source = Path.Combine(dir, "source");
        Directory.CreateDirectory(source);

        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe))
            throw new InvalidOperationException("file name required");

        var ext = Path.GetExtension(safe).ToLowerInvariant();
        if (ext is not (".pdf" or ".txt"))
            throw new InvalidOperationException("Only .pdf or .txt uploads are supported");

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        if (ext == ".txt")
        {
            var bookFull = Path.Combine(source, "book_full.txt");
            await File.WriteAllBytesAsync(bookFull, bytes, ct);
            return bookFull;
        }

        var dest = Path.Combine(source, safe);
        await File.WriteAllBytesAsync(dest, bytes, ct);
        return dest;
    }

    private BookSourceStatus ReadBookSourceStatus(string projectId, string projectDir)
    {
        var source = Path.Combine(projectDir, "source");
        var bookPath = Path.Combine(source, "book_full.txt");
        var metaPath = Path.Combine(source, "extract_meta.json");
        var imgDir = Path.Combine(source, "book_images");

        string? pdfName = null;
        if (Directory.Exists(source))
        {
            try
            {
                // One DirectoryInfo scan (Length already available; avoid re-stat via FileInfo).
                pdfName = new DirectoryInfo(source).EnumerateFiles()
                    .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name.Contains("nick", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenByDescending(f => f.Length)
                    .Select(f => f.Name)
                    .FirstOrDefault();
            }
            catch { /* ignore */ }
        }

        var status = new BookSourceStatus
        {
            PdfExists = !string.IsNullOrEmpty(pdfName),
            PdfName = pdfName,
            BookTextExists = File.Exists(bookPath),
            BookTextPath = File.Exists(bookPath) ? bookPath : null,
            BookTextBytes = File.Exists(bookPath) ? new FileInfo(bookPath).Length : 0,
        };

        if (Directory.Exists(imgDir))
        {
            try
            {
                status.PageImageCount = new DirectoryInfo(imgDir).EnumerateFiles()
                    .Count(f =>
                    {
                        var e = f.Extension.ToLowerInvariant();
                        return e is ".jpg" or ".jpeg" or ".png" or ".webp";
                    });
            }
            catch { /* ignore */ }
        }

        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                status.TextQuality = root.TryGetProperty("text_quality", out var tq) ? tq.GetString() : null;
                status.BookKind = root.TryGetProperty("book_kind", out var bk) ? bk.GetString() : null;
                status.TextEngine = root.TryGetProperty("text_engine", out var te) ? te.GetString() : null;
                if (root.TryGetProperty("text_words", out var tw) && tw.TryGetInt32(out var words))
                    status.TextWords = words;
                if (root.TryGetProperty("suggested_total_minutes", out var sm) && sm.TryGetInt32(out var mins))
                    status.SuggestedTotalMinutes = mins;
                if (root.TryGetProperty("natural_runtime_minutes", out var nr) && nr.TryGetInt32(out var nat))
                    status.NaturalRuntimeMinutes = nat;
                if (root.TryGetProperty("target_runtime_minutes", out var trm) && trm.TryGetInt32(out var tgt))
                    status.TargetRuntimeMinutes = tgt;
                else if (status.SuggestedTotalMinutes is int smv)
                    status.TargetRuntimeMinutes = smv;
                if (status.NaturalRuntimeMinutes is null && status.SuggestedTotalMinutes is int smv2)
                    status.NaturalRuntimeMinutes = smv2;
                if (root.TryGetProperty("runtime_mode", out var rmode) && rmode.ValueKind == JsonValueKind.String)
                    status.RuntimeMode = rmode.GetString();
                if (root.TryGetProperty("suggested_chunk_pages", out var sc) && sc.TryGetInt32(out var chunks))
                    status.SuggestedChunkPages = chunks;
                if (root.TryGetProperty("ready_for_stage1", out var r) &&
                    (r.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    status.ReadyForStage1 = r.GetBoolean();

                if (root.TryGetProperty("analysis", out var an) && an.ValueKind == JsonValueKind.Object)
                {
                    if (an.TryGetProperty("garbage_score", out var gs) && gs.TryGetDouble(out var gsv))
                        status.GarbageScore = gsv;
                    if (string.IsNullOrEmpty(status.TextQuality) &&
                        an.TryGetProperty("text_quality", out var atq))
                        status.TextQuality = atq.GetString();
                }

                if (root.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in notes.EnumerateArray())
                    {
                        var s = n.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            status.Notes.Add(s!);
                    }
                }
            }
            catch { /* ignore */ }
        }

        // Overlay user retarget from pipeline_config when present.
        try
        {
            var cfg = GetConfigSync(projectId);
            if (cfg.TryGetValue("target_runtime_minutes", out var ctr) &&
                ctr.ValueKind == JsonValueKind.Number && ctr.TryGetInt32(out var ctm) && ctm > 0)
            {
                status.TargetRuntimeMinutes = FilmRuntime.ClampMinutes(ctm);
                status.SuggestedTotalMinutes = status.TargetRuntimeMinutes;
            }
            if (cfg.TryGetValue("natural_runtime_minutes", out var cnr) &&
                cnr.ValueKind == JsonValueKind.Number && cnr.TryGetInt32(out var cnat) && cnat > 0)
                status.NaturalRuntimeMinutes = FilmRuntime.ClampMinutes(cnat);
            if (cfg.TryGetValue("runtime_mode", out var crm) && crm.ValueKind == JsonValueKind.String)
                status.RuntimeMode = crm.GetString();
            if (string.IsNullOrWhiteSpace(status.RuntimeMode) &&
                status.NaturalRuntimeMinutes is int n && status.TargetRuntimeMinutes is int tg)
                status.RuntimeMode = tg == n ? "natural" : tg < n ? "reduced" : "custom";
        }
        catch { /* ignore */ }

        // Prefer extract_meta.ready_for_stage1 when present (set by BookPrepareService strategy).
        // Only fall back to heuristics when meta is missing or incomplete.
        var metaReadySet = File.Exists(metaPath) && status.TextQuality is not null;
        if (!status.BookTextExists)
        {
            status.ReadyForStage1 = false;
        }
        else if (!metaReadySet)
        {
            if (status.TextQuality is null && status.BookTextBytes > 200)
            {
                // No meta yet — allow Stage 1 if plain text looks present (user may have uploaded .txt)
                status.TextQuality = "unknown";
                status.ReadyForStage1 = true;
            }
            else if (string.Equals(status.TextQuality, "good", StringComparison.OrdinalIgnoreCase) &&
                     status.GarbageScore < 0.45)
            {
                status.ReadyForStage1 = true;
            }
        }
        else if (!status.ReadyForStage1)
        {
            // Strategy often sets ready=false for "prefer vision" even when text is usable
            // (picture books). Allow Stage 1 / re-run when quality is good enough.
            if (string.Equals(status.TextQuality, "good", StringComparison.OrdinalIgnoreCase) &&
                status.GarbageScore < 0.45 &&
                status.BookTextBytes > 200)
            {
                status.ReadyForStage1 = true;
                if (status.Notes.All(n => !n.Contains("Stage 1 unlocked", StringComparison.OrdinalIgnoreCase)))
                    status.Notes.Add(
                        "Stage 1 unlocked: text quality is good enough (vision still optional for better OCR).");
            }
        }

        if (status.BookTextExists)
        {
            try
            {
                var text = File.ReadAllText(bookPath);
                status.Preview = text.Length <= 600 ? text : text[..600] + "…";
                if (status.TextWords is null or 0)
                {
                    status.TextWords = text.Split(
                        WordSplitChars,
                        StringSplitOptions.RemoveEmptyEntries).Length;
                }
            }
            catch { /* ignore */ }
        }

        // Re-run path: existing scenes.json + book text is enough even if prepare still flags "not ready"
        try
        {
            var scenesPath = Path.Combine(projectDir, "scenes.json");
            if (!status.ReadyForStage1 &&
                status.BookTextExists &&
                status.BookTextBytes > 200 &&
                File.Exists(scenesPath) &&
                new FileInfo(scenesPath).Length > 64)
            {
                status.ReadyForStage1 = true;
                if (status.Notes.All(n => !n.Contains("Re-run Stage 1", StringComparison.OrdinalIgnoreCase)))
                    status.Notes.Add(
                        "Re-run Stage 1 enabled: scenes.json already exists and book_full.txt is present.");
            }
        }
        catch { /* ignore */ }

        return status;
    }

    private Stage1Status ReadStage1Status(string projectId, string projectDir)
    {
        // Fountain is the screenplay source of truth
        try
        {
            var draftPath = ScreenplayService.GetDraftPath(this, projectId);
            if (File.Exists(draftPath) || ScreenplayService.EnsureCanonicalDraft(this, projectId))
            {
                var model = ScreenplayService.TryBuildModelFromProject(this, projectId);
                if (model is not null)
                    return ScreenplayService.StatusFromFountainModel(
                        model, ScreenplayService.GetDraftPath(this, projectId));
            }
        }
        catch { /* ignore */ }

        return new Stage1Status { Present = false, ScenesFile = "scenes.json" };
    }

    private Stage2PlanStatus ReadStage2PlanStatus(string projectId, string projectDir, Stage1Status stage1)
    {
        var bpPath = FindBlueprintPathSync(projectId);
        var status = new Stage2PlanStatus
        {
            Stage1Exists = stage1.Present && stage1.SceneCount > 0,
            Stage1Scenes = stage1.SceneCount,
            BlueprintExists = bpPath is not null && File.Exists(bpPath),
            BlueprintPath = bpPath,
            BlueprintFileName = bpPath is not null ? Path.GetFileName(bpPath) : null,
        };

        if (bpPath is null || !File.Exists(bpPath))
            return status;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(bpPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
            {
                status.Stage2Scenes = scenes.GetArrayLength();
                foreach (var s in scenes.EnumerateArray())
                {
                    if (s.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array)
                        status.Stage2Clips += vc.GetArrayLength();
                }
            }

            status.Stage2Ready = status.Stage2Scenes > 0 && status.Stage2Clips > 0;

            if (root.TryGetProperty("stage2_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                status.LastCompletedAt = meta.TryGetProperty("completed_at", out var ca)
                    ? ca.GetString()
                    : meta.TryGetProperty("last_partial_at", out var lp) ? lp.GetString() : null;
                status.LastRunMessage = meta.TryGetProperty("last_run_message", out var lm)
                    ? lm.GetString()
                    : null;
                if (meta.TryGetProperty("validation_issue_count", out var vic) && vic.TryGetInt32(out var n))
                    status.ValidationIssueCount = n;
            }

            if (string.IsNullOrEmpty(status.LastCompletedAt))
            {
                try
                {
                    status.LastCompletedAt = File.GetLastWriteTime(bpPath).ToString("yyyy-MM-ddTHH:mm:ss");
                }
                catch { /* ignore */ }
            }

            // Stale when Stage 1 bible is newer than blueprint
            var s1Path = ResolveScenesJsonPath(projectId);
            if (File.Exists(s1Path) && status.Stage2Ready)
            {
                try
                {
                    var s1m = File.GetLastWriteTimeUtc(s1Path);
                    var bpm = File.GetLastWriteTimeUtc(bpPath);
                    status.Stage2Stale = s1m > bpm.AddSeconds(1);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        return status;
    }

    public string? ResolveCompositePath(string projectId, int sceneNumber)
    {
        var dir = GetProjectDir(projectId);
        // Remux writes scene_XX.mp4; older/python path used scene_XX_complete.mp4
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, "assets", "video", $"scene_{sceneNumber:D2}.mp4"),
                     Path.Combine(dir, "assets", "scenes", $"scene_{sceneNumber:D2}.mp4"),
                     Path.Combine(dir, "assets", "video", $"scene_{sceneNumber:D2}_complete.mp4"),
                     Path.Combine(dir, "assets", "scenes", $"scene_{sceneNumber:D2}_complete.mp4"),
                 })
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length >= 1024)
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Whether movie_wip is missing, older than inputs, or built from a different set of
    /// scenes (added/deleted). Does not trigger clip regen — mux freshness only.
    /// </summary>
    public WipFreshness AssessWipFreshness(string projectId)
    {
        var result = new WipFreshness();
        var projectDir = GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");

        // Physical path even when file missing (for manifest path)
        var wipFullPath = ResolveWipMovieFullPath(projectId);
        var wipExists = wipFullPath is not null &&
                        File.Exists(wipFullPath) &&
                        new FileInfo(wipFullPath).Length >= 1024;

        if (wipExists)
        {
            var fi = new FileInfo(wipFullPath!);
            result.Exists = true;
            result.Path = Path.GetRelativePath(projectDir, wipFullPath!).Replace('\\', '/');
            result.Bytes = fi.Length;
            result.UpdatedAt = fi.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ss");
        }
        else
        {
            result.Exists = false;
            result.Path = "assets/movie_wip.mp4";
            try
            {
                var cfg = GetConfigSync(projectId);
                if (cfg.TryGetValue("wip_movie_path", out var w) &&
                    w.ValueKind == JsonValueKind.String &&
                    w.GetString() is { Length: > 0 } s)
                    result.Path = s.Replace('\\', '/').TrimStart('/');
            }
            catch { /* ignore */ }
        }

        var clipsByScene = IndexExactClipsByScene(videoDir);
        var blueprintScenes = GetBlueprintSceneNumbers(projectId);
        var scenesToRemux = ListScenesToRemuxForWip(projectId, clipsByScene, blueprintScenes);
        result.ScenesToRemux = scenesToRemux;

        // Stale = missing composite, clips newer, no .sources.json,
        // or manifest clip set ≠ current exact/blueprint clips.
        var staleScenes = new List<int>();
        foreach (var sn in scenesToRemux)
        {
            if (IsSceneCompositeDirty(projectId, sn, videoDir, clipsByScene))
                staleScenes.Add(sn);
        }
        result.StaleScenes = staleScenes;

        // WIP sources = Stage 2 scene composites (blueprint-filtered when available)
        var currentSources = ListWipSourceFilesForProject(projectId, videoDir, blueprintScenes);
        result.CanBuild = scenesToRemux.Count > 0 || currentSources.Count > 0;

        if (!result.CanBuild)
        {
            result.Stale = true;
            result.Reason = "No scene or clip videos on disk to build WIP";
            return result;
        }

        // Stage 2 blueprint newer than last WIP build → always remux
        var bpPath = FindBlueprintPathSync(projectId);
        DateTime? bpMtime = bpPath is not null && File.Exists(bpPath)
            ? new FileInfo(bpPath).LastWriteTimeUtc
            : null;

        if (staleScenes.Count > 0)
        {
            result.Stale = true;
            result.Reason =
                $"Scene composite(s) dirty (missing/out of date): {string.Join(", ", staleScenes.Select(n => $"S{n:D2}"))}";
            return result;
        }

        if (!result.Exists || wipFullPath is null)
        {
            result.Stale = true;
            result.Reason = "WIP missing — needs rebuild";
            return result;
        }

        var manifestPath = ClipFileNaming.WipSourcesManifestPath(wipFullPath);
        if (bpMtime is DateTime bpm && File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (doc.RootElement.TryGetProperty("blueprintMtimeUtc", out var bm) &&
                    bm.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(bm.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var recordedBp))
                {
                    if (bpm > recordedBp.ToUniversalTime().AddSeconds(1))
                    {
                        result.Stale = true;
                        result.Reason = "Stage 2 blueprint changed since last WIP — remux all scenes + rebuild";
                        return result;
                    }
                }
                else if (doc.RootElement.TryGetProperty("builtAtUtc", out var built) &&
                         built.ValueKind == JsonValueKind.String &&
                         DateTime.TryParse(built.GetString(), null,
                             System.Globalization.DateTimeStyles.RoundtripKind, out var builtAt) &&
                         bpm > builtAt.ToUniversalTime().AddSeconds(1))
                {
                    result.Stale = true;
                    result.Reason = "Stage 2 blueprint newer than WIP — remux all scenes + rebuild";
                    return result;
                }

                // Scene list in plan vs last build
                if (doc.RootElement.TryGetProperty("sceneNumbers", out var sns) &&
                    sns.ValueKind == JsonValueKind.Array &&
                    blueprintScenes is { Count: > 0 })
                {
                    var recorded = sns.EnumerateArray()
                        .Select(e => e.TryGetInt32(out var n) ? n : 0)
                        .Where(n => n > 0)
                        .OrderBy(n => n)
                        .ToList();
                    var planned = blueprintScenes.OrderBy(n => n).ToList();
                    if (!recorded.SequenceEqual(planned))
                    {
                        result.Stale = true;
                        result.Reason = "Stage 2 scene list changed — remux all scenes + rebuild";
                        return result;
                    }
                }
            }
            catch { /* fall through */ }
        }
        else if (bpMtime is DateTime bpm2)
        {
            var wipMtime = new FileInfo(wipFullPath).LastWriteTimeUtc;
            if (bpm2 > wipMtime.AddSeconds(1))
            {
                result.Stale = true;
                result.Reason = "Stage 2 blueprint newer than WIP — remux all scenes + rebuild";
                return result;
            }
        }

        // Manifest: detect added/removed/replaced sources vs last successful WIP build
        var manifestMismatch = CompareWipSourcesManifest(wipFullPath, currentSources);
        if (manifestMismatch is { Length: > 0 })
        {
            result.Stale = true;
            result.Reason = manifestMismatch;
            return result;
        }

        // No manifest (old WIP): fall back to mtime — any source newer than WIP
        if (!File.Exists(manifestPath))
        {
            var wipMtime = new FileInfo(wipFullPath).LastWriteTimeUtc;
            foreach (var src in currentSources)
            {
                try
                {
                    if (new FileInfo(src).LastWriteTimeUtc > wipMtime.AddSeconds(1))
                    {
                        result.Stale = true;
                        result.Reason = "Sources newer than WIP (no build manifest — rebuild recommended)";
                        return result;
                    }
                }
                catch { /* ignore */ }
            }
        }

        result.Stale = false;
        result.Reason = "Up to date";
        return result;
    }

    /// <summary>
    /// True when scene should be remuxed before play/WIP.
    /// Marks composites dirty when scene_XX.mp4.sources.json is missing so cuts get rebuilt.
    /// </summary>
    public bool IsSceneCompositeDirty(
        string projectId,
        int sceneNum,
        string? videoDir = null,
        Dictionary<int, List<FileInfo>>? clipsByScene = null)
    {
        videoDir ??= Path.Combine(GetProjectDir(projectId), "assets", "video");
        clipsByScene ??= IndexExactClipsByScene(videoDir);

        var expectedNames = GetExpectedClipFileNames(projectId, sceneNum, videoDir, clipsByScene);
        if (expectedNames.Count == 0)
            return false;

        var composite = ResolveCompositePath(projectId, sceneNum);
        if (composite is null || !File.Exists(composite))
            return true;

        var maxClipMtime = DateTime.MinValue;
        foreach (var name in expectedNames)
        {
            var path = Path.Combine(videoDir, name);
            if (!File.Exists(path)) continue;
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt > maxClipMtime) maxClipMtime = mt;
        }
        if (maxClipMtime > File.GetLastWriteTimeUtc(composite).AddSeconds(1))
            return true;

        var manifestPath = ClipFileNaming.SceneSourcesManifestPath(composite);
        var remuxOut = Path.Combine(videoDir, $"scene_{sceneNum:D2}.mp4");
        if (!File.Exists(manifestPath) && File.Exists(remuxOut))
            manifestPath = ClipFileNaming.SceneSourcesManifestPath(remuxOut);

        // No strict manifest → treat as dirty (old remux may have concat'd .native + orphans)
        if (!File.Exists(manifestPath))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("clips", out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
                return true;

            var recorded = new List<string>();
            foreach (var el in clipsEl.EnumerateArray())
            {
                if (el.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    recorded.Add(name);
            }

            var expectedSorted = expectedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var recSorted = recorded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!expectedSorted.SequenceEqual(recSorted, StringComparer.OrdinalIgnoreCase))
                return true;

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Exact clip file names that should make up the scene composite.</summary>
    private List<string> GetExpectedClipFileNames(
        string projectId,
        int sceneNum,
        string videoDir,
        Dictionary<int, List<FileInfo>> clipsByScene)
    {
        var allowed = TryBlueprintClipNumbers(projectId, sceneNum);
        var names = new List<string>();
        if (clipsByScene.TryGetValue(sceneNum, out var files))
        {
            foreach (var fi in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = fi.Name;
                if (!ClipFileNaming.IsExactClipFileName(name)) continue;
                if (allowed is { Count: > 0 })
                {
                    if (!int.TryParse(name.AsSpan(14, 2), out var cn) || !allowed.Contains(cn))
                        continue;
                }
                names.Add(name);
            }
        }

        // Also include expected blueprint slots that exist as exact files
        if (allowed is { Count: > 0 })
        {
            foreach (var cn in allowed.OrderBy(c => c))
            {
                var name = $"scene_{sceneNum:D2}_clip_{cn:D2}.mp4";
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var path = Path.Combine(videoDir, name);
                if (File.Exists(path) && new FileInfo(path).Length >= 1024)
                    names.Add(name);
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private HashSet<int>? TryBlueprintClipNumbers(string projectId, int sceneNum)
    {
        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is null) return null;
            if (!bp.RootElement.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var s in scenes.EnumerateArray())
            {
                var sn = s.TryGetProperty("scene_number", out var snEl) && snEl.TryGetInt32(out var v) ? v : 0;
                if (sn != sceneNum) continue;
                if (!s.TryGetProperty("veo_clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
                    return null;
                var set = new HashSet<int>();
                foreach (var c in clips.EnumerateArray())
                {
                    var cn = ClipKeying.ClipNumber(c);
                    if (cn > 0)
                        set.Add(cn);
                }
                return set.Count > 0 ? set : null;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Stage 2 scene numbers from blueprint, or null if no plan.</summary>
    public List<int>? GetBlueprintSceneNumbers(string projectId)
    {
        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is null) return null;
            if (!bp.RootElement.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<int>();
            foreach (var s in scenes.EnumerateArray())
            {
                if (s.TryGetProperty("scene_number", out var sn) && sn.TryGetInt32(out var n) && n > 0)
                    list.Add(n);
            }
            return list.Count > 0 ? list.Distinct().OrderBy(x => x).ToList() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Scenes to remux before WIP: Stage 2 order when blueprint exists, only those with clips on disk.
    /// </summary>
    public List<int> ListScenesToRemuxForWip(string projectId) =>
        ListScenesToRemuxForWip(
            projectId,
            IndexExactClipsByScene(Path.Combine(GetProjectDir(projectId), "assets", "video")),
            GetBlueprintSceneNumbers(projectId));

    private static List<int> ListScenesToRemuxForWip(
        string projectId,
        Dictionary<int, List<FileInfo>> clipsByScene,
        List<int>? blueprintScenes)
    {
        var withClips = clipsByScene.Keys.Where(k => clipsByScene[k].Count > 0).ToHashSet();
        if (blueprintScenes is { Count: > 0 })
            return blueprintScenes.Where(withClips.Contains).ToList();
        return withClips.OrderBy(n => n).ToList();
    }

    private static Dictionary<int, List<FileInfo>> IndexExactClipsByScene(string videoDir)
    {
        var clipsByScene = new Dictionary<int, List<FileInfo>>();
        if (!Directory.Exists(videoDir)) return clipsByScene;
        foreach (var fi in new DirectoryInfo(videoDir).EnumerateFiles("scene_*_clip_*.mp4"))
        {
            var name = fi.Name;
            if (!ClipFileNaming.IsExactClipFileName(name)) continue;
            if (!int.TryParse(name.AsSpan(6, 2), out var sn) || sn <= 0) continue;
            if (fi.Length < 1024) continue;
            if (!clipsByScene.TryGetValue(sn, out var list))
            {
                list = new List<FileInfo>();
                clipsByScene[sn] = list;
            }
            list.Add(fi);
        }
        return clipsByScene;
    }

    /// <summary>WIP concat inputs: scene composites for Stage 2 scenes only when blueprint exists.</summary>
    public List<string> ListWipSourceFilesForProject(string projectId)
    {
        var videoDir = Path.Combine(GetProjectDir(projectId), "assets", "video");
        return ListWipSourceFilesForProject(projectId, videoDir, GetBlueprintSceneNumbers(projectId));
    }

    private List<string> ListWipSourceFilesForProject(
        string projectId,
        string videoDir,
        List<int>? blueprintScenes)
    {
        if (!Directory.Exists(videoDir))
            return new List<string>();

        if (blueprintScenes is { Count: > 0 })
        {
            var list = new List<string>();
            foreach (var sn in blueprintScenes)
            {
                var path = ResolveCompositePath(projectId, sn);
                if (path is not null)
                    list.Add(path);
            }
            if (list.Count > 0)
            {
                var creditsPath = Path.Combine(videoDir, "credits.mp4");
                if (list.Count == blueprintScenes.Count &&
                    File.Exists(creditsPath) &&
                    new FileInfo(creditsPath).Length >= 1024 &&
                    !list.Contains(creditsPath, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(creditsPath);
                }
                return list;
            }
        }

        return ClipFileNaming.ListWipSourceFiles(videoDir);
    }

    /// <summary>Full path for WIP file from config (may not exist yet).</summary>
    public string ResolveWipMovieFullPath(string projectId)
    {
        var projectDir = GetProjectDir(projectId);
        var cfg = GetConfigSync(projectId);
        var wipRel = "assets/movie_wip.mp4";
        if (cfg.TryGetValue("wip_movie_path", out var w) &&
            w.ValueKind == JsonValueKind.String &&
            w.GetString() is { Length: > 0 } s)
            wipRel = s.Replace('\\', '/').TrimStart('/');

        if (wipRel.Contains("..", StringComparison.Ordinal))
            return Path.Combine(projectDir, "assets", "movie_wip.mp4");

        return Path.IsPathRooted(wipRel)
            ? wipRel
            : Path.GetFullPath(Path.Combine(projectDir, wipRel.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// null if match or no check; reason string if sources added/removed/changed vs last WIP build.
    /// </summary>
    private static string? CompareWipSourcesManifest(string wipPath, IReadOnlyList<string> currentSources)
    {
        var manifestPath = ClipFileNaming.WipSourcesManifestPath(wipPath);
        if (!File.Exists(manifestPath))
            return null; // caller uses mtime fallback

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("sources", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return "WIP manifest invalid — rebuild needed";

            var recorded = new List<(string Name, long Bytes, DateTime Mtime)>();
            foreach (var el in arr.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                long bytes = 0;
                if (el.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bl))
                    bytes = bl;
                var mtime = DateTime.MinValue;
                if (el.TryGetProperty("mtimeUtc", out var m) &&
                    m.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(m.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var mt))
                    mtime = mt.ToUniversalTime();
                recorded.Add((name, bytes, mtime));
            }

            var current = currentSources
                .Select(f =>
                {
                    var fi = new FileInfo(f);
                    return (Name: fi.Name, Bytes: fi.Length, Mtime: fi.LastWriteTimeUtc);
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var recSorted = recorded
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var curNames = current.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recNames = recSorted.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = curNames.Except(recNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var removed = recNames.Except(curNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

            if (added.Count > 0 || removed.Count > 0)
            {
                var parts = new List<string>();
                if (added.Count > 0)
                    parts.Add("added " + string.Join(", ", added));
                if (removed.Count > 0)
                    parts.Add("removed " + string.Join(", ", removed));
                return "Scene set changed (" + string.Join("; ", parts) + ")";
            }

            foreach (var c in current)
            {
                var r = recSorted.FirstOrDefault(x =>
                    string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                if (r.Name is null) continue;
                if (c.Bytes != r.Bytes ||
                    Math.Abs((c.Mtime - r.Mtime).TotalSeconds) > 1.5)
                    return $"Source changed: {c.Name}";
            }

            if (current.Count != recSorted.Count)
                return $"Source count {current.Count} vs last build {recSorted.Count}";

            return null;
        }
        catch
        {
            return "WIP manifest unreadable — rebuild needed";
        }
    }

    private static bool HasCompositeFile(
        Dictionary<string, long> videoIndex,
        Dictionary<string, long> scenesIndex,
        int sceneNumber)
    {
        foreach (var name in new[]
                 {
                     $"scene_{sceneNumber:D2}.mp4",
                     $"scene_{sceneNumber:D2}_complete.mp4",
                 })
        {
            if (videoIndex.TryGetValue(name, out var v) && v >= 1024) return true;
            if (scenesIndex.TryGetValue(name, out var s) && s >= 1024) return true;
        }
        return false;
    }

    private Task<Dictionary<string, long>> GetDirIndexAsync(string dir, CancellationToken ct) =>
        _readCache.GetOrIndexDirAsync(
            dir,
            (d, _) => Task.FromResult(IndexDirFiles(d)),
            ct);

    /// <summary>
    /// Video index for a project's own assets/video, filled in with the PARENT project's video index
    /// for any filename the fork doesn't have locally. A fork skips copying video (ForkSkipExtensions)
    /// — its own index is empty for clips it never regenerated — but the clip-video HTTP endpoint
    /// (GET .../scenes/{n}/clips/{c}/video) already falls back to serving the parent's file when a
    /// forkable source kept its media server-side. Scene listing/detail need the SAME fallback,
    /// otherwise ClipOnDisk reports false for a clip the endpoint would actually serve fine, and the
    /// client never even attempts to fetch/stitch/play it.
    /// </summary>
    private async Task<Dictionary<string, long>> GetVideoIndexWithParentFallbackAsync(
        string projectId, string videoDir, CancellationToken ct)
    {
        var index = await GetDirIndexAsync(videoDir, ct).ConfigureAwait(false);
        try
        {
            var parentId = (await GetProjectAsync(projectId, ct).ConfigureAwait(false))?.ParentProjectId;
            if (string.IsNullOrWhiteSpace(parentId)) return index;
            var parentDir = await GetProjectDirAsync(parentId, ct).ConfigureAwait(false);
            var parentIndex = await GetDirIndexAsync(Path.Combine(parentDir, "assets", "video"), ct).ConfigureAwait(false);
            foreach (var kv in parentIndex)
                if (!index.ContainsKey(kv.Key))
                    index[kv.Key] = kv.Value;
        }
        catch { /* best effort — never block scene listing on a broken/missing parent link */ }
        return index;
    }

    private static Dictionary<string, long> IndexDirFiles(string dir)
    {
        // Directory metadata is sync-only; cheap compared to reading file contents.
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
            return map;
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFiles())
            {
                try
                {
                    map[info.Name] = info.Length;
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
        return map;
    }

    /// <summary>
    /// Bulk variant of "is this clip present" for scene-list rendering — takes a directory scan
    /// preloaded once per request (videoIndex) rather than a per-clip check, so it stays sync and
    /// dictionary-based instead of routing through MediaSyncLocator (SQL-backed, async): that
    /// would mean one registry query per clip instead of one directory scan per scene. See
    /// MediaSyncLocator's doc comment and FilmJobService.ClipPresentOnServerOrClient for the
    /// single-clip sibling of this same "why not just unify them" call.
    /// </summary>
    private static bool ClipOnDisk(Dictionary<string, long> videoIndex, int scene, int clip)
    {
        var basePrefix = $"scene_{scene:D2}_clip_{clip:D2}";
        var mp4Name = basePrefix + ".mp4";

        if (videoIndex.TryGetValue(mp4Name, out var sz) && sz >= 1024)
            return true;
        if (videoIndex.ContainsKey(mp4Name + ".client.json"))
            return true;
        if (videoIndex.ContainsKey(basePrefix + ".clip.json"))
            return true;

        return videoIndex.Keys.Any(k =>
            k.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) &&
            (k.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
             k.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase) ||
             k.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase)));
    }

    private Dictionary<string, JsonElement> LoadCharacterSeeds(string projectId)
    {
        // Prefer cast_seeds.json, then blueprint.
        try
        {
            foreach (var name in new[] { ScreenplayService.CastSeedsFileName })
            {
                var castPath = Path.Combine(GetProjectDir(projectId), "source", name);
                if (!File.Exists(castPath)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(castPath));
                var root = doc.RootElement;
                JsonElement seedEl = default;
                if (root.TryGetProperty("character_seed_tokens", out var s) && s.ValueKind == JsonValueKind.Object)
                    seedEl = s;
                else if (root.TryGetProperty("global_production_variables", out var g) &&
                         g.TryGetProperty("character_seed_tokens", out var s2) &&
                         s2.ValueKind == JsonValueKind.Object)
                    seedEl = s2;
                if (seedEl.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in seedEl.EnumerateObject())
                        dict[p.Name] = p.Value.Clone();
                    if (dict.Count > 0)
                        return dict;
                }
            }
        }
        catch { /* fall through */ }

        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is not null &&
                bp.RootElement.TryGetProperty("global_production_variables", out var gpv) &&
                gpv.TryGetProperty("character_seed_tokens", out var seeds) &&
                seeds.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in seeds.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                if (dict.Count > 0)
                    return dict;
            }
        }
        catch { /* fall through */ }

        try
        {
            var model = ScreenplayService.TryBuildModelFromProject(this, projectId);
            if (model is not null &&
                model.TryGetValue("global_production_variables", out var gpvObj) &&
                gpvObj is Dictionary<string, object?> gpv &&
                gpv.TryGetValue("character_seed_tokens", out var charObj) &&
                charObj is Dictionary<string, object?> charDict &&
                charDict.Count > 0)
            {
                var json = JsonSerializer.Serialize(charDict);
                using var doc = JsonDocument.Parse(json);
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                if (dict.Count > 0)
                    return dict;
            }
        }
        catch { /* fall through */ }

        var scenesPath = GetScenesPath(projectId);
        if (!File.Exists(scenesPath))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        using var scenesDoc = JsonDocument.Parse(File.ReadAllText(scenesPath));
        if (scenesDoc.RootElement.TryGetProperty("global_production_variables", out var g2) &&
            g2.TryGetProperty("character_seed_tokens", out var s3) &&
            s3.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in s3.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
            return dict;
        }
        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// location_seed_tokens from cast_seeds / blueprint / scenes — same precedence as character seeds.
    /// </summary>
    private Dictionary<string, JsonElement> LoadLocationSeeds(string projectId)
    {
        try
        {
            foreach (var name in new[] { ScreenplayService.CastSeedsFileName })
            {
                var castPath = Path.Combine(GetProjectDir(projectId), "source", name);
                if (!File.Exists(castPath)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(castPath));
                var root = doc.RootElement;
                JsonElement seedEl = default;
                if (root.TryGetProperty("location_seed_tokens", out var s) && s.ValueKind == JsonValueKind.Object)
                    seedEl = s;
                else if (root.TryGetProperty("global_production_variables", out var g) &&
                         g.TryGetProperty("location_seed_tokens", out var s2) &&
                         s2.ValueKind == JsonValueKind.Object)
                    seedEl = s2;
                if (seedEl.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in seedEl.EnumerateObject())
                        dict[p.Name] = p.Value.Clone();
                    if (dict.Count > 0)
                        return dict;
                }
            }
        }
        catch { /* fall through */ }

        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is not null &&
                bp.RootElement.TryGetProperty("global_production_variables", out var gpv) &&
                gpv.TryGetProperty("location_seed_tokens", out var seeds) &&
                seeds.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in seeds.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                if (dict.Count > 0)
                    return dict;
            }
        }
        catch { /* fall through */ }

        try
        {
            var model = ScreenplayService.TryBuildModelFromProject(this, projectId);
            if (model is not null &&
                model.TryGetValue("global_production_variables", out var gpvObj) &&
                gpvObj is Dictionary<string, object?> gpv &&
                gpv.TryGetValue("location_seed_tokens", out var locObj) &&
                locObj is Dictionary<string, object?> locDict &&
                locDict.Count > 0)
            {
                var json = JsonSerializer.Serialize(locDict);
                using var doc = JsonDocument.Parse(json);
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                if (dict.Count > 0)
                    return dict;
            }
        }
        catch { /* fall through */ }

        var scenesPath = GetScenesPath(projectId);
        if (!File.Exists(scenesPath))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var scenesDoc = JsonDocument.Parse(File.ReadAllText(scenesPath));
            if (scenesDoc.RootElement.TryGetProperty("global_production_variables", out var g2) &&
                g2.TryGetProperty("location_seed_tokens", out var s3) &&
                s3.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in s3.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                return dict;
            }
        }
        catch { /* ignore */ }

        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, JsonElement> LoadWardrobeLocks(string projectId)
    {
        // Prefer cast_seeds.json, then blueprint, then scenes.json — same precedence as
        // LoadCharacterSeeds, since UpdateWardrobeLockText keeps all three in sync.
        static Dictionary<string, JsonElement>? TryRead(JsonElement root)
        {
            JsonElement el = default;
            if (root.TryGetProperty("wardrobe_lock_tokens", out var s) && s.ValueKind == JsonValueKind.Object)
                el = s;
            else if (root.TryGetProperty("global_production_variables", out var g) &&
                     g.TryGetProperty("wardrobe_lock_tokens", out var s2) &&
                     s2.ValueKind == JsonValueKind.Object)
                el = s2;
            if (el.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
            return dict.Count > 0 ? dict : null;
        }

        try
        {
            var castPath = GetCastPath(projectId);
            if (File.Exists(castPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(castPath));
                if (TryRead(doc.RootElement) is { } fromCast)
                    return fromCast;
            }
        }
        catch { /* fall through */ }

        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is not null && TryRead(bp.RootElement) is { } fromBp)
                return fromBp;
        }
        catch { /* fall through */ }

        try
        {
            var scenesPath = GetScenesPath(projectId);
            if (File.Exists(scenesPath))
            {
                using var scenesDoc = JsonDocument.Parse(File.ReadAllText(scenesPath));
                if (TryRead(scenesDoc.RootElement) is { } fromScenes)
                    return fromScenes;
            }
        }
        catch { /* fall through */ }

        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetSeed(
        Dictionary<string, JsonElement> seeds,
        string charKey,
        out JsonElement info) =>
        seeds.TryGetValue(charKey, out info);

    private static bool IsVoiceOnly(string key, JsonElement info)
    {
        // Prefer cast seed policy only — do not force voice-only for keys named Narrator
        if (info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("display_name_policy", out var pol))
        {
            return CastKindClassifier.IsVoiceOnlyPolicy(pol.GetString());
        }
        return false;
    }

    /// <summary>
    /// True when the on-screen key is a plural/ensemble cast member (Children, Crowd, …) with no
    /// single portrait identity — exempt from the locked-reference video-gen gate. Same signal as
    /// <see cref="CharacterSummary.IsGroup"/> (<see cref="CastKindClassifier.IsGroup"/>), read from
    /// the cast seed when present and otherwise from the key/display token, so it needs no seed.
    /// </summary>
    private static bool IsGroupSeed(string key, JsonElement? seed)
    {
        string? castKind = null, display = null, desc = null;
        if (seed is { ValueKind: JsonValueKind.Object } s)
        {
            if (s.TryGetProperty("cast_kind", out var ck)) castKind = ck.GetString();
            if (s.TryGetProperty("display_name", out var dn)) display = dn.GetString();
            if (s.TryGetProperty("description", out var d)) desc = d.GetString();
        }
        return CastKindClassifier.IsGroup(key, display, castKind, desc);
    }



    private string ResolveWorkspaceRoot()
    {
        if (!string.IsNullOrWhiteSpace(_opts.WorkspaceRoot))
        {
            try
            {
                var full = Path.GetFullPath(_opts.WorkspaceRoot);
                if (!Directory.Exists(full))
                {
                    Directory.CreateDirectory(full);
                }
                return full;
            }
            catch { /* fallback below */ }
        }

        // Persistent Docker / Railway volume mounts
        if (Directory.Exists("/data"))
        {
            return "/data";
        }
        if (Directory.Exists("/app/data"))
        {
            return "/app/data";
        }

        // host/PageToMovie.Engine → host → repo
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            // Repo root: projects/ + prompts/ (or host/ sibling of projects/)
            if (Directory.Exists(Path.Combine(dir.FullName, "projects")) &&
                (Directory.Exists(Path.Combine(dir.FullName, "prompts")) ||
                 Directory.Exists(Path.Combine(dir.FullName, "host"))))
            {
                return dir.FullName;
            }
            // running from host/PageToMovie.Api/bin/...
            if (dir.Name.Equals("host", StringComparison.OrdinalIgnoreCase) &&
                dir.Parent is not null &&
                Directory.Exists(Path.Combine(dir.Parent.FullName, "projects")))
            {
                return dir.Parent.FullName;
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
