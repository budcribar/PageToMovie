using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Fountain;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

public sealed partial class ProjectStore
{
    public const string ClientMarkerExtension = ".client.json";
    public const string PipelineConfigFileName = StoreLit.PipelineConfigJson;

    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;

    /// <summary>CA1861: avoid allocating split separator arrays on every book-text sample.</summary>
    private static readonly char[] WordSplitChars = { ' ', '\n', '\r', '\t' };

    /// <summary>Repeated path / JSON literals (S1192).</summary>
    private static class StoreLit
    {
        public const string Projects = "projects";
        public const string WorkspaceJson = "workspace.json";
        public const string ProjectJson = "project.json";
        public const string ScenesJson = "scenes.json";
        public const string PipelineConfigJson = "pipeline_config.json";
        public const string BlueprintClipsGrokJson = "blueprint.clips.grok.json";
        public const string Assets = "assets";
        public const string Video = "video";
        public const string Source = "source";
        public const string History = "history";
        public const string Music = "music";
        public const string Scenes = "scenes";
        public const string Clips = "clips";
        public const string Characters = "characters";
        public const string VeoClips = "veo_clips";
        public const string ClipJsonSuffix = ".clip.json";
        public const string TrashDir = ".trash";
        public const string RefPngSuffix = "_ref.png";
        public const string CastSeedsV1 = "cast_seeds.v1";
        public const string CharacterSeedTokens = "character_seed_tokens";
        public const string LocationSeedTokens = "location_seed_tokens";
        public const string WardrobeLockTokens = "wardrobe_lock_tokens";
        public const string GlobalProductionVariables = "global_production_variables";
        public const string SchemaVersion = "schema_version";
        public const string DisplayName = "display_name";
        public const string LocationType = "location_type";
        public const string CharactersOnScreen = "characters_on_screen";
        public const string PrimaryLocationId = "primary_location_id";
        public const string LocationIds = "location_ids";
        public const string VisualPrompt = "visual_prompt";
        public const string VisualLock = "visual_lock";
        public const string DurationSeconds = "duration_seconds";
        public const string IsCredits = "is_credits";
        public const string Setting = "setting";
        public const string Credits = "CREDITS";
        public const string BookSubsteps = "book_substeps";
        public const string VisibilityMode = "visibilityMode";
        public const string VoiceProvider = "voice_provider";
        public const string VoiceProviderVoiceId = "voice_provider_voice_id";
        public const string Title = "title";
        public const string ParentProjectId = "parentProjectId";
        public const string OwnerUserId = "ownerUserId";
        public const string AudioScript = "audio_script";
        public const string Delivery = "delivery";
        public const string Operator = "Operator";
        public const string VoiceLabel = "voice_label";
        public const string VoiceProfile = "voice_profile";
        public const string ImagineVoiceId = "imagine_voice_id";
        public const string IsoDateTime = "yyyy-MM-ddTHH:mm:ss";
    }


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
            if (root[StoreLit.CharacterSeedTokens] is System.Text.Json.Nodes.JsonObject direct)
                seeds = direct;
            else if (root[StoreLit.GlobalProductionVariables] is System.Text.Json.Nodes.JsonObject gpv &&
                     gpv[StoreLit.CharacterSeedTokens] is System.Text.Json.Nodes.JsonObject nested)
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
        var ws = Path.Combine(_workspaceRoot, StoreLit.Projects, StoreLit.WorkspaceJson);
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return null;

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var who = string.IsNullOrWhiteSpace(author) ? StoreLit.Operator : author;
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return null;

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var who = string.IsNullOrWhiteSpace(author) ? StoreLit.Operator : author;
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return Array.Empty<SceneCommitHistoryItem>();

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var commits = await git.GetCommitHistoryAsync(dir, limit).ConfigureAwait(false);
        if (commits.Count == 0) return Array.Empty<SceneCommitHistoryItem>();

        var bpName = StoreLit.BlueprintClipsGrokJson;
        var result = new List<SceneCommitHistoryItem>();

        for (int i = 0; i < commits.Count; i++)
        {
            var c = commits[i];
            var historicalBp = ProjectGitRepositoryService.GetFileContentAtCommit(dir, c.CommitHash, bpName);
            if (string.IsNullOrWhiteSpace(historicalBp)) continue;

            string? parentBp = null;
            if (i + 1 < commits.Count)
            {
                parentBp = ProjectGitRepositoryService.GetFileContentAtCommit(dir, commits[i + 1].CommitHash, bpName);
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
        string? author = null)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(commitHash)) return false;
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return false;

        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is null || !File.Exists(bpPath)) return false;

        var bpName = Path.GetFileName(bpPath);

        var historicalBpStr = ProjectGitRepositoryService.GetFileContentAtCommit(dir, commitHash, bpName);
        if (string.IsNullOrWhiteSpace(historicalBpStr)) return false;

        try
        {
            var currentJson = await File.ReadAllTextAsync(bpPath).ConfigureAwait(false);
            if (!TryApplyHistoricalSceneToBlueprint(currentJson, historicalBpStr, sceneNumber, out var currentRoot)
                || currentRoot is null)
                return false;

            await File.WriteAllTextAsync(bpPath, currentRoot.ToJsonString(JsonOpts)).ConfigureAwait(false);
            InvalidateSceneListCache(projectId);

            var who = string.IsNullOrWhiteSpace(author) ? StoreLit.Operator : author;
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
    public async Task<UncommittedStatusDto> GetProjectUncommittedStatusAsync(string projectId, ProjectGitRepositoryService? gitRepo = null)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return new UncommittedStatusDto();
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return new UncommittedStatusDto();

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
            ApplyCleanGitSummary(dto, head);
            return dto;
        }

        var (_, files) = ProjectGitRepositoryService.GetUncommittedStatus(dir);
        CollectUncommittedSceneClipKeys(files, out var modScenes, out var modClips);

        dto.ModifiedScenes = modScenes.OrderBy(n => n).ToList();
        dto.ModifiedClipKeys = modClips.ToList();
        dto.Summary = dto.ModifiedScenes.Count > 0
            ? $"{dto.ModifiedScenes.Count} scene(s) modified since last save."
            : "Package has uncommitted changes.";
        return dto;
    }

    private static void ApplyCleanGitSummary(UncommittedStatusDto dto, ProjectGitStatus head)
    {
        if (head.Available && !string.IsNullOrWhiteSpace(head.LastCommitHash))
            dto.Summary = "Package up to date with last save.";
        else if (!head.Available)
            dto.Summary = head.SkipReason ?? "Package history not available.";
    }

    private static void CollectUncommittedSceneClipKeys(
        IEnumerable<string> files, out HashSet<int> modScenes, out HashSet<string> modClips)
    {
        modScenes = new HashSet<int>();
        modClips = new HashSet<string>();
        foreach (var f in files)
        {
            var m = CommonRegex.Match(f, @"scene_?(\d+)(?:_clip_?(\d+))?", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var s))
                continue;
            modScenes.Add(s);
            if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var c))
                modClips.Add($"{s}-{c}");
        }
    }

    /// <summary>
    /// Manually commits uncommitted working directory changes.
    /// </summary>
    public async Task<GitCommitInfo?> CommitProjectChangesAsync(
        string projectId, string message, string? author = null, ProjectGitRepositoryService? gitRepo = null,
        bool forceCommit = false)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return null;

        var git = gitRepo ?? new ProjectGitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
        var who = string.IsNullOrWhiteSpace(author) ? StoreLit.Operator : author;
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return Array.Empty<ClipVersionItem>();

        var videoDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Video);
        if (!Directory.Exists(videoDir)) return Array.Empty<ClipVersionItem>();

        var result = new List<ClipVersionItem>();
        var prefix = ClipTakeNaming.SceneClipPrefix(scene, clip);
        var activeMp4 = Path.Combine(videoDir, ClipTakeNaming.CanonicalMp4FileName(scene, clip));
        AddTakeSidecarVersions(result, videoDir, scene, clip);
        AddHistoricalClipVersions(result, videoDir, prefix, activeMp4, scene, clip);
        AddCanonicalAliasIfNoTakes(result, activeMp4, scene, clip);
        await MergeRegisteredClipVersionsAsync(result, projectId, scene, clip, activeMp4).ConfigureAwait(false);
        MarkCurrentTake(result, videoDir, scene, clip);
        // Preserve the pointer-selected object before repairing duplicate/invalid display take
        // numbers. Comparing the pointer after renumbering can mark a different take current.
        AssignUniqueTakeNumbers(result);
        return await Task.FromResult(result.OrderByDescending(x => x.CreatedAtUtc).ToList()).ConfigureAwait(false);
    }

    /// <summary>
    /// One card per take sidecar (provider-hosted or local). A leftover
    /// <c>scene_SS_clip_CC.mp4</c> is not a take and not the player file.
    /// </summary>
    private static void AddTakeSidecarVersions(List<ClipVersionItem> result, string videoDir, int scene, int clip)
    {
        if (!Directory.Exists(videoDir)) return;
        var knownStems = new HashSet<string>(
            result.Select(r => ClipTakeNaming.ClipStem(r.Mp4FileName)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var sidecar in Directory.EnumerateFiles(videoDir, ClipTakeNaming.TakeSidecarSearchPattern(scene, clip)))
        {
            var stem = ClipTakeNaming.ClipStem(Path.GetFileName(sidecar));
            if (string.IsNullOrEmpty(stem) || !knownStems.Add(stem))
                continue;
            var takeMp4 = Path.Combine(videoDir, stem + ".mp4");
            var fi = File.Exists(takeMp4) ? new FileInfo(takeMp4) : new FileInfo(sidecar);
            try
            {
                // A take sidecar used to be read and parsed three times here (take, card, source).
                // Parse once and project all three views from the same JSON element.
                using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
                var root = doc.RootElement;
                var sidecarTake = root.TryGetProperty("take", out var t) && t.TryGetInt32(out var n) ? n : 0;
                var take = ClipTakeNaming.ResolveTakeNumber(stem, sidecarTake);
                var item = CreateClipVersionItem(takeMp4, scene, clip, take, isCurrent: false, fi.LastWriteTimeUtc);
                ApplyClipSidecarJson(item, root);
                item.RelativePath = $"{ClipTakeNaming.AssetsVideoPrefix}/{stem}.mp4";
                var src = ClipProviderSource.Read(root);
                item.SourceUrl = src.SourceUrl ?? item.SourceUrl;
                item.SourceFileId = src.SourceFileId ?? item.SourceFileId;
                item.ProviderLeadInSeconds = src.LeadInSeconds;
                result.Add(item);
            }
            catch
            {
                // Preserve the historical best-effort behavior for a malformed sidecar: the
                // filename still identifies a take card even when its optional metadata is bad.
                var take = ClipTakeNaming.ResolveTakeNumber(stem, 0);
                var item = CreateClipVersionItem(takeMp4, scene, clip, take, isCurrent: false, fi.LastWriteTimeUtc);
                item.RelativePath = $"{ClipTakeNaming.AssetsVideoPrefix}/{stem}.mp4";
                result.Add(item);
            }
        }
    }

    private static int ReadSidecarTakeField(string sidecarPath)
    {
        if (!File.Exists(sidecarPath))
            return 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            if (doc.RootElement.TryGetProperty("take", out var t) && t.TryGetInt32(out var n) && n > 0)
                return n;
        }
        catch { /* best effort */ }
        return 0;
    }

    /// <summary>
    /// Legacy trees with only a leftover alias (no <c>_take_NN</c> sidecars) still
    /// need one card so compare/promote can surface the leftover.
    /// </summary>
    private static void AddCanonicalAliasIfNoTakes(List<ClipVersionItem> result, string activeMp4, int scene, int clip)
    {
        if (result.Any(v => ClipTakeNaming.ParseTakeNumber(v.Mp4FileName) > 0
                            || ClipTakeNaming.IsStableTakeName(v.Mp4FileName)
                            || ClipTakeNaming.IsTimestampedTakeName(v.Mp4FileName)))
            return;
        if (!File.Exists(activeMp4))
            return;
        var fi = new FileInfo(activeMp4);
        var activeSidecar = Path.ChangeExtension(activeMp4, StoreLit.ClipJsonSuffix);
        var take = ClipTakeNaming.ResolveTakeNumber(Path.GetFileName(activeSidecar), ReadSidecarTakeField(activeSidecar));
        if (take <= 0) take = 1;
        var item = ParseClipSidecarOrMeta(activeSidecar, activeMp4, scene, clip, take, isCurrent: true, fi.LastWriteTimeUtc);
        item.RelativePath = ClipTakeNaming.CanonicalRelativePath(scene, clip);
        result.Add(item);
    }

    private static void AssignUniqueTakeNumbers(List<ClipVersionItem> result)
    {
        if (result.Count == 0)
            return;
        var items = result
            .OrderBy(x => x.Take > 0 ? 0 : 1)
            .ThenBy(x => x.Take)
            .ThenBy(x => x.CreatedAtUtc)
            .Select(it => (it.Take, (Action<int>)(n => it.Take = n)))
            .ToList();
        ClipTakeNaming.AssignUniqueTakeNumbers(items);
    }

    private static void MarkCurrentTake(List<ClipVersionItem> result, string videoDir, int scene, int clip)
    {
        foreach (var v in result)
            v.IsCurrent = false;
        if (result.Count == 0)
            return;
        var pointer = ClipSidecarService.ReadCurrentTake(videoDir, scene, clip);
        ClipVersionItem? current = pointer > 0
            ? result.FirstOrDefault(v =>
                ClipTakeNaming.IsStableTakeName(v.Mp4FileName)
                && ClipTakeNaming.ParseTakeNumber(v.Mp4FileName) == pointer)
            : null;
        current ??= pointer > 0 ? result.FirstOrDefault(v => v.Take == pointer) : null;
        current ??= result
            .Where(v => !ClipTakeNaming.IsCanonicalClipName(v.Mp4FileName))
            .OrderByDescending(x => x.Take).ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
        current ??= result.OrderByDescending(x => x.Take).ThenByDescending(x => x.CreatedAtUtc).First();
        current.IsCurrent = true;
    }

    private static void AddHistoricalClipVersions(
        List<ClipVersionItem> result, string videoDir, string prefix, string activeMp4, int scene, int clip)
    {
        var knownStems = new HashSet<string>(
            result.Select(r => ClipTakeNaming.ClipStem(r.Mp4FileName)),
            StringComparer.OrdinalIgnoreCase);
        TryAddHistoricalClipsFromDir(result, videoDir, prefix, activeMp4, scene, clip, knownStems);
        TryAddHistoricalClipsFromDir(
            result, Path.Combine(videoDir, StoreLit.History), prefix, activeMp4, scene, clip, knownStems);
    }

    private static void TryAddHistoricalClipsFromDir(
        List<ClipVersionItem> result, string sDir, string prefix, string activeMp4, int scene, int clip,
        HashSet<string> knownStems)
    {
        if (!Directory.Exists(sDir))
            return;
        foreach (var mp4 in Directory.EnumerateFiles(sDir, $"{prefix}*.mp4"))
            TryAddHistoricalClipVersion(result, sDir, mp4, activeMp4, scene, clip, knownStems);
    }

    private static void TryAddHistoricalClipVersion(
        List<ClipVersionItem> result, string sDir, string mp4, string activeMp4, int scene, int clip,
        HashSet<string> knownStems)
    {
        if (string.Equals(mp4, activeMp4, StringComparison.OrdinalIgnoreCase))
            return;
        var stem = ClipTakeNaming.ClipStem(Path.GetFileName(mp4));
        if (string.IsNullOrEmpty(stem) || !knownStems.Add(stem))
            return;
        var sidecar = ResolveExistingClipSidecar(mp4);
        var take = ClipTakeNaming.ResolveTakeNumber(Path.GetFileName(mp4), ReadSidecarTakeField(sidecar));
        var item = ParseClipSidecarOrMeta(sidecar, mp4, scene, clip, take, isCurrent: false, new FileInfo(mp4).LastWriteTimeUtc);
        if (string.IsNullOrEmpty(item.RelativePath))
            item.RelativePath = HistoricalClipRelativePath(sDir, mp4);
        result.Add(item);
    }

    private static string ResolveExistingClipSidecar(string mp4)
    {
        var sidecar = Path.ChangeExtension(mp4, StoreLit.ClipJsonSuffix);
        return File.Exists(sidecar) ? sidecar : Path.ChangeExtension(mp4, ".meta.json");
    }

    private static string HistoricalClipRelativePath(string sDir, string mp4)
    {
        var file = Path.GetFileName(mp4);
        return Path.GetFileName(sDir).Equals(StoreLit.History, StringComparison.OrdinalIgnoreCase)
            ? $"{ClipTakeNaming.AssetsVideoPrefix}/{StoreLit.History}/{file}"
            : $"{ClipTakeNaming.AssetsVideoPrefix}/{file}";
    }

    private async Task MergeRegisteredClipVersionsAsync(
        List<ClipVersionItem> result, string projectId, int scene, int clip, string activeMp4)
    {
        // Takes that synced to the client and were pruned server-side (client-storage is the primary
        // path — see ServerMediaPruningService) have no bytes left to scan for above, only a
        // MediaRegistryService row. Without this, a clip the UI correctly shows as "on disk" (via
        // ClipOnDisk's marker check) would list zero versions here — "Takes (2)" but an empty compare
        // modal. Merge in registry rows the physical scan didn't already find.
        if (_mediaRegistry is null)
            return;
        var registered = await _mediaRegistry.ListForClipAsync(projectId, scene, clip).ConfigureAwait(false);
        if (registered.Count == 0)
            return;

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
                Take = ClipTakeNaming.ResolveTakeNumber(fileName),
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

    /// <summary>
    /// Promotes a historical clip version/take to be the active clip for that scene.
    /// </summary>
    public async Task<bool> PromoteClipVersionAsync(string projectId, int scene, int clip, string versionId, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId)) return false;
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return false;

        var versions = await GetClipVersionsAsync(projectId, scene, clip).ConfigureAwait(false);
        var target = versions.FirstOrDefault(v => string.Equals(v.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsCurrent) return false;

        var videoDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Video);
        if (!TryPromoteClipVersion(projectId, videoDir, scene, clip, target))
            return false;

        InvalidateSceneListCache(projectId);
        var who = string.IsNullOrWhiteSpace(author) ? StoreLit.Operator : author;
        TriggerAutoGitCommit(projectId, $"Restored clip S{scene:D2}C{clip:D2} to version {target.Mp4FileName}", who);
        return true;
    }

    private bool TryPromoteClipVersion(string projectId, string videoDir, int scene, int clip, ClipVersionItem target)
    {
        var targetMp4Path = ResolveLocalTakeMp4Path(videoDir, target.Mp4FileName);
        var hasLocalTakeMp4 = File.Exists(targetMp4Path);
        if (!hasLocalTakeMp4 && !HasProviderCopy(target))
            return false;

        ClipSidecarService.WriteCurrentTake(videoDir, scene, clip, Math.Max(1, target.Take));
        TryRestorePromotedVisualPrompt(projectId, scene, clip, target.VisualPrompt);
        return true;
    }

    private static string ResolveLocalTakeMp4Path(string videoDir, string fileName)
    {
        var targetMp4Path = Path.Combine(videoDir, fileName);
        return File.Exists(targetMp4Path)
            ? targetMp4Path
            : Path.Combine(videoDir, StoreLit.History, fileName);
    }

    private static bool HasProviderCopy(ClipVersionItem target)
        => !string.IsNullOrWhiteSpace(target.SourceUrl)
            || !string.IsNullOrWhiteSpace(target.SourceFileId);

    private void TryRestorePromotedVisualPrompt(string projectId, int scene, int clip, string visualPrompt)
    {
        if (string.IsNullOrWhiteSpace(visualPrompt))
            return;
        try { UpdateClipVisualPrompt(projectId, scene, clip, visualPrompt); } catch { /* prompt restore is best-effort */ }
    }

    /// <summary>
    /// Archives a leftover bare alias (<c>scene_SS_clip_CC.mp4</c>) into history/ if one
    /// still exists. Does not write or refresh that alias as the player file — the new
    /// take is <c>take_NN</c> via <see cref="ClipSidecarService.PersistGeneratedTakeAsync"/>.
    /// </summary>
    public string ArchiveActiveAndReplaceClipBytesAsync(string projectId, int scene, int clip, byte[] newBytes)
    {
        var dir = GetProjectDir(projectId);
        var videoDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Video);
        Directory.CreateDirectory(videoDir);
        var leftoverAlias = Path.Combine(videoDir, ClipTakeNaming.CanonicalMp4FileName(scene, clip));

        if (File.Exists(leftoverAlias))
        {
            var historyDir = Path.Combine(videoDir, StoreLit.History);
            Directory.CreateDirectory(historyDir);
            var archiveStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var archiveMp4 = Path.Combine(historyDir, $"scene_{scene:D2}_clip_{clip:D2}_{archiveStamp}.mp4");
            try { File.Copy(leftoverAlias, archiveMp4, overwrite: true); } catch { /* leftover archive is best-effort */ }
            var leftoverSidecar = Path.ChangeExtension(leftoverAlias, StoreLit.ClipJsonSuffix);
            if (File.Exists(leftoverSidecar))
            {
                var archiveSidecar = Path.ChangeExtension(archiveMp4, StoreLit.ClipJsonSuffix);
                try { File.Copy(leftoverSidecar, archiveSidecar, overwrite: true); } catch { /* best effort */ }
            }
        }

        _ = newBytes;
        InvalidateSceneListCache(projectId);
        return ClipTakeNaming.TakeMp4FileName(scene, clip, Math.Max(1, ClipSidecarService.ReadCurrentTake(videoDir, scene, clip) + 1));
    }

    private static ClipVersionItem ParseClipSidecarOrMeta(string sidecarPath, string mp4Path, int scene, int clip, int take, bool isCurrent, DateTime lastWriteUtc)
    {
        var item = CreateClipVersionItem(mp4Path, scene, clip, take, isCurrent, lastWriteUtc);

        if (File.Exists(sidecarPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecarPath));
                ApplyClipSidecarJson(item, doc.RootElement);
            }
            catch { /* best effort sidecar parse */ }
        }

        return item;
    }

    private static ClipVersionItem CreateClipVersionItem(
        string mp4Path, int scene, int clip, int take, bool isCurrent, DateTime lastWriteUtc) =>
        new()
        {
            VersionId = Path.GetFileName(mp4Path),
            Scene = scene,
            Clip = clip,
            Take = take,
            IsCurrent = isCurrent,
            CreatedAtUtc = lastWriteUtc,
            Mp4FileName = Path.GetFileName(mp4Path),
        };

    private static void ApplyClipSidecarJson(ClipVersionItem item, JsonElement root)
    {
        item.VisualPrompt = FirstJsonString(root, StoreLit.VisualPrompt, "prompt");
        item.ScriptText = JsonStringOrEmpty(root, "script_text");
        item.Model = JsonStringOrEmpty(root, "model");
        item.Resolution = JsonStringOrEmpty(root, "resolution");
        if (root.TryGetProperty(StoreLit.DurationSeconds, out var d) && d.TryGetDouble(out var dur))
            item.DurationSeconds = dur;
        if (root.TryGetProperty("sha256", out var sha))
            item.Sha256 = sha.GetString() ?? "";
        if (root.TryGetProperty("edited_from_take", out var eft) && eft.TryGetInt32(out var eftVal))
            item.EditedFromTake = eftVal;
        if (root.TryGetProperty("source_file_id", out var sfid))
            item.SourceFileId = sfid.GetString();
        if (root.TryGetProperty("source_url", out var surl) && surl.ValueKind == JsonValueKind.String)
            item.SourceUrl = surl.GetString();
        if (root.TryGetProperty(ClipProviderSource.LeadInProperty, out var lead) && lead.TryGetDouble(out var leadSec))
            item.ProviderLeadInSeconds = leadSec;
        if (root.TryGetProperty("take", out var tk) && tk.TryGetInt32(out var tkNo) && tkNo > 0 && item.Take <= 0)
            item.Take = tkNo;
        if (root.TryGetProperty("source_file_expires_at", out var sfexp) && sfexp.TryGetInt64(out var sfexpVal))
            item.SourceFileExpiresAtUnixSeconds = sfexpVal;
    }

    private static string FirstJsonString(JsonElement root, string primary, string fallback)
    {
        if (root.TryGetProperty(primary, out var vp))
            return vp.GetString() ?? "";
        if (root.TryGetProperty(fallback, out var p))
            return p.GetString() ?? "";
        return "";
    }

    private static string JsonStringOrEmpty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";

    /// <summary>
    /// Soft-deletes a take version by moving its .mp4 and sidecar files into assets/video/.trash/
    /// </summary>
    public async Task<bool> SoftDeleteClipVersionAsync(string projectId, int scene, int clip, string versionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId)) return false;
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return false;

        var videoDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Video);
        var versions = await GetClipVersionsAsync(projectId, scene, clip).ConfigureAwait(false);
        var target = versions.FirstOrDefault(v => string.Equals(v.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsCurrent) return false;

        var targetMp4 = Path.Combine(videoDir, target.Mp4FileName);
        if (!File.Exists(targetMp4))
        {
            targetMp4 = Path.Combine(videoDir, StoreLit.History, target.Mp4FileName);
        }
        if (!File.Exists(targetMp4)) return false;

        var trashDir = Path.Combine(videoDir, StoreLit.TrashDir);
        Directory.CreateDirectory(trashDir);

        var trashMp4 = Path.Combine(trashDir, target.Mp4FileName);
        File.Move(targetMp4, trashMp4, overwrite: true);

        var sidecar = Path.ChangeExtension(targetMp4, StoreLit.ClipJsonSuffix);
        if (File.Exists(sidecar))
        {
            var trashSidecar = Path.Combine(trashDir, Path.GetFileName(sidecar));
            try { File.Move(sidecar, trashSidecar, overwrite: true); } catch { /* sidecar may already be gone */ }
        }

        var meta = Path.ChangeExtension(targetMp4, ".meta.json");
        if (File.Exists(meta))
        {
            var trashMeta = Path.Combine(trashDir, Path.GetFileName(meta));
            try { File.Move(meta, trashMeta, overwrite: true); } catch { /* meta may already be gone */ }
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return Array.Empty<ClipVersionItem>();

        var trashDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Video, StoreLit.TrashDir);
        if (!Directory.Exists(trashDir)) return Array.Empty<ClipVersionItem>();

        var result = new List<ClipVersionItem>();
        var prefix = $"scene_{scene:D2}_clip_{clip:D2}";

        foreach (var mp4 in Directory.EnumerateFiles(trashDir, $"{prefix}*.mp4"))
        {
            var sidecar = Path.ChangeExtension(mp4, StoreLit.ClipJsonSuffix);
            if (!File.Exists(sidecar)) sidecar = Path.ChangeExtension(mp4, ".meta.json");
            var fi = new FileInfo(mp4);
            var item = ParseClipSidecarOrMeta(sidecar, mp4, scene, clip, take: 0, isCurrent: false, fi.LastWriteTimeUtc);
            result.Add(item);
        }

        return result.OrderByDescending(x => x.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// Restores a soft-deleted take version from assets/video/.trash/ back to assets/video/history/
    /// </summary>
    public async Task<bool> RestoreSoftDeletedClipVersionAsync(string projectId, int scene, int clip, string versionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId)) return false;
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return false;

        var videoDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Video);
        var trashDir = Path.Combine(videoDir, StoreLit.TrashDir);
        var trashMp4 = Path.Combine(trashDir, versionId);
        if (!File.Exists(trashMp4)) return false;

        var historyDir = Path.Combine(videoDir, StoreLit.History);
        Directory.CreateDirectory(historyDir);

        var restoredMp4 = Path.Combine(historyDir, versionId);
        File.Move(trashMp4, restoredMp4, overwrite: true);

        var trashSidecar = Path.ChangeExtension(trashMp4, StoreLit.ClipJsonSuffix);
        if (File.Exists(trashSidecar))
        {
            var restoredSidecar = Path.Combine(historyDir, Path.GetFileName(trashSidecar));
            try { File.Move(trashSidecar, restoredSidecar, overwrite: true); } catch { /* sidecar restore is best-effort */ }
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return 0;

        var trashDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Video, StoreLit.TrashDir);
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
            catch { /* skip locked or already-deleted trash files */ }
        }

        InvalidateSceneListCache(projectId);
        return purgedCount;
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        if (!Directory.Exists(dir)) return Array.Empty<MusicVersionItem>();

        var musicDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Music);
        var result = new List<MusicVersionItem>();
        var activeSidecar = Path.Combine(musicDir, $"scene_{scene:D2}.meta.json");
        var hasActiveSidecar = File.Exists(activeSidecar);
        AddMusicSidecarIfPresent(result, activeSidecar, scene, isCurrent: true);
        AddHistoricalMusicSidecars(result, Path.Combine(musicDir, StoreLit.History), scene);

        // Music generated before this take-history feature existed has no sidecar at all — without
        // this, a scene whose audio was clearly generated (HasSceneMusicAsync true) would show zero
        // takes. Bucket any registry rows the sidecars above didn't already cover into one synthetic
        // "legacy" current entry — there is no prior take to compare it against, which is honest:
        // regenerating before this feature existed destroyed whatever came before it.
        if (!hasActiveSidecar && _mediaRegistry is not null)
            await TryAddLegacyMusicVersionAsync(result, projectId, scene).ConfigureAwait(false);

        return result.OrderByDescending(x => x.CreatedAtUtc).ToList();
    }

    private static void AddMusicSidecarIfPresent(
        List<MusicVersionItem> result, string sidecar, int scene, bool isCurrent)
    {
        if (!File.Exists(sidecar))
            return;
        var item = ParseMusicSidecar(sidecar, scene, isCurrent);
        if (item is not null)
            result.Add(item);
    }

    private static void AddHistoricalMusicSidecars(List<MusicVersionItem> result, string historyDir, int scene)
    {
        if (!Directory.Exists(historyDir))
            return;
        foreach (var sidecar in Directory.EnumerateFiles(historyDir, $"scene_{scene:D2}_take_*.meta.json"))
            AddMusicSidecarIfPresent(result, sidecar, scene, isCurrent: false);
    }

    private async Task TryAddLegacyMusicVersionAsync(List<MusicVersionItem> result, string projectId, int scene)
    {
        if (_mediaRegistry is null)
            return;
        var registered = await _mediaRegistry.ListForSceneMusicAsync(projectId, scene).ConfigureAwait(false);
        if (registered.Count == 0)
            return;
        var activeNames = registered
            .Where(r => !r.RelativePath.Contains("/history/", StringComparison.OrdinalIgnoreCase))
            .Select(r => Path.GetFileName(r.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (activeNames.Count == 0)
            return;
        result.Add(new MusicVersionItem
        {
            TakeId = "legacy",
            Scene = scene,
            IsCurrent = true,
            CreatedAtUtc = EarliestRegisteredUtc(registered, activeNames),
            Model = "",
            IsVocal = false,
            Prompt = "(generated before take history was added)",
            SegmentFileNames = activeNames,
            ClientOnly = true,
            RelativePaths = activeNames.Select(n => $"assets/music/{n}").ToList(),
        });
    }

    private static DateTime EarliestRegisteredUtc(
        IReadOnlyList<MediaObjectDto> registered, List<string> activeNames)
    {
        return registered
            .Where(r => activeNames.Contains(Path.GetFileName(r.RelativePath), StringComparer.OrdinalIgnoreCase))
            .Select(ParseRegistryCreatedUtc)
            .DefaultIfEmpty(DateTime.UtcNow)
            .Min();
    }

    private static DateTime ParseRegistryCreatedUtc(MediaObjectDto r) =>
        DateTimeOffset.TryParse(
            r.CreatedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var dto)
            ? dto.UtcDateTime
            : DateTime.UtcNow;

    private static MusicVersionItem? ParseMusicSidecar(string sidecarPath, int scene, bool isCurrent)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var root = doc.RootElement;
            var item = new MusicVersionItem
            {
                TakeId = JsonStringOrEmpty(root, "take_id"),
                Scene = scene,
                IsCurrent = isCurrent,
                Model = JsonStringOrEmpty(root, "model"),
                IsVocal = JsonValueIsTrue(root, "is_vocal"),
                Prompt = JsonStringOrEmpty(root, "prompt"),
                Lyrics = JsonStrOrNull(root, "lyrics"),
                ClientOnly = true,
            };
            ApplyMusicSidecarSegments(item, root, isCurrent);
            item.CreatedAtUtc = ReadMusicSidecarCreatedUtc(root, sidecarPath);
            if (string.IsNullOrWhiteSpace(item.TakeId))
                item.TakeId = Path.GetFileNameWithoutExtension(sidecarPath);
            return item;
        }
        catch
        {
            return null; // best-effort sidecar parse, same tolerance as ParseClipSidecarOrMeta
        }
    }

    private static bool JsonValueIsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static void ApplyMusicSidecarSegments(MusicVersionItem item, JsonElement root, bool isCurrent)
    {
        if (!root.TryGetProperty("segment_file_names", out var segs) || segs.ValueKind != JsonValueKind.Array)
            return;
        foreach (var s in segs.EnumerateArray())
        {
            var name = s.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            item.SegmentFileNames.Add(name);
            item.RelativePaths.Add(isCurrent ? $"assets/music/{name}" : $"assets/music/history/{name}");
        }
    }

    private static DateTime ReadMusicSidecarCreatedUtc(JsonElement root, string sidecarPath)
    {
        if (root.TryGetProperty("created_at_utc", out var ca) &&
            DateTime.TryParse(ca.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        return File.GetLastWriteTimeUtc(sidecarPath);
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
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
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

        var musicDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Music);
        var historyDir = Path.Combine(musicDir, StoreLit.History);
        var activeSidecar = Path.Combine(musicDir, $"scene_{scene:D2}.meta.json");

        if (File.Exists(activeSidecar))
        {
            var current = ParseMusicSidecar(activeSidecar, scene, isCurrent: true);
            if (current is not null && !string.Equals(current.TakeId, "legacy", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(historyDir);
                var archivePath = Path.Combine(historyDir, $"scene_{scene:D2}_take_{current.TakeId}.meta.json");
                try { File.Copy(activeSidecar, archivePath, overwrite: true); } catch { /* archive is best-effort */ }
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

        var musicDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Music);
        var historySidecar = Path.Combine(musicDir, StoreLit.History, $"scene_{scene:D2}_take_{takeId}.meta.json");
        if (!File.Exists(historySidecar)) return false;

        var trashDir = Path.Combine(musicDir, StoreLit.TrashDir);
        Directory.CreateDirectory(trashDir);
        var trashSidecar = Path.Combine(trashDir, $"scene_{scene:D2}_take_{takeId}.meta.json");
        File.Move(historySidecar, trashSidecar, overwrite: true);

        InvalidateSceneListCache(projectId);
        return true;
    }

    /// <summary>Soft-deleted audio takes for one scene — mirrors <see cref="GetTrashClipVersionsAsync"/>.</summary>
    public async Task<IReadOnlyList<MusicVersionItem>> GetTrashMusicVersionsAsync(string projectId, int scene)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Array.Empty<MusicVersionItem>();
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        var trashDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Music, StoreLit.TrashDir);
        if (!Directory.Exists(trashDir)) return Array.Empty<MusicVersionItem>();

        var result = new List<MusicVersionItem>();
        foreach (var sidecar in Directory.EnumerateFiles(trashDir, $"scene_{scene:D2}_take_*.meta.json"))
        {
            var item = ParseMusicSidecar(sidecar, scene, isCurrent: false);
            if (item is not null) result.Add(item);
        }
        return result.OrderByDescending(x => x.CreatedAtUtc).ToList();
    }

    /// <summary>Restores a soft-deleted audio take's sidecar back to assets/music/history/ — mirrors
    /// <see cref="RestoreSoftDeletedClipVersionAsync"/>.</summary>
    public async Task<bool> RestoreSoftDeletedMusicVersionAsync(string projectId, int scene, string takeId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(takeId)) return false;
        var dir = await GetProjectDirAsync(projectId).ConfigureAwait(false);
        var musicDir = Path.Combine(dir, StoreLit.Assets, StoreLit.Music);
        var trashSidecar = Path.Combine(musicDir, StoreLit.TrashDir, $"scene_{scene:D2}_take_{takeId}.meta.json");
        if (!File.Exists(trashSidecar)) return false;

        var historyDir = Path.Combine(musicDir, StoreLit.History);
        Directory.CreateDirectory(historyDir);
        var restoredSidecar = Path.Combine(historyDir, Path.GetFileName(trashSidecar));
        File.Move(trashSidecar, restoredSidecar, overwrite: true);

        InvalidateSceneListCache(projectId);
        return true;
    }

    private static List<string> CompareSceneInBlueprints(string currentBpJson, string? parentBpJson, int sceneNumber)
    {
        var changes = new List<string>();
        try
        {
            var curRoot = System.Text.Json.Nodes.JsonNode.Parse(currentBpJson) as System.Text.Json.Nodes.JsonObject;
            if (curRoot is null) return changes;
            var curScenes = curRoot[StoreLit.Scenes] as System.Text.Json.Nodes.JsonArray;
            if (curScenes is null) return changes;

            var curScene = FindSceneObjectInArray(curScenes, sceneNumber);
            if (curScene is null) return changes;

            if (string.IsNullOrWhiteSpace(parentBpJson))
            {
                changes.Add("Scene created");
                return changes;
            }

            var parRoot = System.Text.Json.Nodes.JsonNode.Parse(parentBpJson) as System.Text.Json.Nodes.JsonObject;
            var parScenes = parRoot?[StoreLit.Scenes] as System.Text.Json.Nodes.JsonArray;
            var parScene = parScenes is not null ? FindSceneObjectInArray(parScenes, sceneNumber) : null;
            if (parScene is null)
            {
                changes.Add("Scene created");
                return changes;
            }

            var curHeading = curScene["heading"]?.ToString() ?? "";
            var parHeading = parScene["heading"]?.ToString() ?? "";
            if (!string.Equals(curHeading, parHeading, StringComparison.OrdinalIgnoreCase))
                changes.Add($"Heading updated: \"{curHeading}\"");

            DiffSceneClipFields(changes, curScene, parScene);
        }
        catch { /* best effort diff */ }

        return changes;
    }

    private static System.Text.Json.Nodes.JsonObject? FindSceneObjectInArray(
        System.Text.Json.Nodes.JsonArray scenes, int sceneNumber)
    {
        foreach (var s in scenes)
        {
            if (s is System.Text.Json.Nodes.JsonObject sObj && ReadJsonNodeInt(sObj[JsonKeys.SceneNumber]) == sceneNumber)
                return sObj;
        }
        return null;
    }

    private static int FindSceneIndexInArray(System.Text.Json.Nodes.JsonArray scenes, int sceneNumber)
    {
        for (var i = 0; i < scenes.Count; i++)
        {
            if (scenes[i] is System.Text.Json.Nodes.JsonObject cObj &&
                ReadJsonNodeInt(cObj[JsonKeys.SceneNumber]) == sceneNumber)
                return i;
        }
        return -1;
    }

    private static void ReplaceOrAddScene(
        System.Text.Json.Nodes.JsonArray scenes,
        int sceneNumber,
        System.Text.Json.Nodes.JsonObject node)
    {
        var i = FindSceneIndexInArray(scenes, sceneNumber);
        if (i >= 0)
            scenes[i] = node;
        else
            scenes.Add(node);
    }

    private static bool TryApplyHistoricalSceneToBlueprint(
        string currentJson,
        string historicalJson,
        int sceneNumber,
        out System.Text.Json.Nodes.JsonObject? currentRoot)
    {
        currentRoot = System.Text.Json.Nodes.JsonNode.Parse(currentJson) as System.Text.Json.Nodes.JsonObject;
        var historicalRoot = System.Text.Json.Nodes.JsonNode.Parse(historicalJson) as System.Text.Json.Nodes.JsonObject;
        if (currentRoot is null || historicalRoot is null)
            return false;

        var currentScenes = currentRoot[StoreLit.Scenes] as System.Text.Json.Nodes.JsonArray;
        var historicalScenes = historicalRoot[StoreLit.Scenes] as System.Text.Json.Nodes.JsonArray;
        if (currentScenes is null || historicalScenes is null)
            return false;

        var hist = FindSceneObjectInArray(historicalScenes, sceneNumber);
        if (hist?.DeepClone() is not System.Text.Json.Nodes.JsonObject clone)
            return false;

        ReplaceOrAddScene(currentScenes, sceneNumber, clone);
        return true;
    }

    private static void DiffSceneClipFields(
        List<string> changes,
        System.Text.Json.Nodes.JsonObject curScene,
        System.Text.Json.Nodes.JsonObject parScene)
    {
        var curClips = curScene[StoreLit.VeoClips] as System.Text.Json.Nodes.JsonArray ?? curScene[StoreLit.Clips] as System.Text.Json.Nodes.JsonArray;
        var parClips = parScene[StoreLit.VeoClips] as System.Text.Json.Nodes.JsonArray ?? parScene[StoreLit.Clips] as System.Text.Json.Nodes.JsonArray;

        var curClipDict = (curClips ?? new System.Text.Json.Nodes.JsonArray())
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .ToDictionary(c => ClipKeying.ClipNumber(c));
        var parClipDict = (parClips ?? new System.Text.Json.Nodes.JsonArray())
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .ToDictionary(c => ClipKeying.ClipNumber(c));

        foreach (var (cNum, curC) in curClipDict)
            DiffOneSceneClip(changes, cNum, curC, parClipDict);

        foreach (var (cNum, _) in parClipDict)
        {
            if (!curClipDict.ContainsKey(cNum))
                changes.Add($"Clip {cNum} removed");
        }
    }

    private static void DiffOneSceneClip(
        List<string> changes,
        int cNum,
        System.Text.Json.Nodes.JsonObject curC,
        Dictionary<int, System.Text.Json.Nodes.JsonObject> parClipDict)
    {
        if (!parClipDict.TryGetValue(cNum, out var parC))
        {
            changes.Add($"Clip {cNum} added");
            return;
        }

        var curPrompt = curC[StoreLit.VisualPrompt]?.ToString() ?? "";
        var parPrompt = parC[StoreLit.VisualPrompt]?.ToString() ?? "";
        if (!string.Equals(curPrompt, parPrompt, StringComparison.Ordinal))
            changes.Add($"Clip {cNum} prompt modified");

        var curAudio = curC[StoreLit.AudioScript]?.ToString() ?? curC[JsonKeys.Dialogue]?.ToString() ?? "";
        var parAudio = parC[StoreLit.AudioScript]?.ToString() ?? parC[JsonKeys.Dialogue]?.ToString() ?? "";
        if (!string.Equals(curAudio, parAudio, StringComparison.Ordinal))
            changes.Add($"Clip {cNum} dialogue modified");

        var curDur = curC[StoreLit.DurationSeconds]?.ToString() ?? "";
        var parDur = parC[StoreLit.DurationSeconds]?.ToString() ?? "";
        if (!string.Equals(curDur, parDur, StringComparison.Ordinal))
            changes.Add($"Clip {cNum} duration changed to {curDur}s");
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
            var projectsDir = Path.Combine(WorkspaceRoot, StoreLit.Projects);
            if (!Directory.Exists(projectsDir))
                return "";
            foreach (var dir in Directory.GetDirectories(projectsDir)
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, StoreLit.WorkspaceJson, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(Path.Combine(dir, StoreLit.ProjectJson)))
                    return name;
                var childHit = Directory.GetDirectories(dir)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(child => File.Exists(Path.Combine(child, StoreLit.ProjectJson)));
                if (childHit is not null)
                    return $"{name}/{Path.GetFileName(childHit)}";
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
            return File.Exists(Path.Combine(dir, StoreLit.ProjectJson));
        }
        catch { return false; }
    }

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct = default) =>
        _readCache.GetOrBuildProjectsAsync(ListProjectsCoreAsync, ct);

    private async Task<IReadOnlyList<ProjectInfo>> ListProjectsCoreAsync(CancellationToken ct)
    {
        var projectsDir = Path.Combine(WorkspaceRoot, StoreLit.Projects);
        if (!Directory.Exists(projectsDir))
            return Array.Empty<ProjectInfo>();

        var list = new List<ProjectInfo>();
        // Flat: projects/{id}/project.json  OR nested: projects/{user}/{slug}/project.json
        foreach (var dir in Directory.GetDirectories(projectsDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (string.Equals(name, StoreLit.WorkspaceJson, StringComparison.OrdinalIgnoreCase))
                continue;

            var metaPath = Path.Combine(dir, StoreLit.ProjectJson);
            if (File.Exists(metaPath))
            {
                await TryAddProjectFromDirAsync(list, dir, name, ct).ConfigureAwait(false);
                continue;
            }

            await AddNamespacedProjectsAsync(list, dir, name, ct).ConfigureAwait(false);
        }
        return list.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task TryAddProjectFromDirAsync(
        List<ProjectInfo> list, string dir, string idOverride, CancellationToken ct)
    {
        var info = await ReadProjectInfoFromDirAsync(dir, idOverride, ct).ConfigureAwait(false);
        if (info is not null)
            list.Add(info);
    }

    private static async Task AddNamespacedProjectsAsync(
        List<ProjectInfo> list, string ownerDir, string ownerName, CancellationToken ct)
    {
        foreach (var child in Directory.GetDirectories(ownerDir))
        {
            ct.ThrowIfCancellationRequested();
            var slug = Path.GetFileName(child);
            if (!File.Exists(Path.Combine(child, StoreLit.ProjectJson)))
                continue;
            await TryAddProjectFromDirAsync(list, child, $"{ownerName}/{slug}", ct).ConfigureAwait(false);
        }
    }

    private static async Task<ProjectInfo?> ReadProjectInfoFromDirAsync(
        string dir, string idOverride, CancellationToken ct)
    {
        var metaPath = Path.Combine(dir, StoreLit.ProjectJson);
        if (!File.Exists(metaPath))
            return null;
        string? title = null;
        string? label = null;
        string? ownerUserId = null;
        string? parentProjectId = null;
        string? visibilityMode = null;
        string? studioPath = null;
        try
        {
            await using var stream = File.OpenRead(metaPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                // Embedded project.json "id" is ignored — folder path id always wins.
                ApplyProjectInfoProperty(p, ref title, ref label, ref ownerUserId,
                    ref parentProjectId, ref visibilityMode, ref studioPath);
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
            VisibilityMode = ProjectVisibilityExtensions.ParseProjectVisibility(visibilityMode),
            StudioPath = ProjectStudioPaths.Normalize(studioPath),
        };
    }

    private static void ApplyProjectInfoProperty(
        JsonProperty p,
        ref string? title,
        ref string? label,
        ref string? ownerUserId,
        ref string? parentProjectId,
        ref string? visibilityMode,
        ref string? studioPath)
    {
        if (string.Equals(p.Name, StoreLit.Title, StringComparison.OrdinalIgnoreCase))
            title = p.Value.GetString();
        else if (string.Equals(p.Name, "label", StringComparison.OrdinalIgnoreCase))
            label = p.Value.GetString();
        else if (string.Equals(p.Name, StoreLit.ParentProjectId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Name, "parent_project_id", StringComparison.OrdinalIgnoreCase))
            parentProjectId = p.Value.GetString();
        else if (string.Equals(p.Name, StoreLit.VisibilityMode, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Name, "visibility_mode", StringComparison.OrdinalIgnoreCase))
            visibilityMode = p.Value.GetString();
        else if (string.Equals(p.Name, StoreLit.OwnerUserId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Name, "owner_user_id", StringComparison.OrdinalIgnoreCase))
            ownerUserId = p.Value.GetString();
        else if (string.Equals(p.Name, "studioPath", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Name, "studio_path", StringComparison.OrdinalIgnoreCase))
            studioPath = p.Value.GetString();
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
        var wsPath = Path.Combine(WorkspaceRoot, StoreLit.Projects, StoreLit.WorkspaceJson);
        if (Path.GetDirectoryName(wsPath) is { } dir)
            Directory.CreateDirectory(dir);
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
        StudioPath studioPath = StudioPath.Full)
    {
        var raw = (idOrTitle ?? "").Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("Project name required");

        var slug = SanitizeProjectId(raw);
        if (slug.Length == 0)
            throw new InvalidOperationException("Project name has no usable characters");

        var (owner, id, dir) = ResolveCreateProjectIdentity(slug, ownerUserId);
        if (Directory.Exists(dir))
        {
            var existing = await TryReuseExistingCreateDirAsync(dir, id, ownerUserId, ct).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, StoreLit.Source));
        Directory.CreateDirectory(Path.Combine(dir, StoreLit.Assets, StoreLit.Characters));
        Directory.CreateDirectory(Path.Combine(dir, StoreLit.Assets, StoreLit.Scenes));
        Directory.CreateDirectory(Path.Combine(dir, StoreLit.Assets, StoreLit.Video));

        await WriteNewProjectMetaAsync(dir, id, raw, title, owner, ownerUserId, studioPath, ct).ConfigureAwait(false);
        await TryInitProjectGitAsync(dir, owner, ct).ConfigureAwait(false);

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

    private (string? Owner, string Id, string Dir) ResolveCreateProjectIdentity(string slug, string? ownerUserId)
    {
        var owner = string.IsNullOrWhiteSpace(ownerUserId) ? null : SanitizeUserSegment(ownerUserId);
        var id = string.IsNullOrEmpty(owner) ? slug : $"{owner}/{slug}";
        var dir = string.IsNullOrEmpty(owner)
            ? Path.Combine(WorkspaceRoot, StoreLit.Projects, slug)
            : Path.Combine(WorkspaceRoot, StoreLit.Projects, owner, slug);
        return (owner, id, dir);
    }

    private async Task<ProjectInfo?> TryReuseExistingCreateDirAsync(
        string dir, string id, string? ownerUserId, CancellationToken ct)
    {
        var metaFile = Path.Combine(dir, StoreLit.ProjectJson);
        if (File.Exists(metaFile))
        {
            try
            {
                var existing = await GetProjectAsync(id, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    await MaybeStampCreateOwnerAsync(metaFile, existing, ownerUserId, ct).ConfigureAwait(false);
                    return await ActivateAsync(existing.Id, ct).ConfigureAwait(false);
                }
            }
            catch { /* fall through to clean up leftover folder */ }
        }
        try { Directory.Delete(dir, recursive: true); } catch { /* non-fatal */ }
        return null;
    }

    private async Task MaybeStampCreateOwnerAsync(
        string metaFile, ProjectInfo existing, string? ownerUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(existing.OwnerUserId) && !string.IsNullOrWhiteSpace(ownerUserId))
        {
            var metaExisting = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                await File.ReadAllTextAsync(metaFile, ct).ConfigureAwait(false), JsonOpts)
                ?? new Dictionary<string, object?>();
            metaExisting[StoreLit.OwnerUserId] = ownerUserId.Trim();
            await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(metaExisting, JsonOpts) + "\n", ct).ConfigureAwait(false);
            InvalidateReadCaches(null);
        }
    }

    private static async Task WriteNewProjectMetaAsync(
        string dir, string id, string raw, string? title, string? owner, string? ownerUserId,
        StudioPath studioPath, CancellationToken ct)
    {
        var displayTitle = string.IsNullOrWhiteSpace(title) ? raw : title.Trim();
        var meta = new Dictionary<string, object?>
        {
            ["id"] = id,
            [StoreLit.Title] = displayTitle,
            ["blueprint_file"] = StoreLit.BlueprintClipsGrokJson,
            ["scenes_file"] = StoreLit.ScenesJson,
            ["config_file"] = StoreLit.PipelineConfigJson,
            ["state_file"] = "pipeline_state.json",
            [JsonKeys.Description] = "",
            [StoreLit.OwnerUserId] = string.IsNullOrWhiteSpace(ownerUserId)
                ? owner
                : ownerUserId.Trim(),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["studioPath"] = ProjectStudioPaths.ToSerializedString(studioPath),
            // Format version for export/import converters (ProjectMigrationService).
            [StoreLit.SchemaVersion] = ProjectFormatVersions.ProjectSchemaVersion,
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, StoreLit.ProjectJson),
            JsonSerializer.Serialize(meta, JsonOpts) + "\n",
            ct).ConfigureAwait(false);
    }

    private static async Task TryInitProjectGitAsync(string dir, string? owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
        "vision_provider", "audio_provider", StoreLit.VoiceProvider,
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
        foreach (var sib in OwnerSiblingProjects(all, newProjectId, ownerUserId, ActiveProjectId))
        {
            if (await TrySeedModelConfigFromSiblingAsync(newProjectId, sib.Id, ct).ConfigureAwait(false))
                return;
        }
    }

    private static IEnumerable<ProjectInfo> OwnerSiblingProjects(
        IReadOnlyList<ProjectInfo> all, string newProjectId, string ownerUserId, string activeProjectId) =>
        all.Where(p =>
                !string.Equals(p.Id, newProjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.OwnerUserId, ownerUserId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => string.Equals(p.Id, activeProjectId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase);

    private async Task<bool> TrySeedModelConfigFromSiblingAsync(
        string newProjectId, string siblingId, CancellationToken ct)
    {
        Dictionary<string, JsonElement> src;
        try { src = await GetConfigAsync(siblingId, ct).ConfigureAwait(false); }
        catch { return false; }
        if (src.Count == 0)
            return false;
        if (ProjectModelSelection.TryGet(src, ProjectModelSelection.PlanningConfigKey, ProjectModelSelection.ChatConfigKey)
            is not { Length: > 0 })
            return false;

        var seed = CopyModelConfigSeed(src);
        if (seed.Count == 0)
            return false;

        var path = ConfigPath(newProjectId);
        var json = JsonSerializer.Serialize(seed, JsonDefaults.Indented);
        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
        InvalidateReadCaches(newProjectId);
        return true;
    }

    private static Dictionary<string, object?> CopyModelConfigSeed(Dictionary<string, JsonElement> src)
    {
        var seed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ModelConfigSeedKeys)
        {
            if (!src.TryGetValue(key, out var el))
                continue;
            if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                continue;
            seed[key] = el.Deserialize<object>();
        }
        return seed;
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
        var dir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var metaPath = Path.Combine(dir, StoreLit.ProjectJson);
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
        if (meta.TryGetValue(StoreLit.OwnerUserId, out var existing) &&
            existing is string s &&
            string.Equals(s.Trim(), want, StringComparison.OrdinalIgnoreCase))
            return;

        meta[StoreLit.OwnerUserId] = want;
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
        EnsureForkAllowed(source, newOwnerUserId, isInvite);

        // Idempotent per (source, user): if this owner already has a fork of this source, reopen it
        // rather than creating a duplicate. Otherwise each Easy Start "read in my voice" pick piled up
        // a new fork (Buster-fork-xxxx, Buster-fork-yyyy, …).
        var existingFork = FindExistingFork(
            await ListProjectsAsync(ct).ConfigureAwait(false),
            source.Id,
            SanitizeUserSegment(newOwnerUserId));
        if (existingFork is not null)
            return existingFork;

        var (newId, newDir) = CreateForkDirectory(source, newOwnerUserId);
        CopyForkFiles(source.Path, newDir, ct);
        await RewriteForkProjectMetaAsync(newDir, newId, source, newOwnerUserId, ct).ConfigureAwait(false);
        await TryCommitForkGitAsync(newDir, source.Path, newOwnerUserId).ConfigureAwait(false);

        InvalidateReadCaches(null);
        return await RequireProjectAsync(newId, ct).ConfigureAwait(false);
    }

    private static void EnsureForkAllowed(ProjectInfo source, string newOwnerUserId, bool isInvite)
    {
        if (string.IsNullOrWhiteSpace(newOwnerUserId))
            throw new InvalidOperationException("newOwnerUserId required");

        // Allow forking if project is Open/forkable (or unowned/legacy), requested via explicit
        // invite, or if caller is owner/admin. Plain Public is READ-ONLY: viewable, not forkable.
        var isOwnerOrLegacy = string.IsNullOrWhiteSpace(source.OwnerUserId)
            || string.Equals(source.OwnerUserId, newOwnerUserId, StringComparison.OrdinalIgnoreCase);
        var isForkable = source.VisibilityMode == ProjectVisibility.Open;
        if (!isInvite && !isOwnerOrLegacy && !isForkable)
        {
            throw new InvalidOperationException($"Forking disabled for this project (Visibility mode: {source.VisibilityMode}). Only 'Open' (Public Forkable) projects can be forked by community members.");
        }
    }

    private static ProjectInfo? FindExistingFork(
        IReadOnlyList<ProjectInfo> projects, string sourceId, string forkOwnerSeg) =>
        projects.FirstOrDefault(p => IsExistingForkOf(p, sourceId, forkOwnerSeg));

    private static bool IsExistingForkOf(ProjectInfo p, string sourceId, string forkOwnerSeg)
    {
        if (string.IsNullOrWhiteSpace(p.ParentProjectId))
            return false;
        if (!string.Equals(p.ParentProjectId, sourceId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!(p.Id ?? "").Contains('/'))
            return false;
        return string.Equals((p.Id ?? "").Split('/')[0], forkOwnerSeg, StringComparison.OrdinalIgnoreCase);
    }

    private (string NewId, string NewDir) CreateForkDirectory(ProjectInfo source, string newOwnerUserId)
    {
        var baseName = source.Title ?? source.Id.Split('/').LastOrDefault() ?? source.Id;
        var slug = SanitizeProjectId($"{baseName}-fork-{Guid.NewGuid().ToString("N")[..6]}");
        if (slug.Length == 0)
            throw new InvalidOperationException("Could not derive a fork id");

        var ownerSeg = SanitizeUserSegment(newOwnerUserId);
        var newId = string.IsNullOrEmpty(ownerSeg) ? slug : $"{ownerSeg}/{slug}";
        var newDir = ConfineToProjectsRoot(
            string.IsNullOrEmpty(ownerSeg)
                ? Path.Combine(WorkspaceRoot, StoreLit.Projects, slug)
                : Path.Combine(WorkspaceRoot, StoreLit.Projects, ownerSeg, slug),
            newId);
        if (Directory.Exists(newDir))
            throw new InvalidOperationException($"Project already exists: {newId}");

        Directory.CreateDirectory(newDir);
        return (newId, newDir);
    }

    private static void CopyForkFiles(string sourcePath, string newDir, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (ForkSkipExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            // Media is referenced, not copied: the .clip.json sidecars carry the provider source_url
            // (fetchable by anyone) and file_id (account-scoped). Skip what belongs to the SOURCE
            // owner only — .client.json markers describe their local folder, _extend_src_*.json
            // hold file_ids minted with their key; the fork re-mints its own on first extend.
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(ClientMarkerExtension, StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("_extend_src_", StringComparison.OrdinalIgnoreCase))
                continue;
            var rel = Path.GetRelativePath(sourcePath, file);
            if (rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, 2)[0] == ".git")
                continue; // history is adopted via fetch in TryCommitForkGitAsync, not raw file copy

            var destPath = Path.Combine(newDir, rel);
            if (Path.GetDirectoryName(destPath) is { } dir)
                Directory.CreateDirectory(dir);
            File.Copy(file, destPath, overwrite: true);
        }
    }

    private static async Task RewriteForkProjectMetaAsync(
        string newDir, string newId, ProjectInfo source, string newOwnerUserId, CancellationToken ct)
    {
        // Rewrite project.json for the new owner/id/parent link rather than keeping the source's copy.
        var metaPath = Path.Combine(newDir, StoreLit.ProjectJson);
        Dictionary<string, object?> meta;
        try
        {
            meta = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                       await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false), JsonOpts)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            meta = new Dictionary<string, object?>();
        }
        meta["id"] = newId;
        meta[StoreLit.Title] = $"{source.Title ?? source.Id} (fork)";
        meta[StoreLit.OwnerUserId] = newOwnerUserId.Trim();
        meta[StoreLit.ParentProjectId] = source.Id;
        // A fork is the user's private working copy — don't inherit the source's "Open" visibility,
        // or every fork would show up as its own pickable "story" in the forkable list.
        meta[StoreLit.VisibilityMode] = ProjectVisibility.Private.ToString();
        meta["createdAt"] = DateTimeOffset.UtcNow.ToString("o");
        await File.WriteAllTextAsync(
            metaPath, JsonSerializer.Serialize(meta, JsonOpts) + "\n", ct).ConfigureAwait(false);
    }

    private static async Task TryCommitForkGitAsync(string newDir, string sourceDir, string newOwnerUserId)
    {
        try
        {
            ProjectGitRepositoryService.EnsureRepositoryAt(newDir);
            var git = new ProjectGitRepositoryService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);
            // Adopt the parent's history first: "Sync origin" is a real 3-way merge and needs a
            // shared merge base — a fresh unrelated repo made every later sync a wall of conflicts.
            ProjectGitRepositoryService.AdoptParentHistory(newDir, sourceDir);
            await git.CommitProjectStateAsync(newDir, newOwnerUserId, "Initial fork state").ConfigureAwait(false);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Update project visibility mode (Private, Public, or Unlisted) in project.json.
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
        var metaPath = Path.Combine(proj.Path, StoreLit.ProjectJson);
        var meta = await ReadMetaOrEmptyAsync(metaPath, ct).ConfigureAwait(false);

        meta["id"] = proj.Id;
        meta[StoreLit.Title] = title;
        meta["label"] = title;
        if (!string.IsNullOrWhiteSpace(proj.OwnerUserId)) meta[StoreLit.OwnerUserId] = proj.OwnerUserId;
        if (!string.IsNullOrWhiteSpace(proj.ParentProjectId)) meta[StoreLit.ParentProjectId] = proj.ParentProjectId;
        meta[StoreLit.VisibilityMode] = proj.VisibilityMode.ToString();

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOpts) + "\n", ct).ConfigureAwait(false);
        InvalidateReadCaches(null);

        proj.Title = title;
        proj.Label = title;
        TriggerAutoGitCommit(projectId, "Rename project");
        return proj;
    }


    public async Task<ProjectInfo> SetProjectVisibilityModeAsync(
        string projectId,
        ProjectVisibility visibilityMode,
        CancellationToken ct = default)
    {
        var proj = await RequireProjectAsync(projectId, ct).ConfigureAwait(false);

        var metaPath = Path.Combine(proj.Path, StoreLit.ProjectJson);
        var meta = await ReadMetaOrEmptyAsync(metaPath, ct).ConfigureAwait(false);

        meta[StoreLit.VisibilityMode] = visibilityMode.ToString();
        meta["id"] = proj.Id;
        if (!string.IsNullOrWhiteSpace(proj.Title)) meta[StoreLit.Title] = proj.Title;
        if (!string.IsNullOrWhiteSpace(proj.OwnerUserId)) meta[StoreLit.OwnerUserId] = proj.OwnerUserId;
        if (!string.IsNullOrWhiteSpace(proj.ParentProjectId)) meta[StoreLit.ParentProjectId] = proj.ParentProjectId;

        var updatedJson = JsonSerializer.Serialize(meta, JsonOpts) + "\n";
        await File.WriteAllTextAsync(metaPath, updatedJson, ct).ConfigureAwait(false);

        proj.VisibilityMode = visibilityMode;
        InvalidateReadCaches(null);
        TriggerAutoGitCommit(projectId, "Update project visibility");
        return proj;
    }

    public Task<ProjectInfo> SetProjectVisibilityModeAsync(
        string projectId,
        string visibilityMode,
        CancellationToken ct = default) =>
        SetProjectVisibilityModeAsync(projectId, ProjectVisibilityExtensions.ParseProjectVisibility(visibilityMode), ct);

    /// <summary>
    /// Persist product path (full vs simple-voice) on project.json.
    /// </summary>
    public async Task<ProjectInfo> SetProjectStudioPathAsync(
        string projectId,
        StudioPath studioPath,
        CancellationToken ct = default)
    {
        var path = ProjectStudioPaths.Normalize(studioPath);
        var proj = await RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        var metaPath = Path.Combine(proj.Path, StoreLit.ProjectJson);
        var meta = await ReadMetaOrEmptyAsync(metaPath, ct).ConfigureAwait(false);

        meta["studioPath"] = ProjectStudioPaths.ToSerializedString(path);
        meta["id"] = proj.Id;
        if (!string.IsNullOrWhiteSpace(proj.Title)) meta[StoreLit.Title] = proj.Title;
        if (!string.IsNullOrWhiteSpace(proj.OwnerUserId)) meta[StoreLit.OwnerUserId] = proj.OwnerUserId;
        if (!string.IsNullOrWhiteSpace(proj.ParentProjectId)) meta[StoreLit.ParentProjectId] = proj.ParentProjectId;
        meta[StoreLit.VisibilityMode] = proj.VisibilityMode.ToString();

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
            else if ((char.IsWhiteSpace(ch) || ch is '.' or '/') && sb.Length > 0 && sb[^1] != '_')
            {
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

        var dir = ConfineToProjectsRoot(ResolveProjectDirPath(id), projectId);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"Unknown project: {id}");
        var projectsRoot = Path.GetFullPath(Path.Combine(WorkspaceRoot, StoreLit.Projects));

        // Best-effort delete (files may be locked by a running job). Git writes loose-object files
        // read-only by design (immutable objects) — on Windows, Directory.Delete throws
        // UnauthorizedAccessException for a read-only file regardless of how long you wait, so a plain
        // retry loop never helps a project with any git history (every project has at least the
        // "Initial project state" commit). Clear the attribute recursively first, then retry briefly
        // for the separate, genuinely transient case of a file still open from a running job.
        await DeleteProjectDirectoryWithRetryAsync(dir, id, ct).ConfigureAwait(false);

        if (string.Equals(_activeProjectId, id, StringComparison.OrdinalIgnoreCase))
            _activeProjectId = "";

        // Update workspace.json active pointer
        var wsPath = Path.Combine(WorkspaceRoot, StoreLit.Projects, StoreLit.WorkspaceJson);
        await UpdateWorkspaceAfterProjectDeleteAsync(id, projectsRoot, wsPath, ct).ConfigureAwait(false);

        InvalidateReadCaches(null);
        InvalidateReadCaches(id);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task DeleteProjectDirectoryWithRetryAsync(string dir, string id, CancellationToken ct)
    {
        ClearReadOnlyRecursive(dir);
        var attempts = 0;
        while (true)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
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
    }

    private async Task UpdateWorkspaceAfterProjectDeleteAsync(
        string id, string projectsRoot, string wsPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(wsPath))
                return;
            try
            {
                var state = JsonSerializer.Deserialize<WorkspaceState>(
                    await File.ReadAllTextAsync(wsPath, ct).ConfigureAwait(false), JsonOpts);
                if (!string.Equals(state?.ActiveProject, id, StringComparison.OrdinalIgnoreCase))
                    return;
                var nextActive = PickNextActiveProject(projectsRoot, id);
                await File.WriteAllTextAsync(
                    wsPath,
                    JsonSerializer.Serialize(
                        new WorkspaceState { ActiveProject = nextActive ?? "" },
                        JsonOpts) + "\n",
                    ct).ConfigureAwait(false);
                _activeProjectId = nextActive ?? "";
            }
            catch { /* leave workspace as-is if unreadable */ }
        }
        catch { /* ignore workspace update failures */ }
    }

    private static string? PickNextActiveProject(string projectsRoot, string deletedId)
    {
        if (!Directory.Exists(projectsRoot))
            return null;
        return Directory.GetDirectories(projectsRoot)
            .Select(Path.GetFileName)
            .FirstOrDefault(n =>
                !string.IsNullOrEmpty(n) &&
                !string.Equals(n, deletedId, StringComparison.OrdinalIgnoreCase));
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
        var dir = ConfineToProjectsRoot(ResolveProjectDirPath(id), projectId);
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
            string.Equals(id, StoreLit.WorkspaceJson, StringComparison.OrdinalIgnoreCase))
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
        var projects = Path.Combine(WorkspaceRoot, StoreLit.Projects);
        var parts = normalizedId.Split('/');
        if (parts.Length == 2)
            return ConfineToProjectsRoot(Path.Combine(projects, parts[0], parts[1]), normalizedId);
        // Legacy flat; also try nested scan if flat missing (caller checks Exists)
        var flat = ConfineToProjectsRoot(Path.Combine(projects, parts[0]), normalizedId);
        if (Directory.Exists(flat))
            return flat;
        // Slow path: find projects/*/slug when id is bare slug stored under a user folder
        if (Directory.Exists(projects))
        {
            foreach (var ownerDir in Directory.GetDirectories(projects))
            {
                var candidate = ConfineToProjectsRoot(Path.Combine(ownerDir, parts[0]), normalizedId);
                if (File.Exists(Path.Combine(candidate, StoreLit.ProjectJson)))
                    return candidate;
            }
        }
        return flat;
    }

    /// <summary>
    /// Canonicalize <paramref name="path"/> and reject anything outside the projects root.
    /// </summary>
    private string ConfineToProjectsRoot(string path, string? displayId = null)
    {
        var root = Path.GetFullPath(Path.Combine(WorkspaceRoot, StoreLit.Projects));
        var full = Path.GetFullPath(path);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid project path: {displayId ?? path}");
        return full;
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
        var dir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var configPath = Path.Combine(dir, StoreLit.PipelineConfigJson);
        var name = StoreLit.BlueprintClipsGrokJson;
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
        var configPath = Path.Combine(dir, StoreLit.PipelineConfigJson);
        var name = StoreLit.BlueprintClipsGrokJson;
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
                     StoreLit.BlueprintClipsGrokJson,
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
        Path.Combine(GetProjectDir(projectId), StoreLit.PipelineConfigJson);

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
                            speakers.Add(pending);
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
            rows.Add(BuildCharacterSummary(projectId, projectDir, speakerTokens, key, info));

        ApplyPlanUsageToCharacters(projectId, rows);

        return rows
            .OrderBy(r =>
            {
                if (r.Key.EndsWith("_Young")) return 1;
                if (r.Key.EndsWith("_Teen")) return 2;
                return 0;
            })
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string JsonStr(JsonElement info, string name) =>
        info.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static string JsonStr(JsonElement info, string name, string fallback)
    {
        if (!info.TryGetProperty(name, out var v))
            return fallback;
        return v.GetString() ?? fallback;
    }

    private static string? JsonStrOrNull(JsonElement info, string name) =>
        info.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static VoiceAgeBand? ReadAgeBand(JsonElement info)
    {
        if (!info.TryGetProperty("age_band", out var ab))
            return null;
        return Enum.TryParse<VoiceAgeBand>(ab.GetString(), true, out var parsedAb) ? parsedAb : null;
    }

    private static VoiceGender ReadGender(JsonElement info)
    {
        var raw = JsonStr(info, "gender");
        if (raw.Length == 0)
            raw = JsonStr(info, "sex");
        if (Enum.TryParse<VoiceGender>(raw, true, out var parsed))
            return parsed;
        if (raw.Contains("female", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("woman", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("girl", StringComparison.OrdinalIgnoreCase))
            return VoiceGender.Female;
        if (raw.Contains("male", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("man", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("boy", StringComparison.OrdinalIgnoreCase))
            return VoiceGender.Male;
        return VoiceGender.Neutral;
    }

    private static string CastKindLabel(bool voiceOnly, bool isGroup)
    {
        if (voiceOnly)
            return "voice_only";
        return isGroup ? "group" : "individual";
    }

    private static bool CharacterSpeaks(HashSet<string> speakerTokens, string key, string display) =>
        speakerTokens.Contains(CastKindClassifier.NormalizeToken(key))
        || (!string.IsNullOrWhiteSpace(display) && speakerTokens.Contains(CastKindClassifier.NormalizeToken(display)));

    private static bool CharacterIsLocked(bool voiceOnly, bool hasRef, JsonElement info)
    {
        if (!voiceOnly)
            return hasRef;
        return !string.IsNullOrWhiteSpace(
            info.TryGetProperty(StoreLit.VoiceProfile, out var vpr) ? vpr.GetString() : null);
    }

    private CharacterSummary BuildCharacterSummary(
        string projectId,
        string projectDir,
        HashSet<string> speakerTokens,
        string key,
        JsonElement info)
    {
        var voiceOnly = IsVoiceOnly(info);
        var display = CharacterDisplayName(key, info);
        var descPreview = JsonStr(info, JsonKeys.Description);
        var castKindRaw = JsonStrOrNull(info, "cast_kind");
        var isGroup = !voiceOnly && CastKindClassifier.IsGroup(key, display, castKindRaw, descPreview);
        var (hasRef, refName, preferred) = ResolveCharacterLook(projectId, projectDir, key, voiceOnly);
        var bookRefs = CollectSeedPlatePaths(info);
        var clonePath = GetVoiceCloneSamplePath(projectId, key);
        var hasClone = File.Exists(clonePath);
        var charUrl = $"{ProjectIdRouting.ProjectApi(projectId)}/characters/{Uri.EscapeDataString(key)}";

        return new CharacterSummary
        {
            Key = key,
            DisplayName = display,
            Description = JsonStr(info, JsonKeys.Description),
            VisualLock = JsonStr(info, StoreLit.VisualLock),
            VoiceProfile = JsonStr(info, StoreLit.VoiceProfile),
            VoiceLabel = JsonStr(info, StoreLit.VoiceLabel),
            SpeciesKind = JsonStrOrNull(info, "species_kind"),
            Speaks = CharacterSpeaks(speakerTokens, key, display),
            HasVoiceCloneSample = hasClone,
            VoiceCloneFileName = hasClone ? Path.GetFileName(clonePath) : null,
            VoiceCloneUrl = hasClone ? $"{charUrl}/voice/clone-sample" : null,
            VoiceProvider = JsonStrOrNull(info, StoreLit.VoiceProvider),
            VoiceProviderVoiceId = JsonStrOrNull(info, StoreLit.VoiceProviderVoiceId),
            ImagineVoiceId = JsonStrOrNull(info, StoreLit.ImagineVoiceId),
            VoiceOnly = voiceOnly,
            IsGroup = isGroup,
            CastKind = CastKindLabel(voiceOnly, isGroup),
            Locked = CharacterIsLocked(voiceOnly, hasRef, info),
            RefFileName = hasRef ? refName : null,
            RefUrl = hasRef ? $"{charUrl}/ref" : null,
            HasPreferred = preferred.HasPreferred,
            PreferredLabel = preferred.Label,
            PreferredUrl = preferred.Url,
            WardrobeAlways = ReadWardrobeAlways(info),
            DesignReferenceImages = bookRefs,
            BookRefs = voiceOnly ? new List<CharacterImageRef>() : CollectBookRefImages(projectId, projectDir, key, bookRefs),
            Variants = voiceOnly ? new List<CharacterImageRef>() : CollectCharacterVariants(projectId, projectDir, key),
            AgeBand = ReadAgeBand(info),
            Gender = ReadGender(info),
            VariantOf = JsonStrOrNull(info, "variant_of"),
            UsedInPlan = true, // filled below from shot plan
        };
    }

    private static string CharacterDisplayName(string key, JsonElement info)
    {
        if (info.TryGetProperty("canonical_given_name", out var cn) &&
            cn.GetString() is { Length: > 0 } cname)
            return cname;
        if (info.TryGetProperty(StoreLit.VoiceLabel, out var vl) && vl.GetString() is { Length: > 0 } lab)
            return lab;
        return key.Replace(JsonKeys.CharacterPrefix, "").Replace("_", " ");
    }

    private static List<string> ReadWardrobeAlways(JsonElement info)
    {
        var wardrobe = new List<string>();
        if (!info.TryGetProperty("wardrobe_always", out var wa) || wa.ValueKind != JsonValueKind.Array)
            return wardrobe;
        foreach (var x in wa.EnumerateArray())
        {
            var s = x.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                wardrobe.Add(s);
        }
        return wardrobe;
    }

    private (bool HasRef, string RefName, (bool HasPreferred, string? Label, string? Url) Preferred)
        ResolveCharacterLook(string projectId, string projectDir, string key, bool voiceOnly)
    {
        var refName = CharacterRefFileName(key);
        var resolvedRef = voiceOnly ? null : ResolveCharacterRefPath(projectId, key, allowNormalizedFallback: false);
        var hasRef = resolvedRef is not null;
        if (resolvedRef is not null)
            refName = Path.GetFileName(resolvedRef);

        var hasPreferred = hasRef;
        string? preferredLabel = hasRef ? "locked" : null;
        string? preferredUrl = hasRef
            ? $"{ProjectIdRouting.ProjectApi(projectId)}/characters/{Uri.EscapeDataString(key)}/ref"
            : null;
        if (!hasPreferred && !voiceOnly)
        {
            var v1 = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Characters,
                $"{key.ToLowerInvariant()}_variant_01.png");
            if (File.Exists(v1) && new FileInfo(v1).Length >= 64)
            {
                hasPreferred = true;
                preferredLabel = "best so far (variant 1)";
                preferredUrl =
                    $"{ProjectIdRouting.ProjectApi(projectId)}/characters/{Uri.EscapeDataString(key)}/variants/1";
            }
        }
        return (hasRef, refName, (hasPreferred, preferredLabel, preferredUrl));
    }

    private static List<CharacterImageRef> CollectBookRefImages(
        string projectId, string projectDir, string key, List<string> bookRefs)
    {
        var bookRefImages = new List<CharacterImageRef>();
        for (var i = 0; i < bookRefs.Count; i++)
        {
            var rel = bookRefs[i].Replace('\\', '/');
            var full = ResolveProjectRelativePath(projectDir, rel);
            // Same filename under assets/characters if seed path moved
            if (full is null || !File.Exists(full))
            {
                var byName = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Characters, Path.GetFileName(rel));
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
                    ? $"{ProjectIdRouting.ProjectApi(projectId)}/characters/{Uri.EscapeDataString(key)}/bookrefs/{i}"
                    : null,
            });
        }
        return bookRefImages;
    }

    private static List<CharacterImageRef> CollectCharacterVariants(string projectId, string projectDir, string key)
    {
        var variants = new List<CharacterImageRef>();
        for (var idx = 1; idx <= 3; idx++)
        {
            var fileName = $"{key.ToLowerInvariant()}_variant_0{idx}.png";
            var full = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Characters, fileName);
            var exists = File.Exists(full) && new FileInfo(full).Length > 64;
            variants.Add(new CharacterImageRef
            {
                Index = idx,
                RelativePath = $"assets/characters/{fileName}",
                FileName = fileName,
                Exists = exists,
                Url = exists
                    ? $"{ProjectIdRouting.ProjectApi(projectId)}/characters/{Uri.EscapeDataString(key)}/variants/{idx}"
                    : null,
            });
        }
        return variants;
    }

    private void ApplyPlanUsageToCharacters(string projectId, List<CharacterSummary> rows)
    {
        // Mark which seeds appear in the current plan (hide unused in UI; keep seeds on disk).
        var (planCast, _) = CollectPlanUsageKeys(projectId);
        if (planCast.Count == 0)
            return;
        foreach (var r in rows)
        {
            r.UsedInPlan = planCast.Contains(r.Key)
                || (!string.IsNullOrWhiteSpace(r.VariantOf) && planCast.Contains(r.VariantOf));
        }
        var usedBases = new HashSet<string>(
            rows.Where(r => r.UsedInPlan).Select(r => r.Key),
            StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (!r.UsedInPlan
                && !string.IsNullOrWhiteSpace(r.VariantOf)
                && usedBases.Contains(r.VariantOf))
                r.UsedInPlan = true;
        }
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
            rows.Add(BuildLocationSummary(projectId, key, info));

        EnrichStubLocationsFromFountain(projectId, rows);
        ApplyPlanUsageToLocations(projectId, rows);
        PageToMovie.Adaptation.Conversion.LocationArchitecturalCoherence.Harmonize(rows);

        return rows
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private LocationSummary BuildLocationSummary(string projectId, string key, JsonElement info)
    {
        var display = info.TryGetProperty(StoreLit.DisplayName, out var dn) && dn.GetString() is { Length: > 0 } dname
            ? dname
            : key.Replace('_', ' ').Trim();
        var desc = JsonStr(info, JsonKeys.Description);
        var vlock = JsonStr(info, StoreLit.VisualLock);
        if (string.IsNullOrWhiteSpace(desc) && !string.IsNullOrWhiteSpace(vlock))
            desc = vlock;
        if (string.IsNullOrWhiteSpace(vlock) && !string.IsNullOrWhiteSpace(desc))
            vlock = desc;
        var anchor = JsonStr(info, "setting_anchor");
        var arch = JsonStr(info, "architectural_features");
        var row = new LocationSummary
        {
            Key = key,
            DisplayName = display,
            Description = desc,
            VisualLock = vlock,
            SettingAnchor = string.IsNullOrWhiteSpace(anchor) ? null : anchor,
            ArchitecturalFeatures = string.IsNullOrWhiteSpace(arch) ? null : arch,
            UsedInPlan = true,
        };
        FillLocationPlateStatus(projectId, row);
        return row;
    }

    private static bool LocationDescriptionsAreStubs(List<LocationSummary> rows) =>
        rows.Count > 0 && rows.All(r =>
            string.IsNullOrWhiteSpace(r.Description)
            || r.Description.Equals(r.DisplayName, StringComparison.OrdinalIgnoreCase)
            || r.Description.Equals(r.Key, StringComparison.OrdinalIgnoreCase));

    private void EnrichStubLocationsFromFountain(string projectId, List<LocationSummary> rows)
    {
        // If seeds were name-only stubs, re-derive from fountain prose and fill blanks.
        if (!LocationDescriptionsAreStubs(rows))
            return;
        var derived = DeriveLocationSeedsFromFountain(projectId);
        foreach (var row in rows)
            ApplyDerivedLocationText(row, derived);
    }

    private static void ApplyDerivedLocationText(LocationSummary row, Dictionary<string, JsonElement> derived)
    {
        if (!derived.TryGetValue(row.Key, out var el)
            && !derived.TryGetValue(row.Key.Replace(' ', '_'), out el))
        {
            // match by display name
            var hit = derived.FirstOrDefault(kv =>
                kv.Value.TryGetProperty(StoreLit.DisplayName, out var dn)
                && string.Equals(dn.GetString(), row.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is null) return;
            el = hit.Value;
        }
        if (el.ValueKind != JsonValueKind.Object) return;
        if (el.TryGetProperty(JsonKeys.Description, out var d) && d.GetString() is { Length: > 0 } desc2)
            row.Description = desc2;
        if (el.TryGetProperty(StoreLit.VisualLock, out var v) && v.GetString() is { Length: > 0 } vl2)
            row.VisualLock = vl2;
    }

    private void ApplyPlanUsageToLocations(string projectId, List<LocationSummary> rows)
    {
        var (_, planLocs) = CollectPlanUsageKeys(projectId);
        if (planLocs.Count == 0)
            return;
        foreach (var row in rows)
            row.UsedInPlan = planLocs.Contains(row.Key);
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

            if (model.TryGetValue(StoreLit.GlobalProductionVariables, out var gpvObj)
                && gpvObj is Dictionary<string, object?> gpv
                && gpv.TryGetValue(StoreLit.LocationSeedTokens, out var locObj)
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
    /// <see cref="ListLocations"/> works after cast extract.
    /// Never drops <c>character_seed_tokens</c> (or other top-level cast keys). Prefer filmable
    /// AI set text over Stage‑1 action dumps ("ext OLYMPUS …").
    /// </summary>
    public bool MergeLocationSeedsIntoCastFile(string projectId, Dictionary<string, object?>? locationSeeds = null)
    {
        try
        {
            locationSeeds ??= ExtractLocationSeedObjects(projectId);
            if (locationSeeds is null || locationSeeds.Count == 0) return false;

            var castPath = Path.Combine(GetProjectDir(projectId), StoreLit.Source, ScreenplayService.CastSeedsFileName);
            var (root, preserved, hadCharacters) = LoadCastRootForLocationMerge(castPath);
            MergeIncomingLocationSeeds(root, locationSeeds);
            RestorePreservedCastFields(root, preserved);

            // If we had characters going in, refuse to write a file without them.
            if (hadCharacters &&
                (root[StoreLit.CharacterSeedTokens] is not System.Text.Json.Nodes.JsonObject checkChars
                 || checkChars.Count == 0))
            {
                    return false;
            }

            File.WriteAllText(castPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct PreservedCastFields(
        System.Text.Json.Nodes.JsonNode? Characters,
        System.Text.Json.Nodes.JsonNode? Wardrobe,
        System.Text.Json.Nodes.JsonNode? MovieTitle,
        System.Text.Json.Nodes.JsonNode? Render,
        System.Text.Json.Nodes.JsonNode? Perf,
        System.Text.Json.Nodes.JsonNode? Schema);

    private static (System.Text.Json.Nodes.JsonObject Root, PreservedCastFields Preserved, bool HadCharacters)
        LoadCastRootForLocationMerge(string castPath)
    {
        if (!File.Exists(castPath))
        {
            var dir = Path.GetDirectoryName(castPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var created = new System.Text.Json.Nodes.JsonObject
            {
                [StoreLit.SchemaVersion] = StoreLit.CastSeedsV1,
                ["generation"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["method"] = "MergeLocationSeedsIntoCastFile",
                    ["ts"] = DateTime.Now.ToString(StoreLit.IsoDateTime),
                },
            };
            return (created, default, false);
        }

        var text = File.ReadAllText(castPath);
        var root = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject
                   ?? new System.Text.Json.Nodes.JsonObject();
        // Explicitly clone cast fields before mutation — Odyssey2 export lost 45 characters
        // when a merge rewrote locations-only JSON after a successful cast extract.
        var preserved = new PreservedCastFields(
            root[StoreLit.CharacterSeedTokens]?.DeepClone(),
            root[StoreLit.WardrobeLockTokens]?.DeepClone(),
            root[JsonKeys.MovieTitle]?.DeepClone(),
            root["render_style_lock"]?.DeepClone(),
            root["performance_lock"]?.DeepClone(),
            root[StoreLit.SchemaVersion]?.DeepClone());
        var hadCharacters = preserved.Characters is System.Text.Json.Nodes.JsonObject co && co.Count > 0;
        return (root, preserved, hadCharacters);
    }

    private static void MergeIncomingLocationSeeds(
        System.Text.Json.Nodes.JsonObject root,
        Dictionary<string, object?> locationSeeds)
    {
        var existing = root[StoreLit.LocationSeedTokens] as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();

        foreach (var (key, val) in locationSeeds)
        {
            if (val is not Dictionary<string, object?> incoming) continue;
            MergeOneLocationSeed(existing, key, incoming);
        }

        root[StoreLit.LocationSeedTokens] = existing;
    }

    private static void MergeOneLocationSeed(
        System.Text.Json.Nodes.JsonObject existing,
        string key,
        Dictionary<string, object?> incoming)
    {
        var incomingNode = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(incoming)) as System.Text.Json.Nodes.JsonObject;
        if (incomingNode is null) return;

        if (existing[key] is not System.Text.Json.Nodes.JsonObject cur)
        {
            existing[key] = incomingNode;
            return;
        }

        var display = cur[StoreLit.DisplayName]?.GetValue<string>()
                      ?? incomingNode[StoreLit.DisplayName]?.GetValue<string>()
                      ?? key;
        foreach (var field in new[] { JsonKeys.Description, StoreLit.VisualLock })
            PreferStrongerLocationField(cur, incomingNode, field, display, key);
        if (cur[StoreLit.DisplayName] is null && incomingNode[StoreLit.DisplayName] is not null)
            cur[StoreLit.DisplayName] = incomingNode[StoreLit.DisplayName]!.DeepClone();
        if (cur[StoreLit.LocationType] is null && incomingNode[StoreLit.LocationType] is not null)
            cur[StoreLit.LocationType] = incomingNode[StoreLit.LocationType]!.DeepClone();
    }

    private static void PreferStrongerLocationField(
        System.Text.Json.Nodes.JsonObject cur,
        System.Text.Json.Nodes.JsonObject incomingNode,
        string field,
        string display,
        string key)
    {
        var curV = cur[field]?.GetValue<string>() ?? "";
        var inV = incomingNode[field]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(inV)) return;

        var curStub = IsWeakLocationField(curV, display, key);
        var inStub = IsWeakLocationField(inV, display, key);

        // Prefer strong filmable text; never replace strong with Stage‑1 action dump.
        if (curStub)
        {
            if (!inStub || inV.Length > curV.Length)
                cur[field] = inV;
        }
        else if (!inStub && inV.Length > curV.Length + 40)
        {
            cur[field] = inV; // longer AI set design wins
        }
    }

    private static void RestorePreservedCastFields(
        System.Text.Json.Nodes.JsonObject root,
        PreservedCastFields preserved)
    {
        // Re-apply preserved cast fields so a merge can never produce locations-only JSON.
        if (preserved.Characters is not null)
            root[StoreLit.CharacterSeedTokens] = preserved.Characters;
        if (preserved.Wardrobe is not null)
            root[StoreLit.WardrobeLockTokens] = preserved.Wardrobe;
        if (preserved.MovieTitle is not null)
            root[JsonKeys.MovieTitle] = preserved.MovieTitle;
        if (preserved.Render is not null)
            root["render_style_lock"] = preserved.Render;
        if (preserved.Perf is not null)
            root["performance_lock"] = preserved.Perf;
        if (preserved.Schema is not null)
            root[StoreLit.SchemaVersion] = preserved.Schema;
        else if (root[StoreLit.SchemaVersion] is null)
            root[StoreLit.SchemaVersion] = StoreLit.CastSeedsV1;
    }

    /// <summary>True when location description/lock is empty, name-only, or Stage‑1 heading echo.</summary>
    private static bool IsWeakLocationField(string value, string display, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim();
        if (v.Equals(display, StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
        if (v.StartsWith("ext ", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("int ", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("int.", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("and int", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("and ext", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public Dictionary<string, object?>? ExtractLocationSeedObjects(string projectId)
    {
        try
        {
            var model = ScreenplayService.TryBuildModelFromProject(this, projectId);
            if (model is null) return null;
            if (model.TryGetValue(StoreLit.GlobalProductionVariables, out var gpvObj)
                && gpvObj is Dictionary<string, object?> gpv
                && gpv.TryGetValue(StoreLit.LocationSeedTokens, out var locObj)
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
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(info))
            return null;

        return ClipVideoPromptBuilder.ResolveCharacterRefPathPublic(
            GetProjectDir(projectId), charKey, allowNormalizedFallback);
    }

    /// <summary>
    /// Cast / location keys referenced by the current Stage‑2 shot plan (scene + clip on-screen lists,
    /// location_ids, primary_location_id). Empty when no blueprint — callers treat all seeds as used.
    /// Seeds not in this set stay on disk but are hidden from default Cast / Locs UI.
    /// </summary>
    public (HashSet<string> CastKeys, HashSet<string> LocationKeys) CollectPlanUsageKeys(string projectId)
    {
        var cast = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is null)
                return (cast, locs);

            if (!bp.RootElement.TryGetProperty(StoreLit.Scenes, out var scenes)
                || scenes.ValueKind != JsonValueKind.Array)
                return (cast, locs);

            foreach (var s in scenes.EnumerateArray())
                CollectPlanUsageFromScene(s, cast, locs);
        }
        catch
        {
            // soft — no plan filter
        }

        return (cast, locs);
    }

    private static void CollectPlanUsageFromScene(JsonElement s, HashSet<string> cast, HashSet<string> locs)
    {
        AddJsonStringArray(s, StoreLit.CharactersOnScreen, cast);
        AddJsonStringProp(s, StoreLit.PrimaryLocationId, locs);
        AddJsonStringArray(s, StoreLit.LocationIds, locs);

        if (!s.TryGetProperty(StoreLit.VeoClips, out var clips) || clips.ValueKind != JsonValueKind.Array)
            return;
        foreach (var c in clips.EnumerateArray())
            CollectPlanUsageFromClip(c, cast, locs);
    }

    private static void CollectPlanUsageFromClip(JsonElement c, HashSet<string> cast, HashSet<string> locs)
    {
        AddJsonStringArray(c, StoreLit.CharactersOnScreen, cast);
        AddJsonStringProp(c, StoreLit.PrimaryLocationId, locs);
        AddJsonStringArray(c, StoreLit.LocationIds, locs);
        if (!c.TryGetProperty(StoreLit.VisualPrompt, out var vp))
            return;
        CollectIdsFromVisualPrompt(vp.GetString() ?? "", cast, locs);
    }

    private static void CollectIdsFromVisualPrompt(string text, HashSet<string> cast, HashSet<string> locs)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 CommonRegex.Matches(text, @"Character_[A-Za-z0-9_]+"))
        {
            if (m.Success) cast.Add(m.Value);
        }
        foreach (System.Text.RegularExpressions.Match m in
                 CommonRegex.Matches(text, @"Loc_[A-Za-z0-9_]+"))
        {
            if (m.Success) locs.Add(m.Value);
        }
    }

    private static void AddJsonStringProp(JsonElement el, string prop, HashSet<string> into)
    {
        if (el.TryGetProperty(prop, out var v) && v.GetString() is { Length: > 0 } s)
            into.Add(s);
    }

    private static void AddJsonStringArray(JsonElement el, string prop, HashSet<string> into)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var x in arr.EnumerateArray())
        {
            var k = x.GetString();
            if (!string.IsNullOrWhiteSpace(k))
                into.Add(k);
        }
    }

    /// <summary>
    /// On-screen cast keys for a scene that are not voice-only and have no locked ref image.
    /// </summary>
    public IReadOnlyList<string> GetUnlockedOnScreenCharacters(string projectId, int sceneNumber)
    {
        using var bp = LoadBlueprintSync(projectId);
        if (bp is null)
            return Array.Empty<string>();

        var sceneEl = FindSceneElement(bp.RootElement, sceneNumber);
        if (sceneEl is null)
            return Array.Empty<string>();

        var cast = CollectOnScreenCastKeys(sceneEl.Value);
        // G3 draft: scene plate lock not required
        if (IsDraftProductionMode(projectId))
            return new List<string>();

        return FilterUnlockedOnScreenCharacters(projectId, cast);
    }

    private static JsonElement? FindSceneElement(JsonElement root, int sceneNumber)
    {
        if (!root.TryGetProperty(StoreLit.Scenes, out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in scenes.EnumerateArray())
        {
            if (s.TryGetProperty(JsonKeys.SceneNumber, out var sn) && sn.TryGetInt32(out var n) && n == sceneNumber)
                return s.Clone();
        }
        return null;
    }

    private static HashSet<string> CollectOnScreenCastKeys(JsonElement sceneEl)
    {
        var cast = new HashSet<string>(StringComparer.Ordinal);
        AddJsonStringArray(sceneEl, StoreLit.CharactersOnScreen, cast);
        AddCharacterKeysFromClipPrompts(sceneEl, cast);
        return cast;
    }

    private static void AddCharacterKeysFromClipPrompts(JsonElement sceneEl, HashSet<string> cast)
    {
        if (!sceneEl.TryGetProperty(StoreLit.VeoClips, out var clips) ||
            clips.ValueKind != JsonValueKind.Array)
            return;
        foreach (var c in clips.EnumerateArray())
        {
            if (!c.TryGetProperty(StoreLit.VisualPrompt, out var vp))
                continue;
            AddCharacterKeysFromText(vp.GetString() ?? "", cast);
        }
    }

    private static void AddCharacterKeysFromText(string text, HashSet<string> cast)
    {
        foreach (Match m in CommonRegex.Matches(text, @"Character_[A-Za-z0-9_]+"))
        {
            if (m.Success)
                cast.Add(m.Value);
        }
    }

    private List<string> FilterUnlockedOnScreenCharacters(string projectId, HashSet<string> cast)
    {
        return cast
            .Where(key => OnScreenCharacterNeedsLock(projectId, key))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool OnScreenCharacterNeedsLock(string projectId, string key)
    {
        var seed = GetCharacterSeed(projectId, key);
        if (seed is not null && IsVoiceOnly(seed.Value))
            return false;
        // Group / ensemble cast (e.g. "Children", "Crowd") have no single portrait identity —
        // the operator can't pick one image for them and shouldn't be forced to. The video model
        // renders group members freely, so a group never requires a locked reference. This mirrors
        // the client readiness gates, which already skip IsGroup, and uses the same
        // CastKindClassifier signal so it generalizes across books/casts (not a name hardcode).
        if (IsGroupSeed(key, seed))
            return false;
        // Blueprint may still say Character_Suitor_1 while cast_seeds only has Character_Suitors.
        // North Star: numbered generics covered by an ensemble group never need a solo plate.
        if (seed is null && ResolvesToExistingGroupCast(projectId, key))
            return false;
        // Unknown seed still counts as needing a lock if mentioned on-screen
        return ResolveCharacterRefPath(projectId, key) is null;
    }

    // ── Location set plates (step 1: storage + lock; generate/edit later) ─────────

    /// <summary>Project-relative directory for location set plates.</summary>
    public static string LocationAssetsRelativeDir => Path.Combine(StoreLit.Assets, "locations");

    public string GetLocationAssetsDir(string projectId)
    {
        var dir = Path.Combine(GetProjectDir(projectId), LocationAssetsRelativeDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LocationRefFileName(string locKey) =>
        ProjectAssetNaming.LocationRefFileName(locKey);

    public static string LocationVariantFileName(string locKey, int index)
    {
        var stem = Path.GetFileNameWithoutExtension(LocationRefFileName(locKey));
        if (stem.EndsWith("_ref", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^"_ref".Length];
        return $"{stem}_variant_{Math.Clamp(index, 1, 9):D2}.png".ToLowerInvariant();
    }

    /// <summary>
    /// Candidate on-disk names for a locked location plate (canonical + Loc_ aliases).
    /// </summary>
    public static IEnumerable<string> LocationRefFileNameCandidates(string locKey) =>
        ProjectAssetNaming.LocationRefFileNameCandidates(locKey);

    /// <summary>Absolute path to locked location plate if present and non-empty.</summary>
    public string? ResolveLocationRefPath(string projectId, string locKey)
    {
        var dir = Path.Combine(GetProjectDir(projectId), LocationAssetsRelativeDir);
        if (!Directory.Exists(dir)) return null;
        var name = LocationRefFileName(locKey);
        var full = Path.Combine(dir, name);
        if (File.Exists(full) && new FileInfo(full).Length >= 64)
            return full;
        if (File.Exists(full + ClientMarkerExtension))
            return full;
        // Alias: Loc_Foo → foo_ref.png without Loc_
        var raw = (locKey ?? "").Trim();
        if (raw.StartsWith(JsonKeys.LocationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bare = raw[JsonKeys.LocationPrefix.Length..];
            var alt = Path.Combine(dir, LocationRefFileName(bare));
            if (File.Exists(alt) && new FileInfo(alt).Length >= 64)
                return alt;
            if (File.Exists(alt + ClientMarkerExtension))
                return alt;
        }
        return null;
    }

    private void FillLocationPlateStatus(string projectId, LocationSummary row)
    {
        var path = ResolveLocationRefPath(projectId, row.Key);
        // Same contract as characters (ReadCharacterRefState): a plate that lives only in the
        // user's folder (.client.json marker) IS locked — the browser pre-flight uploads it for
        // the duration of a generation. Requiring the physical server file (446c39ab) showed
        // client-storage users their locked plates as unlocked.
        var hasLockedPlate = path is not null;
        row.Locked = hasLockedPlate;
        row.HasPreferred = hasLockedPlate;
        if (path is not null)
        {
            row.PreferredRelativePath = Path.Combine(LocationAssetsRelativeDir, Path.GetFileName(path)).Replace('\\', '/');
            row.PreferredUrl = $"{ProjectIdRouting.ProjectApi(projectId)}/locations/{Uri.EscapeDataString(row.Key)}/ref";
        }

        var dir = Path.Combine(GetProjectDir(projectId), LocationAssetsRelativeDir);
        if (!Directory.Exists(dir)) return;
        for (var i = 1; i <= 6; i++)
        {
            var name = LocationVariantFileName(row.Key, i);
            var full = Path.Combine(dir, name);
            var exists = File.Exists(full) && new FileInfo(full).Length >= 64;
            if (!exists) continue;
            row.Variants.Add(new CharacterImageRef
            {
                FileName = name,
                RelativePath = Path.Combine(LocationAssetsRelativeDir, name).Replace('\\', '/'),
                Index = i,
                Exists = true,
                Url = $"{ProjectIdRouting.ProjectApi(projectId)}/locations/{Uri.EscapeDataString(row.Key)}/variants/{i}",
            });
            // LockVariantAsync copies the variant bytes into the ref, so an exact match tells us
            // which look is the locked one (the tile grid shows the lock on it after a reload).
            if (path is not null && row.PreferredVariantIndex is null && SameFileBytes(path, full))
                row.PreferredVariantIndex = i;
        }
    }

    private static bool SameFileBytes(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
            Span<byte> ba = stackalloc byte[8192]; Span<byte> bb = stackalloc byte[8192];
            while (true)
            {
                var na = sa.Read(ba); var nb = sb.Read(bb);
                if (na != nb || !ba[..na].SequenceEqual(bb[..nb])) return false;
                if (na == 0) return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Write description + visual_lock into location_seed_tokens (cast_seeds / blueprint).</summary>
    public bool UpdateLocationLook(
        string projectId,
        string locKey,
        string? description,
        string? visualLock,
        string? settingAnchor = null,
        string? architecturalFeatures = null)
    {
        if (string.IsNullOrWhiteSpace(locKey)) return false;
        try
        {
            var castPath = Path.Combine(GetProjectDir(projectId), StoreLit.Source, ScreenplayService.CastSeedsFileName);
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(castPath))
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(castPath)) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                if (Path.GetDirectoryName(castPath) is { } dir)
                    Directory.CreateDirectory(dir);
                root = new System.Text.Json.Nodes.JsonObject { [StoreLit.SchemaVersion] = StoreLit.CastSeedsV1 };
            }

            var locs = root[StoreLit.LocationSeedTokens] as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            var entry = locs[locKey] as System.Text.Json.Nodes.JsonObject
                        ?? new System.Text.Json.Nodes.JsonObject
                        {
                            [StoreLit.DisplayName] = locKey.StartsWith(JsonKeys.LocationPrefix, StringComparison.OrdinalIgnoreCase)
                                ? locKey[JsonKeys.LocationPrefix.Length..].Replace('_', ' ')
                                : locKey.Replace('_', ' '),
                        };
            if (description is not null)
                entry[JsonKeys.Description] = description;
            if (visualLock is not null)
                entry[StoreLit.VisualLock] = visualLock;
            if (settingAnchor is not null)
                entry["setting_anchor"] = settingAnchor;
            if (architecturalFeatures is not null)
                entry["architectural_features"] = architecturalFeatures;
            locs[locKey] = entry;
            root[StoreLit.LocationSeedTokens] = locs;
            // Preserve character_seed_tokens if present
            File.WriteAllText(castPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Write uploaded/generated bytes as the locked location plate.</summary>
    public string LockLocationRefFromBytes(string projectId, string locKey, byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length < 64)
            throw new InvalidOperationException("Location image is empty or unreadable.");
        var dir = GetLocationAssetsDir(projectId);
        var name = LocationRefFileName(locKey);
        var full = Path.Combine(dir, name);
        File.WriteAllBytes(full, pngBytes);
        return full;
    }

    /// <summary>
    /// Canonical locked ref: <c>{character_key_lower}_ref.png</c>
    /// e.g. Character_Mom → character_mom_ref.png.
    /// </summary>
    public static string CharacterRefFileName(string charKey) =>
        ProjectAssetNaming.CharacterRefFileName(charKey);

    /// <summary>
    /// Candidate on-disk names for a locked ref (canonical + short aliases + common typos).
    /// </summary>
    public static IEnumerable<string> CharacterRefFileCandidates(string charKey) =>
        ProjectAssetNaming.CharacterRefFileCandidates(charKey);

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
        return string.IsNullOrWhiteSpace(key) ? null : GetWardrobeLock(projectId, key);
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
        return k.EndsWith(StoreLit.RefPngSuffix, StringComparison.OrdinalIgnoreCase) ? k : $"{k}_ref.png";
    }

    /// <summary>Existing shared costume reference path for a wardrobe group, or null if not generated yet.</summary>
    public string? ResolveWardrobeRefPath(string projectId, string wardrobeKey)
    {
        var path = Path.Combine(
            GetProjectDir(projectId), StoreLit.Assets, StoreLit.Characters, WardrobeRefFileName(wardrobeKey));
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
            if (el.TryGetProperty(StoreLit.VoiceProviderVoiceId, out var altEl) &&
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
            if (el.TryGetProperty(StoreLit.VoiceProvider, out var pEl) &&
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
        Path.Combine(GetProjectDir(projectId), StoreLit.Assets, StoreLit.Characters);

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
        string? voiceCloneProviderId = null,
        string? imagineVoiceId = null)
    {
        var patch = new CharacterSeedTextPatch(
            charKey, description, visualLock, voiceProfile, voiceLabel,
            voiceCloneSample, voiceProvider, voiceProviderVoiceId, voiceCloneProviderId,
            imagineVoiceId);
        PatchCharacterSeedFile(ScreenplayService.GetCastSeedsPath(this, projectId), patch, createCastShape: true);
        var bp = FindBlueprintPathSync(projectId);
        if (bp is not null)
            PatchCharacterSeedFile(bp, patch, createCastShape: false);
        var scenesPath = ResolveScenesJsonPath(projectId);
        if (File.Exists(scenesPath))
            PatchCharacterSeedFile(scenesPath, patch, createCastShape: false);
        TriggerAutoGitCommit(projectId, "Update character seeds");
    }

    private readonly record struct CharacterSeedTextPatch(
        string CharKey,
        string? Description,
        string? VisualLock,
        string? VoiceProfile,
        string? VoiceLabel,
        string? VoiceCloneSample,
        string? VoiceProvider,
        string? VoiceProviderVoiceId,
        string? VoiceCloneProviderId,
        string? ImagineVoiceId);

    private static void ApplyCharacterSeedTextPatch(
        System.Text.Json.Nodes.JsonObject seeds,
        CharacterSeedTextPatch patch)
    {
        var (seed, foundKey) = FindSeedByCharKey(seeds, patch.CharKey);
        if (seed is null || foundKey is null)
        {
            seed = new System.Text.Json.Nodes.JsonObject();
            foundKey = patch.CharKey;
            seeds[foundKey] = seed;
        }
        if (patch.Description is not null)
            seed[JsonKeys.Description] = CharacterVisualTextScrubber.ScrubVisualProse(patch.Description);
        if (patch.VisualLock is not null)
            seed[StoreLit.VisualLock] = CharacterVisualTextScrubber.ScrubVisualProse(patch.VisualLock);
        if (patch.VoiceProfile is not null)
            seed[StoreLit.VoiceProfile] = patch.VoiceProfile.Trim();
        if (patch.VoiceLabel is not null)
            seed[StoreLit.VoiceLabel] = patch.VoiceLabel.Trim();
        SetOrRemoveJsonString(seed, "voice_clone_sample", patch.VoiceCloneSample);
        SetOrRemoveJsonString(seed, StoreLit.VoiceProvider, patch.VoiceProvider);
        SetOrRemoveJsonString(seed, StoreLit.VoiceProviderVoiceId, patch.VoiceProviderVoiceId);
        SetOrRemoveJsonString(seed, "voice_clone_provider_id", patch.VoiceCloneProviderId);
        SetOrRemoveJsonString(seed, StoreLit.ImagineVoiceId, patch.ImagineVoiceId);
        seeds[foundKey] = seed;
    }

    private static void SetOrRemoveJsonString(System.Text.Json.Nodes.JsonObject seed, string key, string? value)
    {
        if (value is null)
            return;
        if (string.IsNullOrWhiteSpace(value))
            seed.Remove(key);
        else
            seed[key] = value.Trim();
    }

    private static void PatchCharacterSeedFile(string path, CharacterSeedTextPatch patch, bool createCastShape)
    {
        try
        {
            if (!TryLoadCharacterSeedRoot(path, createCastShape, out var root, out var seeds, out var gpv))
                return;

            ApplyCharacterSeedTextPatch(seeds, patch);
            // Bug fix (pre-existing, hit by any brand-new cast_seeds.json write — e.g. the
            // first voice/clone call for a narration pseudo-character on a project with no
            // cast_seeds.json yet): a JsonNode instance can only have one parent, so the same
            // `seeds` object can't be assigned directly to both root and global_production_
            // variables. Mirror a separate parsed copy instead of the same reference — root[
            // "character_seed_tokens"] previously threw "node already has a parent" here,
            // which this method's catch-all silently swallowed, so the whole write was
            // dropped with no visible error.
            if (createCastShape && gpv is not null)
                root[StoreLit.CharacterSeedTokens] = System.Text.Json.Nodes.JsonNode.Parse(seeds.ToJsonString());
            if (Path.GetDirectoryName(path) is { } dir)
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, root.ToJsonString(JsonDefaults.Indented) + "\n");
        }
        catch
        {
            /* non-fatal */
        }
    }

    private static bool TryLoadCharacterSeedRoot(
        string path,
        bool createCastShape,
        out System.Text.Json.Nodes.JsonObject root,
        out System.Text.Json.Nodes.JsonObject seeds,
        out System.Text.Json.Nodes.JsonObject? gpv)
    {
        root = null!;
        seeds = null!;
        gpv = null;
        if (File.Exists(path))
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                   as System.Text.Json.Nodes.JsonObject
                   ?? new System.Text.Json.Nodes.JsonObject();
        }
        else if (createCastShape)
        {
            root = new System.Text.Json.Nodes.JsonObject { [StoreLit.SchemaVersion] = StoreLit.CastSeedsV1 };
        }
        else
        {
            return false;
        }

        if (root[StoreLit.CharacterSeedTokens] is System.Text.Json.Nodes.JsonObject direct)
        {
            seeds = direct;
            return true;
        }

        gpv = root[StoreLit.GlobalProductionVariables] as System.Text.Json.Nodes.JsonObject
              ?? new System.Text.Json.Nodes.JsonObject();
        root[StoreLit.GlobalProductionVariables] = gpv;
        seeds = gpv[StoreLit.CharacterSeedTokens] as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();
        gpv[StoreLit.CharacterSeedTokens] = seeds;
        return true;
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
        PatchWardrobeLockFile(
            ScreenplayService.GetCastSeedsPath(this, projectId), wardrobeKey, description, visualLock, createCastShape: true);
        var bp = FindBlueprintPathSync(projectId);
        if (bp is not null)
            PatchWardrobeLockFile(bp, wardrobeKey, description, visualLock, createCastShape: false);
        var scenesPath = ResolveScenesJsonPath(projectId);
        if (File.Exists(scenesPath))
            PatchWardrobeLockFile(scenesPath, wardrobeKey, description, visualLock, createCastShape: false);
    }

    private static void PatchWardrobeLockObject(
        System.Text.Json.Nodes.JsonObject locks,
        string wardrobeKey,
        string? description,
        string? visualLock)
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
            entry[JsonKeys.Description] = CharacterVisualTextScrubber.ScrubVisualProse(description);
        if (visualLock is not null)
            entry[StoreLit.VisualLock] = CharacterVisualTextScrubber.ScrubVisualProse(visualLock);
        locks[foundKey] = entry;
    }

    private static void PatchWardrobeLockFile(
        string path,
        string wardrobeKey,
        string? description,
        string? visualLock,
        bool createCastShape)
    {
        try
        {
            if (!TryLoadWardrobeLockRoot(path, createCastShape, out var root))
                return;
            var locks = ResolveWardrobeLockObject(root);
            PatchWardrobeLockObject(locks, wardrobeKey, description, visualLock);
            if (Path.GetDirectoryName(path) is { } dir)
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, root.ToJsonString(JsonDefaults.Indented) + "\n");
        }
        catch
        {
            /* non-fatal */
        }
    }

    private static bool TryLoadWardrobeLockRoot(
        string path, bool createCastShape, out System.Text.Json.Nodes.JsonObject root)
    {
        if (File.Exists(path))
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                   as System.Text.Json.Nodes.JsonObject
                   ?? new System.Text.Json.Nodes.JsonObject();
            return true;
        }
        if (createCastShape)
        {
            root = new System.Text.Json.Nodes.JsonObject { [StoreLit.SchemaVersion] = StoreLit.CastSeedsV1 };
            return true;
        }
        root = null!;
        return false;
    }

    private static System.Text.Json.Nodes.JsonObject ResolveWardrobeLockObject(
        System.Text.Json.Nodes.JsonObject root)
    {
        if (root[StoreLit.WardrobeLockTokens] is System.Text.Json.Nodes.JsonObject direct)
            return direct;
        var gpv = root[StoreLit.GlobalProductionVariables] as System.Text.Json.Nodes.JsonObject;
        if (gpv is not null && gpv[StoreLit.WardrobeLockTokens] is System.Text.Json.Nodes.JsonObject gpvLocks)
            return gpvLocks;
        var locks = new System.Text.Json.Nodes.JsonObject();
        root[StoreLit.WardrobeLockTokens] = locks;
        return locks;
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
                [StoreLit.VoiceProfile] = p.VoiceProfile,
                [StoreLit.VoiceLabel] = p.VoiceLabel,
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

        // cast_seeds.json / scenes first so Characters page edits win
        try
        {
            IngestCharacterPromptProfiles(map, LoadCharacterSeeds(projectId));
        }
        catch { /* ignore */ }

        return map;
    }

    private static void IngestCharacterPromptProfiles(
        Dictionary<string, ClipVideoPromptBuilder.CharacterProfile> map,
        Dictionary<string, JsonElement> seeds)
    {
        foreach (var (key, info) in seeds)
        {
            var profile = TryBuildCharacterPromptProfile(key, info);
            if (profile is not null)
                map[key] = profile;
        }
    }

    private static ClipVideoPromptBuilder.CharacterProfile? TryBuildCharacterPromptProfile(
        string key, JsonElement info)
    {
        var desc = JsonStr(info, JsonKeys.Description).Trim();
        var vlock = JsonStr(info, StoreLit.VisualLock).Trim();
        var profile = JsonStr(info, StoreLit.VoiceProfile).Trim();
        var label = JsonStr(info, StoreLit.VoiceLabel).Trim();
        var imagineVoiceId = JsonStr(info, StoreLit.ImagineVoiceId).Trim();
        var gender = JsonStr(info, "gender").Trim();
        if (gender.Length == 0)
            gender = JsonStr(info, "sex").Trim();
        var ageBand = JsonStr(info, "age_band").Trim();
        if (desc.Length == 0 && vlock.Length == 0 && profile.Length == 0 && label.Length == 0 && imagineVoiceId.Length == 0)
            return null;
        var display = JsonStr(info, "canonical_given_name").Trim();
        if (display.Length == 0)
            display = key.Replace(JsonKeys.CharacterPrefix, "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
        // Prefer cast seed policy only — never force VOICE ONLY because key is "Narrator"
        // (on-camera confessor / POV roles are common and need locked face refs).
        // Shared mechanism: CastKindClassifier.IsVoiceOnlyPolicy.
        return new ClipVideoPromptBuilder.CharacterProfile
        {
            Key = key,
            DisplayName = display,
            Description = desc,
            VisualLock = vlock,
            VoiceProfile = profile,
            VoiceLabel = label,
            ImagineVoiceId = imagineVoiceId,
            Gender = gender,
            AgeBand = ageBand,
            VoiceOnly = info.TryGetProperty("display_name_policy", out var pol) &&
                        CastKindClassifier.IsVoiceOnlyPolicy(pol.GetString()),
            CastKind = JsonStr(info, "cast_kind").Trim(),
        };
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
        PatchCharacterSeedsFile(Path.Combine(dir, StoreLit.Source, ScreenplayService.CastSeedsFileName), PatchSeedsObject);
        PatchCharacterSeedsFile(Path.Combine(dir, StoreLit.ScenesJson), PatchSeedsObject);
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

        var clips = FindSceneClipsArray(scenes, scene);
        var clipObj = clips is null ? null : FindClipNode(clips, clip);
        if (clipObj is null)
            throw new InvalidOperationException($"Clip S{scene:D2}C{clip:D2} not found in shot plan.");

        clipObj[StoreLit.VisualPrompt] = (visualPrompt ?? "").Trim();
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
            if (ReadJsonNodeInt(s[JsonKeys.SceneNumber]) != scene) continue;
            return s[StoreLit.VeoClips] as System.Text.Json.Nodes.JsonArray
                   ?? s[StoreLit.Clips] as System.Text.Json.Nodes.JsonArray;
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
        var scenes = root[StoreLit.Scenes] as System.Text.Json.Nodes.JsonArray
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

    private static readonly Regex CharacterKeyRx = new(@"^Character_[A-Za-z][A-Za-z0-9_]{0,80}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, CommonRegex.Timeout);

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
        NormalizeClipEditFields(fields);
        ValidateClipEditBounds(fields, minSeconds, absMaxSeconds);
        ValidateClipEditAudio(fields);
        ValidateClipEditCastKeys(fields, knownCastKeys);
        AutoIncludeOnScreenCast(fields);
    }

    private static void NormalizeClipEditFields(ClipEditRequest fields)
    {
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
    }

    private static void ValidateClipEditBounds(ClipEditRequest fields, int minSeconds, int absMaxSeconds)
    {
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
    }

    private static void ValidateClipEditAudio(ClipEditRequest fields)
    {
        var delivery = fields.Delivery ?? "";
        var dialogue = fields.Dialogue ?? "";
        var speaker = fields.Speaker ?? "";

        // Delivery allowlist when set
        if (delivery.Length > 0 && !AllowedDeliveries.Contains(delivery))
            throw new InvalidOperationException(
                "Delivery must be spoken_on_camera, voiceover_internal, off_camera, or none.");

        var deliveryNone = delivery.Length == 0 ||
                           string.Equals(delivery, "none", StringComparison.OrdinalIgnoreCase);

        // Audio consistency
        if (dialogue.Length > 0 && speaker.Length == 0)
            throw new InvalidOperationException(
                "Dialogue needs a speaker. Pick who says the line, or clear the dialogue text.");

        if (dialogue.Length > 0 && deliveryNone)
            throw new InvalidOperationException(
                "Dialogue needs a delivery (spoken_on_camera, voiceover_internal, or off_camera) — not none.");

        if (speaker.Length > 0 && dialogue.Length == 0)
            throw new InvalidOperationException(
                "Speaker is set but dialogue is empty. Add the line, or set speaker to none.");
    }

    private static void ValidateClipEditCastKeys(ClipEditRequest fields, IReadOnlyCollection<string>? knownCastKeys)
    {
        var speaker = fields.Speaker ?? "";
        var primarySubject = fields.PrimarySubject ?? "";

        // Cast identity: Character_* keys only (no free-text display names)
        if (speaker.Length > 0)
            RequireCharacterKey(speaker, "Speaker");
        if (primarySubject.Length > 0)
            RequireCharacterKey(primarySubject, "Primary subject");
        foreach (var ck in fields.CharactersOnScreen)
            RequireCharacterKey(ck, "On-screen character");

        var cast = knownCastKeys?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cast is not { Count: > 0 })
            return;
        if (speaker.Length > 0 && !cast.Contains(speaker))
            throw new InvalidOperationException(
                $"Speaker must be a cast member (unknown key: {speaker}).");
        if (primarySubject.Length > 0 && !cast.Contains(primarySubject))
            throw new InvalidOperationException(
                $"Primary subject must be a cast member (unknown key: {primarySubject}).");
        var unknownOnScreen = fields.CharactersOnScreen.FirstOrDefault(ck => !cast.Contains(ck));
        if (unknownOnScreen is not null)
        {
            throw new InvalidOperationException(
                $"On-screen list has unknown cast key: {unknownOnScreen}.");
        }
    }

    private static void AutoIncludeOnScreenCast(ClipEditRequest fields)
    {
        var primarySubject = fields.PrimarySubject ?? "";
        var speaker = fields.Speaker ?? "";

        // Auto-include primary + on-camera speaker in on-screen list
        if (primarySubject.Length > 0 &&
            !fields.CharactersOnScreen.Any(c =>
                string.Equals(c, primarySubject, StringComparison.OrdinalIgnoreCase)))
        {
            fields.CharactersOnScreen.Add(primarySubject);
        }

        var onCam = Stage2PlannerService.IsOnCameraDelivery(fields.Delivery);
        if (onCam &&
            speaker.Length > 0 &&
            !fields.CharactersOnScreen.Any(c =>
                string.Equals(c, speaker, StringComparison.OrdinalIgnoreCase)))
        {
            fields.CharactersOnScreen.Add(speaker);
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
        var castKeys = TryLoadCastKeys(projectId);
        var modelId = TryLoadClipVideoModelId(projectId);
        var (durMinSeconds, _, durAbsMaxSeconds) = ClipDurationEstimator.ResolveBoundsForModel(modelId);

        CanonicalizeClipCastKeys(fields, castKeys);
        ValidateClipEditRequest(fields, castKeys, durMinSeconds, durAbsMaxSeconds);

        clipObj[StoreLit.VisualPrompt] = fields.VisualPrompt;
        clipObj["negative_prompt"] = fields.NegativePrompt;
        EnsureStage1BeatId(clipObj, fields);
        clipObj["primary_subject"] = fields.PrimarySubject;
        clipObj[StoreLit.DurationSeconds] = fields.DurationSeconds;
        clipObj[StoreLit.CharactersOnScreen] = new System.Text.Json.Nodes.JsonArray(
            fields.CharactersOnScreen
                .Select(c => System.Text.Json.Nodes.JsonValue.Create(c) as System.Text.Json.Nodes.JsonNode)
                .ToArray());
        clipObj["color_palette"] = fields.ColorPalette;
        clipObj["film_stock"] = fields.FilmStock;

        ApplyClipAudioPayload(clipObj, fields);
        SyncClipRootAudioFields(clipObj, fields);
    }

    private IReadOnlyCollection<string>? TryLoadCastKeys(string projectId)
    {
        try { return LoadCharacterSeeds(projectId).Keys.ToList(); }
        catch { return null; }
    }

    private string? TryLoadClipVideoModelId(string projectId)
    {
        try
        {
            var cfg = GetConfigSync(projectId);
            if (cfg.TryGetValue("model_name", out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch
        {
            /* use default */
        }
        return null;
    }

    private static void CanonicalizeClipCastKeys(ClipEditRequest fields, IReadOnlyCollection<string>? castKeys)
    {
        if (castKeys is not { Count: > 0 })
            return;
        var byNormalizedKey = castKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(Stage2PlannerService.NormalizeCharacterKey)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(fields.Speaker))
            fields.Speaker = CanonicalizeCastKey(fields.Speaker, byNormalizedKey);
        if (!string.IsNullOrEmpty(fields.PrimarySubject))
            fields.PrimarySubject = CanonicalizeCastKey(fields.PrimarySubject, byNormalizedKey);
        fields.CharactersOnScreen = fields.CharactersOnScreen
            .Select(k => CanonicalizeCastKey(k, byNormalizedKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CanonicalizeCastKey(string key, Dictionary<string, string> byNormalizedKey) =>
        byNormalizedKey.TryGetValue(Stage2PlannerService.NormalizeCharacterKey(key), out var real)
            ? real
            : key;

    private static void EnsureStage1BeatId(System.Text.Json.Nodes.JsonObject clipObj, ClipEditRequest fields)
    {
        if (!string.IsNullOrWhiteSpace(clipObj["stage1_beat_id"]?.ToString()))
            return;
        var kind = string.IsNullOrWhiteSpace(fields.Dialogue) ? "action" : JsonKeys.Dialogue;
        var body = string.IsNullOrWhiteSpace(fields.Dialogue) ? fields.VisualPrompt : fields.Dialogue;
        clipObj["stage1_beat_id"] = PageToMovie.Core.Utils.StableBeatId.ForContent(
            $"S{fields.Scene:D2}", kind, fields.Speaker, body);
    }

    private static void ApplyClipAudioPayload(System.Text.Json.Nodes.JsonObject clipObj, ClipEditRequest fields)
    {
        if (clipObj[JsonKeys.AudioPayload] is not System.Text.Json.Nodes.JsonObject audio)
        {
            audio = new System.Text.Json.Nodes.JsonObject();
            clipObj[JsonKeys.AudioPayload] = audio;
        }
        audio[JsonKeys.Dialogue] = fields.Dialogue;
        audio[JsonKeys.Speaker] = NullIfBlank(fields.Speaker);
        audio[StoreLit.Delivery] = NullIfBlank(fields.Delivery);
        if (!string.IsNullOrWhiteSpace(fields.PronunciationHint))
            audio["pronunciation_hint"] = fields.PronunciationHint;
    }

    private static void SyncClipRootAudioFields(System.Text.Json.Nodes.JsonObject clipObj, ClipEditRequest fields)
    {
        if (clipObj.ContainsKey(JsonKeys.Dialogue))
            clipObj[JsonKeys.Dialogue] = fields.Dialogue;
        if (clipObj.ContainsKey(JsonKeys.Speaker))
            clipObj[JsonKeys.Speaker] = NullIfBlank(fields.Speaker);
        if (clipObj.ContainsKey(StoreLit.Delivery))
            clipObj[StoreLit.Delivery] = NullIfBlank(fields.Delivery);
        if (clipObj.ContainsKey(StoreLit.AudioScript))
            clipObj[StoreLit.AudioScript] = fields.Dialogue;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
        clipObj[JsonKeys.ClipNumber] = clip;

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
            [JsonKeys.ClipNumber] = fields.Clip,
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
        var removedFromBlueprint = TryRemoveClipFromBlueprint(projectId, scene, clip);
        var deletedVideo = DeleteClipMediaFiles(projectDir, scene, clip);

        if (!removedFromBlueprint && !deletedVideo)
            throw new InvalidOperationException($"Clip S{scene:D2}C{clip:D2} not found.");

        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        return removedFromBlueprint;
    }

    private bool TryRemoveClipFromBlueprint(string projectId, int scene, int clip)
    {
        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is null || !File.Exists(bpPath))
            return false;

        var (root, scenes) = ParseBlueprintScenes(bpPath);
        var removed = RemoveClipNode(scenes, scene, clip);
        if (removed)
            File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        return removed;
    }

    private static bool RemoveClipNode(System.Text.Json.Nodes.JsonArray scenes, int scene, int clip)
    {
        foreach (var sNode in scenes)
        {
            if (sNode is not System.Text.Json.Nodes.JsonObject s) continue;
            if (ReadJsonNodeInt(s[JsonKeys.SceneNumber]) != scene) continue;
            var clips = s[StoreLit.VeoClips] as System.Text.Json.Nodes.JsonArray
                        ?? s[StoreLit.Clips] as System.Text.Json.Nodes.JsonArray;
            if (clips is null) return false;
            for (var i = 0; i < clips.Count; i++)
            {
                if (clips[i] is not System.Text.Json.Nodes.JsonObject c) continue;
                if (ClipKeying.ClipNumber(c) != clip) continue;
                clips.RemoveAt(i);
                return true;
            }
            return false;
        }
        return false;
    }

    private static bool DeleteClipMediaFiles(string projectDir, int scene, int clip)
    {
        var videoPath = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Video, $"scene_{scene:D2}_clip_{clip:D2}.mp4");
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
        return deletedVideo;
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
        var removed = TryRemoveSceneFromBlueprint(projectId, scene);
        DeleteSceneMediaFiles(projectDir, scene);

        if (!removed)
            throw new InvalidOperationException($"Scene {scene} not found.");

        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        return removed;
    }

    private bool TryRemoveSceneFromBlueprint(string projectId, int scene)
    {
        var bpPath = FindBlueprintPathSync(projectId);
        if (bpPath is null || !File.Exists(bpPath))
            return false;

        var (root, scenes) = ParseBlueprintScenes(bpPath);
        var removed = false;
        for (var i = 0; i < scenes.Count; i++)
        {
            if (scenes[i] is not System.Text.Json.Nodes.JsonObject s) continue;
            if (ReadJsonNodeInt(s[JsonKeys.SceneNumber]) != scene) continue;
            scenes.RemoveAt(i);
            removed = true;
            break;
        }

        if (removed)
            File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");
        return removed;
    }

    private static void DeleteSceneMediaFiles(string projectDir, int scene)
    {
        var videoDir = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Video);
        if (!Directory.Exists(videoDir))
            return;
        foreach (var f in Directory.EnumerateFiles(videoDir, $"scene_{scene:D2}*", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
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
        if (creditsIndex >= 0 && scenes[creditsIndex] is System.Text.Json.Nodes.JsonObject creditsObj)
        {
            next = ReadJsonNodeInt(creditsObj[JsonKeys.SceneNumber]);
            for (var i = creditsIndex; i < scenes.Count; i++)
                if (scenes[i] is System.Text.Json.Nodes.JsonObject so)
                    so[JsonKeys.SceneNumber] = ReadJsonNodeInt(so[JsonKeys.SceneNumber]) + 1;
        }
        else
        {
            next = NextSceneNumber(scenes);
        }

        var sceneObj = new System.Text.Json.Nodes.JsonObject
        {
            [JsonKeys.SceneNumber] = next,
            [StoreLit.Setting] = string.IsNullOrWhiteSpace(setting) ? "INT. NEW SCENE - DAY" : setting.Trim(),
            [StoreLit.VeoClips] = new System.Text.Json.Nodes.JsonArray(),
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
            [JsonKeys.ClipNumber] = 1,
            ["timestamp"] = "",
            ["veo_continuation_source"] = "none",
            [StoreLit.IsCredits] = true,
            [StoreLit.VisualPrompt] = BuildCreditsVisualPrompt(projectId),
            [JsonKeys.AudioPayload] = new System.Text.Json.Nodes.JsonObject { [JsonKeys.Speaker] = "", [JsonKeys.Dialogue] = "" },
        };
        var sceneObj = new System.Text.Json.Nodes.JsonObject
        {
            [JsonKeys.SceneNumber] = next,
            [StoreLit.Setting] = "END CREDITS",
            [StoreLit.IsCredits] = true,
            [StoreLit.VeoClips] = new System.Text.Json.Nodes.JsonArray { clip },
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
        var fromFountain = TryReadFountainTitle(projectId);
        if (fromFountain is not null) return fromFountain;
        var fromBlueprint = TryReadBlueprintMovieTitle(projectId);
        if (fromBlueprint is not null) return fromBlueprint;
        return projectId;
    }

    private string? TryReadFountainTitle(string projectId)
    {
        try
        {
            var fountainPath = GetScreenplayPath(projectId);
            if (!File.Exists(fountainPath))
                return null;
            foreach (var line in File.ReadLines(fountainPath).Take(30))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var val = trimmed[6..].Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private string? TryReadBlueprintMovieTitle(string projectId)
    {
        try
        {
            var bpPath = FindBlueprintPathSync(projectId);
            if (bpPath is null)
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(bpPath));
            if (doc.RootElement.TryGetProperty(JsonKeys.MovieTitle, out var mt) &&
                mt.ValueKind == JsonValueKind.String && mt.GetString() is { Length: > 0 } m)
                return m.Trim();
        }
        catch { /* fall through */ }
        return null;
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
        var scenes = root[StoreLit.Scenes] as System.Text.Json.Nodes.JsonArray;
        if (scenes is null)
        {
            scenes = new System.Text.Json.Nodes.JsonArray();
            root[StoreLit.Scenes] = scenes;
        }
        return (root, scenes, bpPath);
    }

    private static int NextSceneNumber(System.Text.Json.Nodes.JsonArray scenes)
    {
        var next = 1;
        foreach (var s in scenes)
            if (s is System.Text.Json.Nodes.JsonObject so)
                next = Math.Max(next, ReadJsonNodeInt(so[JsonKeys.SceneNumber]) + 1);
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

        if (tree.TryGetValue(StoreLit.CharacterSeedTokens, out var direct) && direct is not null)
        {
            var seedsJson = JsonSerializer.Serialize(direct);
            seeds = JsonSerializer.Deserialize<Dictionary<string, object?>>(seedsJson)
                    ?? new Dictionary<string, object?>();
            commit = () => tree[StoreLit.CharacterSeedTokens] = seeds;
        }
        else if (tree.TryGetValue(StoreLit.GlobalProductionVariables, out var gpvObj) && gpvObj is not null)
        {
            var gpvJson = JsonSerializer.Serialize(gpvObj);
            var gpv = JsonSerializer.Deserialize<Dictionary<string, object?>>(gpvJson)
                      ?? new Dictionary<string, object?>();
            if (!gpv.TryGetValue(StoreLit.CharacterSeedTokens, out var seedsObj) || seedsObj is null)
                return;
            var seedsJson = JsonSerializer.Serialize(seedsObj);
            seeds = JsonSerializer.Deserialize<Dictionary<string, object?>>(seedsJson)
                    ?? new Dictionary<string, object?>();
            commit = () =>
            {
                gpv[StoreLit.CharacterSeedTokens] = seeds;
                tree[StoreLit.GlobalProductionVariables] = gpv;
            };
        }
        else
            return;

        // Case-insensitive key match (Character_Narrator vs character_narrator)
        string? matchKey = seeds.Keys.FirstOrDefault(k =>
            string.Equals(k, charKey, StringComparison.OrdinalIgnoreCase));
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
        var metaPath = Path.Combine(dir, StoreLit.ProjectJson);
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
            if (doc.RootElement.TryGetProperty("character_plates", out var cp) &&
                cp.ValueKind == JsonValueKind.Object)
            {
                ApplyNestedCharacterPlates(cp, state);
                return state;
            }
            ApplyLegacyCharacterPlates(doc.RootElement, state);
        }
        catch { /* ignore */ }
        return state;
    }

    private static void ApplyNestedCharacterPlates(JsonElement cp, CharacterPlatesState state)
    {
        state.SortedByCharacter = ReadJsonBool(cp, "sorted_by_character");
        state.SortedAt = ReadJsonStringIfPresent(cp, "sorted_at");
        var src = ReadJsonStringIfPresent(cp, StoreLit.Source);
        if (src is { Length: > 0 })
            state.Source = src;
        if (TryReadJsonInt32(cp, "characters_updated", out var n))
            state.CharactersUpdated = n;
        state.Method = ReadJsonStringIfPresent(cp, "method");
    }

    private static void ApplyLegacyCharacterPlates(JsonElement root, CharacterPlatesState state)
    {
        if (!ReadJsonBool(root, "character_plates_sorted"))
            return;
        state.SortedByCharacter = true;
        state.SortedAt = ReadJsonStringIfPresent(root, "character_plates_sorted_at");
        state.Method = ReadJsonStringIfPresent(root, "character_plates_method");
    }

    private static string? ReadJsonStringIfPresent(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        return el.GetString();
    }

    private static bool TryReadJsonInt32(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el) && el.TryGetInt32(out value);
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
        var now = DateTime.Now.ToString(StoreLit.IsoDateTime);
        merged["character_plates"] = new Dictionary<string, object?>
        {
            ["sorted_by_character"] = true,
            ["sorted_at"] = now,
            [StoreLit.Source] = "scenes.json#character_seed_tokens.design_reference_images",
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
            [StoreLit.Source] = "scenes.json#character_seed_tokens.design_reference_images",
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
        if (merged.TryGetValue(StoreLit.BookSubsteps, out var existing) && existing is not null)
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
            ["ts"] = DateTime.Now.ToString(StoreLit.IsoDateTime),
        };
        if (stage == BookSubstepKeys.FitLength && targetMinutes is > 0)
            entry["target_minutes"] = targetMinutes;
        subs[stage] = entry;

        merged[StoreLit.BookSubsteps] = subs;
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
        if (!merged.Remove(StoreLit.BookSubsteps)) return;
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
            if (!doc.RootElement.TryGetProperty(StoreLit.BookSubsteps, out var subs) ||
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

        var revs = LoadNestedObjectDict(merged, "character_revisions");
        var prevRev = ReadNestedIntProperty(revs, charKey, "revision");

        revs[charKey] = new Dictionary<string, object?>
        {
            ["revision"] = prevRev + 1,
            ["updated_at"] = DateTime.Now.ToString(StoreLit.IsoDateTime),
            ["reason"] = reason,
        };
        merged["character_revisions"] = revs;
        merged["characters_designed"] = true;

        var json = JsonSerializer.Serialize(merged, JsonDefaults.Indented);
        File.WriteAllText(path, json + "\n");
    }

    private static Dictionary<string, object?> LoadNestedObjectDict(
        Dictionary<string, object?> merged, string key)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!merged.TryGetValue(key, out var crObj) || crObj is null)
            return result;
        try
        {
            using var crDoc = JsonDocument.Parse(JsonSerializer.Serialize(crObj));
            if (crDoc.RootElement.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var p in crDoc.RootElement.EnumerateObject())
                result[p.Name] = p.Value.Deserialize<object>();
        }
        catch { /* ignore */ }
        return result;
    }

    private static int ReadNestedIntProperty(
        Dictionary<string, object?> revs, string charKey, string property)
    {
        if (!revs.TryGetValue(charKey, out var prev) || prev is null)
            return 0;
        try
        {
            using var prevDoc = JsonDocument.Parse(JsonSerializer.Serialize(prev));
            if (prevDoc.RootElement.TryGetProperty(property, out var r) && r.TryGetInt32(out var rv))
                return rv;
        }
        catch { /* ignore */ }
        return 0;
    }

    public string? ResolveCharacterVariantPath(string projectId, string charKey, int variantIndex)
    {
        if (variantIndex is < 1 or > 3)
            return null;
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(info))
            return null;
        var fileName = $"{charKey.ToLowerInvariant()}_variant_0{variantIndex}.png";
        var full = Path.Combine(GetProjectDir(projectId), StoreLit.Assets, StoreLit.Characters, fileName);
        return File.Exists(full) && new FileInfo(full).Length >= 64 ? full : null;
    }

    public string? ResolveCharacterBookRefPath(string projectId, string charKey, int bookIndex)
    {
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(info))
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
        var byName = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Characters, Path.GetFileName(rel));
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
            AddUniquePlatePaths(arr, bookRefs);
            if (bookRefs.Count > 0)
                break; // prefer design_reference_images when present
        }
        return bookRefs;
    }

    private static void AddUniquePlatePaths(JsonElement arr, List<string> bookRefs)
    {
        foreach (var x in arr.EnumerateArray())
        {
            var s = x.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (IsTextOnlyPlatePath(s)) continue;
            if (!bookRefs.Contains(s, StringComparer.OrdinalIgnoreCase))
                bookRefs.Add(s);
        }
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
        if (ReadJsonBool(s, StoreLit.IsCredits))
            return true;
        if (s.TryGetProperty(StoreLit.Setting, out var set) &&
            (set.GetString() ?? "").Contains(StoreLit.Credits, StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.TryGetProperty("scene_heading", out var sh) &&
            (sh.GetString() ?? "").Contains(StoreLit.Credits, StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.TryGetProperty(StoreLit.VeoClips, out var clips) && clips.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clips.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && ReadJsonBool(c, StoreLit.IsCredits))
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
        if (DictBoolTrue(scene, StoreLit.IsCredits)) return true;
        if (DictContains(scene, StoreLit.Setting, StoreLit.Credits)) return true;
        if (DictContains(scene, "scene_heading", StoreLit.Credits)) return true;
        if (scene.TryGetValue(StoreLit.VeoClips, out var clipsObj) && clipsObj is IEnumerable<object?> clips)
        {
            foreach (var c in clips)
                if (c is IReadOnlyDictionary<string, object?> cd && DictBoolTrue(cd, StoreLit.IsCredits))
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
                !bp.RootElement.TryGetProperty(StoreLit.Scenes, out var scenesEl) ||
                scenesEl.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SceneSummary>();
            }

            var ctx = await BuildSceneListContextAsync(projectId, ct).ConfigureAwait(false);
            var rows = new List<SceneSummary>();
            foreach (var s in scenesEl.EnumerateArray())
            {
                var row = await TryBuildSceneSummaryAsync(projectId, s, ctx, probeDurations, ct).ConfigureAwait(false);
                if (row is not null)
                    rows.Add(row);
            }

            var ordered = rows.OrderBy(r => r.SceneNumber).ToList();
            try
            {
                var draftPath = ScreenplayService.GetDraftPath(this, projectId);
                var fountain = File.Exists(draftPath)
                    ? await File.ReadAllTextAsync(draftPath, ct).ConfigureAwait(false)
                    : "";
                ScreenplayService.ApplyIncomingJoins(ordered, fountain);
            }
            catch
            {
                // Join attach is advisory — never block the scene list.
            }

            return ordered;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private sealed class SceneListContext
    {
        public required Dictionary<string, long> VideoIndex { get; init; }
        public required SceneMediaPresenceIndex MediaPresence { get; init; }
        public required Dictionary<string, long> ScenesIndex { get; init; }
        public HashSet<string>? ApprovedScenes { get; init; }
        public required HashSet<int> MusicScenes { get; init; }
    }

    private async Task<SceneListContext> BuildSceneListContextAsync(string projectId, CancellationToken ct)
    {
        var projectDir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var videoDir = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Video);
        var scenesDir = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Scenes);
        var videoIndex = await GetVideoIndexWithParentFallbackAsync(projectId, videoDir, ct).ConfigureAwait(false);
        return new SceneListContext
        {
            VideoIndex = videoIndex,
            MediaPresence = new SceneMediaPresenceIndex(videoIndex),
            ScenesIndex = await GetDirIndexAsync(scenesDir, ct).ConfigureAwait(false),
            ApprovedScenes = await LoadApprovedSceneKeysAsync(projectDir, ct).ConfigureAwait(false),
            MusicScenes = await LoadMusicSceneNumbersAsync(projectId, ct).ConfigureAwait(false),
        };
    }

    private static async Task<HashSet<string>?> LoadApprovedSceneKeysAsync(string projectDir, CancellationToken ct)
    {
        var stateFile = Path.Combine(projectDir, "pipeline_state.json");
        if (!File.Exists(stateFile))
            return null;
        try
        {
            var stateText = await File.ReadAllTextAsync(stateFile, ct).ConfigureAwait(false);
            using var stateDoc = JsonDocument.Parse(stateText);
            if (!stateDoc.RootElement.TryGetProperty("scene_review", out var sr) ||
                sr.ValueKind != JsonValueKind.Object)
                return null;
            var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in sr.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("status", out var stEl) &&
                    string.Equals(stEl.GetString(), "approved", StringComparison.OrdinalIgnoreCase))
                {
                    approved.Add(prop.Name);
                }
            }
            return approved;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HashSet<int>> LoadMusicSceneNumbersAsync(string projectId, CancellationToken ct)
    {
        var musicScenes = new HashSet<int>();
        if (_mediaRegistry is null)
            return musicScenes;
        foreach (var mo in await _mediaRegistry.ListProjectAsync(projectId, ct).ConfigureAwait(false))
        {
            if (string.Equals(mo.Kind, StoreLit.Music, StringComparison.OrdinalIgnoreCase) && mo.Scene is int msc)
                musicScenes.Add(msc);
        }
        return musicScenes;
    }

    private async Task<SceneSummary?> TryBuildSceneSummaryAsync(
        string projectId,
        JsonElement s,
        SceneListContext ctx,
        bool probeDurations,
        CancellationToken ct)
    {
        if (!s.TryGetProperty(JsonKeys.SceneNumber, out var snEl) || !snEl.TryGetInt32(out var sn))
            return null;

        var clips = SceneClipElements(s);
        var nClips = clips.Count;
        var onDisk = CountClipsOnDisk(ctx.MediaPresence, sn, clips);
        var complete = nClips > 0 && onDisk >= nClips;
        var missingServerVideo = ListClipsMissingServerVideo(ctx.MediaPresence, sn, clips);
        var (locs, primaryLoc) = CollectSceneLocations(s);
        var staleClipCount = CountStaleClips(projectId, s, sn, onDisk, ctx.MediaPresence);

        var planned = ReadOptionalDuration(s, "total_estimated_duration_seconds");
        var actual = await ProbeSceneActualDurationAsync(projectId, sn, clips, probeDurations, ct).ConfigureAwait(false);
        return new SceneSummary
        {
            SceneNumber = sn,
            Setting = SceneSettingLabel(s),
            IsCredits = IsCreditsScene(s),
            ClipCount = nClips,
            ClipsOnDisk = onDisk,
            ClipsComplete = complete,
            ClipsMissingServerVideo = missingServerVideo,
            StaleClipCount = staleClipCount,
            HasStaleClips = staleClipCount > 0,
            PlannedDurationSeconds = planned,
            ActualDurationSeconds = actual,
            DurationSeconds = actual ?? planned,
            CompositeExists = HasCompositeFile(ctx.VideoIndex, ctx.ScenesIndex, sn),
            CharactersOnScreen = CollectSceneCharacters(s, clips),
            LocationIds = locs,
            PrimaryLocationId = primaryLoc,
            PrimaryLocationLocked = !string.IsNullOrWhiteSpace(primaryLoc) &&
                ResolveLocationRefPath(projectId, primaryLoc) is not null,
            Status = SceneStatusLabel(nClips, onDisk, complete),
            IsApproved = ctx.ApprovedScenes?.Contains($"S{sn:D2}") == true,
            HasBackgroundMusic = ctx.MusicScenes.Contains(sn),
        };
    }

    private static List<JsonElement> SceneClipElements(JsonElement s) =>
        s.TryGetProperty(StoreLit.VeoClips, out var vc) && vc.ValueKind == JsonValueKind.Array
            ? vc.EnumerateArray().ToList()
            : new List<JsonElement>();

    private static int CountClipsOnDisk(SceneMediaPresenceIndex mediaPresence, int sn, List<JsonElement> clips)
    {
        var onDisk = 0;
        foreach (var c in clips)
        {
            var cn = ClipKeying.ClipNumber(c);
            if (cn <= 0) continue;
            if (mediaPresence.IsPresent(sn, cn))
                onDisk++;
        }
        return onDisk;
    }

    private static List<int> ListClipsMissingServerVideo(
        SceneMediaPresenceIndex mediaPresence, int sn, List<JsonElement> clips)
    {
        var planned = new List<int>();
        foreach (var c in clips)
        {
            var cn = ClipKeying.ClipNumber(c);
            if (cn > 0)
                planned.Add(cn);
        }
        return planned.Distinct().Where(cn => !mediaPresence.HasServerMp4(sn, cn)).OrderBy(x => x).ToList();
    }

    private static double? ReadOptionalDuration(JsonElement s, string name)
    {
        if (!s.TryGetProperty(name, out var dEl))
            return null;
        if (dEl.TryGetDouble(out var dd)) return dd;
        if (dEl.TryGetInt32(out var di)) return di;
        return null;
    }

    private async Task<double?> ProbeSceneActualDurationAsync(
        string projectId, int sn, List<JsonElement> clips, bool probeDurations, CancellationToken ct)
    {
        if (!probeDurations || _duration is null)
            return null;
        var compositePath = ResolveCompositePath(projectId, sn);
        var clipPaths = new List<string>();
        foreach (var c in clips)
        {
            var cn = ClipKeying.ClipNumber(c);
            if (cn <= 0) continue;
            var cp = ResolveClipVideoPath(projectId, sn, cn);
            if (cp is not null) clipPaths.Add(cp);
        }
        return await _duration.GetSceneActualDurationSecondsAsync(compositePath, clipPaths, ct).ConfigureAwait(false);
    }

    private static List<string> CollectSceneCharacters(JsonElement s, List<JsonElement> clips)
    {
        var chars = new List<string>();
        AddUniqueJsonStrings(s, StoreLit.CharactersOnScreen, chars);
        // Scene-level characters_on_screen can lag clip-level casts (e.g. a character who
        // only appears mid-scene in specific clips) — union in each clip's own list too.
        foreach (var c in clips)
            AddUniqueJsonStrings(c, StoreLit.CharactersOnScreen, chars);
        return chars;
    }

    private static void AddUniqueJsonStrings(JsonElement el, string prop, List<string> into)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var x in arr.EnumerateArray())
        {
            var name = x.GetString();
            if (!string.IsNullOrWhiteSpace(name) && !into.Contains(name, StringComparer.OrdinalIgnoreCase))
                into.Add(name);
        }
    }

    private static (List<string> Locs, string? Primary) CollectSceneLocations(JsonElement s)
    {
        var locs = JsonStringList(s, StoreLit.LocationIds);
        string? primaryLoc = null;
        if (s.TryGetProperty(StoreLit.PrimaryLocationId, out var pl) &&
            pl.GetString() is { Length: > 0 } plId)
        {
            primaryLoc = plId;
            if (!locs.Contains(plId, StringComparer.OrdinalIgnoreCase))
                locs.Insert(0, plId);
        }
        return (locs, primaryLoc);
    }

    private static List<string> JsonStringList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var x in arr.EnumerateArray())
        {
            var s = x.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
        return list;
    }

    private static string SceneSettingLabel(JsonElement s)
    {
        var settingText = JsonStr(s, StoreLit.Setting);
        var headingText = JsonStr(s, "scene_heading");
        var isCredits = IsCreditsScene(s);
        // Credits scenes carry a scene_heading, not a setting — show a clear label instead of a blank cell.
        if (!string.IsNullOrWhiteSpace(settingText))
            return settingText;
        return isCredits ? "END CREDITS" : headingText;
    }

    private static string SceneStatusLabel(int nClips, int onDisk, bool complete)
    {
        if (nClips == 0 || onDisk == 0)
            return "empty";
        return complete ? "complete" : "partial";
    }

    private int CountStaleClips(
        string projectId, JsonElement s, int sn, int onDisk, SceneMediaPresenceIndex mediaPresence)
    {
        try
        {
            var bpPathForStale = FindBlueprintPathSync(projectId);
            if (onDisk <= 0 || bpPathForStale is null || !File.Exists(bpPathForStale)
                || !s.TryGetProperty(StoreLit.Clips, out var clipsElStale) || clipsElStale.ValueKind != JsonValueKind.Array)
                return 0;
            var bpM = File.GetLastWriteTimeUtc(bpPathForStale);
            var staleClipCount = 0;
            foreach (var cEl in clipsElStale.EnumerateArray())
            {
                if (!cEl.TryGetProperty(JsonKeys.ClipNumber, out var cnEl) || !cnEl.TryGetInt32(out var cn2))
                    continue;
                if (!mediaPresence.IsPresent(sn, cn2)) continue;
                var path = ResolveClipVideoPath(projectId, sn, cn2);
                if (path is null || !File.Exists(path)) continue;
                if (bpM > File.GetLastWriteTimeUtc(path).AddSeconds(2))
                    staleClipCount++;
            }
            return staleClipCount;
        }
        catch
        {
            return 0;
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
            var sceneEl = FindSceneElement(bp.RootElement, sceneNumber);
            if (sceneEl is null)
                return null;
            return await BuildSceneDetailAsync(projectId, sceneNumber, sceneEl.Value, probeDurations, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private async Task<SceneDetail> BuildSceneDetailAsync(
        string projectId,
        int sceneNumber,
        JsonElement sEl,
        bool probeDurations,
        CancellationToken ct)
    {
        var projectDir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var videoDir = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Video);
        var scenesDir = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Scenes);
        var videoIndex = await GetVideoIndexWithParentFallbackAsync(projectId, videoDir, ct).ConfigureAwait(false);
        var scenesIndex = await GetDirIndexAsync(scenesDir, ct).ConfigureAwait(false);

        var (clips, duplicateClipNumbers) = await CollectSceneClipSummariesAsync(
            projectId, sceneNumber, projectDir, sEl, videoIndex, probeDurations, ct).ConfigureAwait(false);
        clips = clips.OrderBy(c => c.ClipNumber).ToList();

        var planned = ReadOptionalDuration(sEl, "total_estimated_duration_seconds");
        var actual = await ProbeSceneDetailActualAsync(projectId, sceneNumber, clips, probeDurations, ct)
            .ConfigureAwait(false);
        var compositeOk = HasCompositeFile(videoIndex, scenesIndex, sceneNumber);
        var hasMusic = _mediaRegistry is not null &&
            await _mediaRegistry.HasSceneMusicAsync(projectId, sceneNumber, ct).ConfigureAwait(false);

        var detail = new SceneDetail
        {
            SceneNumber = sceneNumber,
            Setting = JsonStr(sEl, StoreLit.Setting),
            PlannedDurationSeconds = planned,
            ActualDurationSeconds = actual,
            DurationSeconds = actual ?? planned,
            ClipCount = clips.Count,
            ClipsOnDisk = clips.Count(c => c.OnDisk),
            CompositeExists = compositeOk,
            HasBackgroundMusic = hasMusic,
            MusicScore = ReadMusicScore(sEl),
            CompositeUrl = compositeOk
                ? $"{ProjectIdRouting.ProjectApi(projectId)}/scenes/{sceneNumber}/composite"
                : null,
            CharactersOnScreen = JsonStringList(sEl, StoreLit.CharactersOnScreen),
            LocationIds = JsonStringList(sEl, StoreLit.LocationIds),
            PrimaryLocationId = JsonStrOrNull(sEl, StoreLit.PrimaryLocationId),
            PrimaryLocationLocked = false,
            Clips = clips,
            DuplicateClipNumbers = duplicateClipNumbers,
        };
        if (!string.IsNullOrWhiteSpace(detail.PrimaryLocationId))
            detail.PrimaryLocationLocked =
                ResolveLocationRefPath(projectId, detail.PrimaryLocationId) is not null;
        return detail;
    }

    private async Task<double?> ProbeSceneDetailActualAsync(
        string projectId, int sceneNumber, List<ClipSummary> clips, bool probeDurations, CancellationToken ct)
    {
        if (!probeDurations || _duration is null)
            return null;
        var compositePath = ResolveCompositePath(projectId, sceneNumber);
        var clipPaths = clips
            .Where(c => c.OnDisk)
            .Select(c => ResolveClipVideoPath(projectId, sceneNumber, c.ClipNumber))
            .OfType<string>();
        return await _duration.GetSceneActualDurationSecondsAsync(compositePath, clipPaths, ct).ConfigureAwait(false);
    }

    private static MusicScoreInfo? ReadMusicScore(JsonElement sEl)
    {
        if (sEl.TryGetProperty("music_score", out var msEl) && msEl.ValueKind == JsonValueKind.Object)
        {
            return new MusicScoreInfo
            {
                Prompt = JsonStr(msEl, "prompt"),
                Genre = JsonStrOrNull(msEl, "genre"),
                Mood = JsonStrOrNull(msEl, "mood"),
                Tempo = JsonStrOrNull(msEl, "tempo"),
            };
        }
        if (sEl.TryGetProperty("music_prompt", out var mpEl) && mpEl.ValueKind == JsonValueKind.String)
            return new MusicScoreInfo { Prompt = mpEl.GetString() ?? "" };
        return null;
    }

    private async Task<(List<ClipSummary> Clips, List<int> Duplicates)> CollectSceneClipSummariesAsync(
        string projectId,
        int sceneNumber,
        string projectDir,
        JsonElement sEl,
        Dictionary<string, long> videoIndex,
        bool probeDurations,
        CancellationToken ct)
    {
        var mediaPresence = new SceneMediaPresenceIndex(videoIndex);
        var clips = new List<ClipSummary>();
        var duplicateClipNumbers = new List<int>();
        var seenClipNumbers = new HashSet<int>();
        if (!sEl.TryGetProperty(StoreLit.VeoClips, out var vc) || vc.ValueKind != JsonValueKind.Array)
            return (clips, duplicateClipNumbers);

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

            clips.Add(await BuildClipSummaryAsync(
                projectId, sceneNumber, projectDir, c, cn, videoIndex, mediaPresence, probeDurations, ct).ConfigureAwait(false));
        }
        return (clips, duplicateClipNumbers);
    }

    private async Task<ClipSummary> BuildClipSummaryAsync(
        string projectId,
        int sceneNumber,
        string projectDir,
        JsonElement c,
        int cn,
        Dictionary<string, long> videoIndex,
        SceneMediaPresenceIndex mediaPresence,
        bool probeDurations,
        CancellationToken ct)
    {
        var fileName = $"scene_{sceneNumber:D2}_clip_{cn:D2}.mp4";
        var onDisk = mediaPresence.IsPresent(sceneNumber, cn);
        var size = ResolveClipSizeOnDisk(videoIndex, fileName, sceneNumber, cn, onDisk);
        var audio = ParseClipAudio(c);
        var dur = 0;
        if (c.TryGetProperty(StoreLit.DurationSeconds, out var dEl) && dEl.TryGetInt32(out var ds))
            dur = ds;

        var clipPath = onDisk ? ResolveClipVideoPath(projectId, sceneNumber, cn) : null;
        var resolvedFileName = clipPath is not null ? Path.GetFileName(clipPath) : fileName;
        double? actualClip = null;
        if (probeDurations && onDisk && _duration is not null && clipPath is not null)
            actualClip = await _duration.GetDurationSecondsAsync(clipPath, ct).ConfigureAwait(false);

        var visualPrompt = ClipVideoPromptBuilder.SanitizeSpokenQuotesInVisual(JsonStr(c, StoreLit.VisualPrompt));
        var (stage1BeatId, stage1BeatIds) = ResolveStage1BeatIds(c, sceneNumber, audio.Dialogue, audio.Speaker, visualPrompt);
        var dialogueVer = await LoadClipDialogueVerificationAsync(projectDir, sceneNumber, cn, ct).ConfigureAwait(false);
        var (isStale, staleReason) = EvaluateClipStale(
            onDisk, clipPath, dialogueVer, FindBlueprintPathSync(projectId));
        // Plan lint: the plan text contradicts cast facts (e.g. a voice-only role on screen). Surfaced,
        // not patched — the fix is the planner rule + a rebuild, and the clip says so.
        var lint = ShotPlanLint.Check(c, ShotPlanLint.VoiceOnlyKeys(LoadCharacterSeeds(projectId)));
        if (lint.Count > 0 && !isStale)
        {
            isStale = onDisk;
            staleReason = "plan_lint: " + string.Join("; ", lint.Select(f => f.Message));
        }

        return new ClipSummary
        {
            ClipNumber = cn,
            Timestamp = JsonStr(c, "timestamp"),
            DurationSeconds = dur,
            ActualDurationSeconds = actualClip,
            Continuation = JsonStr(c, "veo_continuation_source", "none"),
            PrimarySubject = JsonStr(c, "primary_subject"),
            VisualPrompt = visualPrompt,
            NegativePrompt = JsonStr(c, "negative_prompt"),
            Dialogue = audio.Dialogue,
            Speaker = audio.Speaker,
            Delivery = audio.Delivery,
            SecondarySpeaker = audio.SecondarySpeaker,
            SecondaryDialogue = string.IsNullOrWhiteSpace(audio.SecondaryDialogue)
                ? audio.SecondaryDialogue
                : ClipVideoPromptBuilder.SanitizeSpokenDialogue(audio.SecondaryDialogue),
            PronunciationHint = audio.PronunciationHint,
            CharactersOnScreen = JsonStringList(c, StoreLit.CharactersOnScreen),
            ColorPalette = JsonStrOrNull(c, "color_palette"),
            FilmStock = JsonStrOrNull(c, "film_stock"),
            OnDisk = onDisk,
            SizeBytes = size,
            FileName = onDisk ? resolvedFileName : null,
            ProviderLeadInSeconds = ResolveProviderLeadInSeconds(onDisk, clipPath, projectDir, sceneNumber, cn),
            VideoUrl = onDisk
                ? $"{ProjectIdRouting.ProjectApi(projectId)}/scenes/{sceneNumber}/clips/{cn}/video"
                : null,
            DialogueVerification = dialogueVer,
            Stage1BeatId = stage1BeatId,
            Stage1BeatIds = stage1BeatIds,
            IsStale = isStale,
            StaleReason = staleReason,
            PlanLint = lint.Select(f => f.Message).ToList(),
        };
    }

    private static double? ResolveProviderLeadInSeconds(
        bool onDisk, string? clipPath, string projectDir, int sceneNumber, int cn)
    {
        if (!onDisk || clipPath is not null)
            return null;
        var ps = ClipProviderSource.ReadForClip(
            Path.Combine(projectDir, StoreLit.Assets, StoreLit.Video), sceneNumber, cn);
        if (ps is { IsCombined: true })
            return ps.LeadInSeconds;
        return null;
    }

    private static long ResolveClipSizeOnDisk(
        Dictionary<string, long> videoIndex, string fileName, int sceneNumber, int cn, bool onDisk)
    {
        if (!onDisk)
            return 0;
        if (videoIndex.TryGetValue(fileName, out var sz))
            return sz;
        var prefix = $"scene_{sceneNumber:D2}_clip_{cn:D2}_take_";
        var takeMatch = videoIndex.FirstOrDefault(kv =>
            kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            kv.Key.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(takeMatch.Key) ? 0 : takeMatch.Value;
    }

    private readonly record struct ClipAudioFields(
        string Dialogue,
        string? Speaker,
        string? Delivery,
        string? PronunciationHint,
        string? SecondarySpeaker,
        string? SecondaryDialogue);

    private static ClipAudioFields ParseClipAudio(JsonElement c)
    {
        var dialogue = "";
        string? speaker = null;
        string? delivery = null;
        string? pronunciationHint = null;
        string? secondarySpeaker = null;
        string? secondaryDialogue = null;
        if (c.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object)
            ApplyAudioPayload(ap, ref dialogue, ref speaker, ref delivery, ref pronunciationHint, ref secondarySpeaker, ref secondaryDialogue);
        FillClipAudioFallbacks(c, ref dialogue, ref speaker, ref delivery, ref pronunciationHint);
        dialogue = ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue);
        return new ClipAudioFields(dialogue, speaker, delivery, pronunciationHint, secondarySpeaker, secondaryDialogue);
    }

    private static void ApplyAudioPayload(
        JsonElement ap,
        ref string dialogue,
        ref string? speaker,
        ref string? delivery,
        ref string? pronunciationHint,
        ref string? secondarySpeaker,
        ref string? secondaryDialogue)
    {
        if (ap.TryGetProperty(JsonKeys.Dialogue, out var d))
            dialogue = d.GetString() ?? "";
        if (ap.TryGetProperty(JsonKeys.Speaker, out var sp))
            speaker = sp.GetString();
        if (ap.TryGetProperty(StoreLit.Delivery, out var del))
            delivery = del.GetString();
        if (ap.TryGetProperty("pronunciation_hint", out var ph))
            pronunciationHint = ph.GetString();
        if (ap.TryGetProperty("secondary_speaker", out var ssp))
            secondarySpeaker = ssp.GetString();
        if (ap.TryGetProperty("secondary_dialogue", out var sdlg))
            secondaryDialogue = sdlg.GetString();
    }

    private static void FillClipAudioFallbacks(
        JsonElement c,
        ref string dialogue,
        ref string? speaker,
        ref string? delivery,
        ref string? pronunciationHint)
    {
        if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty(JsonKeys.Dialogue, out var rootD))
            dialogue = rootD.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty(StoreLit.AudioScript, out var rootAS))
            dialogue = rootAS.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(speaker) && c.TryGetProperty(JsonKeys.Speaker, out var rootSp))
            speaker = rootSp.GetString();
        if (string.IsNullOrWhiteSpace(delivery) && c.TryGetProperty(StoreLit.Delivery, out var rootDel))
            delivery = rootDel.GetString();
        if (string.IsNullOrWhiteSpace(pronunciationHint) && c.TryGetProperty("pronunciation_hint", out var rootPh))
            pronunciationHint = rootPh.GetString();
    }

    private static (string? BeatId, List<string> BeatIds) ResolveStage1BeatIds(
        JsonElement c, int sceneNumber, string dialogue, string? speaker, string visualPrompt)
    {
        var stage1BeatId = JsonStrOrNull(c, "stage1_beat_id");
        var stage1BeatIds = c.TryGetProperty("stage1_beat_ids", out var s1bs) &&
                            s1bs.ValueKind == JsonValueKind.Array
            ? s1bs.EnumerateArray()
                .Select(x => x.GetString())
                .OfType<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList()
            : new List<string>();
        // E3 write-through: fill stable beat id from content when blueprint lacks it
        if (string.IsNullOrWhiteSpace(stage1BeatId) &&
            (!string.IsNullOrWhiteSpace(dialogue) || !string.IsNullOrWhiteSpace(visualPrompt)))
        {
            var kind = string.IsNullOrWhiteSpace(dialogue) ? "action" : JsonKeys.Dialogue;
            stage1BeatId = PageToMovie.Core.Utils.StableBeatId.ForContent(
                $"S{sceneNumber:D2}", kind, speaker, string.IsNullOrWhiteSpace(dialogue) ? visualPrompt : dialogue);
        }
        if (stage1BeatIds.Count == 0 && !string.IsNullOrWhiteSpace(stage1BeatId))
            stage1BeatIds.Add(stage1BeatId);
        return (stage1BeatId, stage1BeatIds);
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
            StoreLit.Assets,
            StoreLit.Video);

        if (!Directory.Exists(videoDir)) return null;

        var current = ClipSidecarService.CurrentTakePath(videoDir, sceneNumber, clipNumber);
        if (current is not null && File.Exists(current) && new FileInfo(current).Length >= 1024)
            return current;

        // No pointer (or take bytes not on this machine): newest take_NN file.
        // Leftover bare aliases are ignored when a take file exists.
        var takePrefix = ClipTakeNaming.SceneClipPrefix(sceneNumber, clipNumber) + "_take_";
        var latestTake = new DirectoryInfo(videoDir)
            .EnumerateFiles($"{takePrefix}*.mp4")
            .Where(fi => fi.Length >= 1024 && !fi.Name.StartsWith('_'))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault();

        return latestTake?.FullName;
    }

    /// <summary>
    /// xAI Files <c>file_id</c> from the clip sidecar, even when the .mp4 was never copied
    /// (forks skip video). Null if no sidecar or no id.
    /// </summary>
    public string? TryReadClipSourceFileId(string projectId, int sceneNumber, int clipNumber)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        try
        {
            var videoDir = Path.Combine(GetProjectDir(projectId), StoreLit.Assets, StoreLit.Video);
            if (!Directory.Exists(videoDir)) return null;
            var prefix = $"scene_{sceneNumber:D2}_clip_{clipNumber:D2}";
            var exact = Path.Combine(videoDir, prefix + StoreLit.ClipJsonSuffix);
            if (File.Exists(exact) && TrySidecarFileId(exact, out var id))
                return id;
            foreach (var sidecar in Directory.EnumerateFiles(videoDir, prefix + "*" + StoreLit.ClipJsonSuffix))
            {
                if (TrySidecarFileId(sidecar, out id))
                    return id;
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private static bool TrySidecarFileId(string sidecarPath, out string? fileId)
    {
        fileId = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            if (!doc.RootElement.TryGetProperty("source_file_id", out var el))
                return false;
            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s)) return false;
            fileId = s.Trim();
            return true;
        }
        catch
        {
            return false;
        }
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
        var path = Path.Combine(GetProjectDir(projectId), StoreLit.Assets, "movie_preview.mp4");
        return File.Exists(path) && new FileInfo(path).Length >= 1024 ? path : null;
    }

    /// <summary>Last successful YouTube upload for a project, or null if never uploaded.</summary>
    public async Task<YouTubeUploadInfo?> GetYouTubeUploadInfoAsync(string projectId, CancellationToken ct = default)
    {
        var path = Path.Combine(await GetProjectDirAsync(projectId, ct).ConfigureAwait(false), StoreLit.Assets, "youtube_upload.json");
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
        var dir = Path.Combine(await GetProjectDirAsync(projectId, ct).ConfigureAwait(false), StoreLit.Assets);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "youtube_upload.json"),
            JsonSerializer.Serialize(info, JsonOpts),
            ct).ConfigureAwait(false);
    }

    public string ResolveScenesJsonPath(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var preferred = StoreLit.ScenesJson;
        var metaPath = Path.Combine(dir, StoreLit.ProjectJson);
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("scenes_file", out var sf))
                {
                    var n = sf.GetString();
                    if (!string.IsNullOrWhiteSpace(n))
                        preferred = n;
                }
            }
            catch { /* ignore */ }
        }

        var full = Path.Combine(dir, preferred);
        if (File.Exists(full))
            return full;
        if (!string.Equals(preferred, StoreLit.ScenesJson, StringComparison.OrdinalIgnoreCase))
        {
            var standard = Path.Combine(dir, StoreLit.ScenesJson);
            if (File.Exists(standard))
                return standard;
        }
        return Path.Combine(dir, preferred);
    }

    /// <summary>
    /// True when this user has a personal studio key (BYOK). Server env keys do not count
    /// unless <see cref="PageToMovieOptions.AllowServerApiKeyFallback"/> is on.
    /// </summary>
    public async Task<bool> IsAnyStudioKeyConfiguredAsync(string? userId = null, CancellationToken ct = default)
    {
        // Fakes mode uses key-free fake providers, so a key is effectively always "configured".
        // Mirrors the /health endpoint (xaiConfigured = ... || useFakes) so the fully-faked
        // pipeline is self-sufficient for offline dev/testing. Real mode is unaffected — the
        // BYOK setup gate still requires a real key.
        if (_opts.UseFakes) return true;

        if (await HasAnyUserProviderKeyAsync(userId, ct).ConfigureAwait(false))
            return true;

        // Ambient scope only after personal keys were loaded into the request — still OK
        // because GetKey no longer injects server env for signed-in users under BYOK.
        if (HasAmbientStudioKey())
            return true;

        if (_opts.AllowServerApiKeyFallback &&
            await HasAnyServerFallbackKeyAsync(ct).ConfigureAwait(false))
            return true;

        return false;
    }

    private async Task<bool> HasAnyUserProviderKeyAsync(string? userId, CancellationToken ct)
    {
        if (_keyProvider is null || string.IsNullOrWhiteSpace(userId))
            return false;
        foreach (var provider in new[] { "grok", "gemini", "anthropic", "openai", "fal" })
        {
            if (await _keyProvider.HasKeyAsync(userId, provider, ct).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private static bool HasAmbientStudioKey() =>
        !string.IsNullOrWhiteSpace(ApiKeyScope.Current)
        || !string.IsNullOrWhiteSpace(ApiKeyScope.CurrentGemini)
        || !string.IsNullOrWhiteSpace(ApiKeyScope.CurrentAnthropic)
        || !string.IsNullOrWhiteSpace(ApiKeyScope.Get("openai"));

    private async Task<bool> HasAnyServerFallbackKeyAsync(CancellationToken ct)
    {
        if (HasServerEnvStudioKey())
            return true;
        if (_keyProvider is null)
            return false;
        foreach (var provider in new[] { "grok", "gemini", "anthropic", "fal", "openai" })
        {
            if (await _keyProvider.HasKeyAsync(null, provider, ct).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private static bool HasServerEnvStudioKey() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FAL_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FAL_KEY"));

    public async Task<AdaptationStatus> GetAdaptationStatusAsync(string projectId, string? userId = null, CancellationToken ct = default)
    {
        var dir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var book = ReadBookSourceStatus(projectId, dir);
        var stage1 = ReadStage1Status(projectId);
        var screenplay = ScreenplayService.ReadStatus(this, projectId, stage1);
        var stage2 = ReadStage2PlanStatus(projectId, stage1);
        // Fountain re-sign that changed Stage 1 makes an existing shot plan stale
        if (screenplay.DraftExists && screenplay.Dirty && stage2.Stage2Ready)
            stage2.Stage2Stale = true;
        // Any planning/gen key is enough for import→screenplay. Prefer ambient scope
        // (request middleware already loaded this user's personal keys), then provider
        // lookup for the real userId — never HasKey("grok") as a *user id* (old bug).
        var xai = await IsAnyStudioKeyConfiguredAsync(userId, ct).ConfigureAwait(false);

        var cfg = GetConfigSync(projectId);
        var planningModel = ReadPlanningModelName(cfg);

        var cast = ReadCastStatus(projectId);

        // Fountain is the screenplay source of truth.
        // Flow: import → draft/approve → pin characters → shot plan → generate clips (Scenes).
        var next = ResolveAdaptationNextStep(book, screenplay, stage1, stage2, cast, ProjectScreenplayIndex.TryReadSummary(dir));


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
            Index = ProjectScreenplayIndex.TryReadSummary(dir),
            Cut = ProjectScreenplayCut.TryReadSummary(dir),
        };
    }

    private static string ReadPlanningModelName(Dictionary<string, JsonElement> cfg) =>
        ReadConfigNonEmptyString(cfg, "planning_model_name")
        ?? ReadConfigNonEmptyString(cfg, "chat_model_name")
        ?? "";

    private static string? ReadConfigNonEmptyString(Dictionary<string, JsonElement> cfg, string key)
    {
        if (cfg.TryGetValue(key, out var el) &&
            el.ValueKind == JsonValueKind.String &&
            el.GetString() is { Length: > 0 } s)
            return s;
        return null;
    }

    private static string ResolveAdaptationNextStep(
        BookSourceStatus book,
        ScreenplayStatus screenplay,
        Stage1Status stage1,
        Stage2PlanStatus stage2,
        CastStatus cast,
        ScreenplayIndexSummary? index)
    {
        if (!HasAdaptationSource(book, screenplay, stage1))
            return "import_book";
        if (NeedsFixBookText(book, screenplay, stage1))
            return "fix_book_text";
        // Inherited max+index: pick a length before writing another screenplay.
        if (index is { HasIndex: true } && screenplay.DraftExists && !cast.ReadyForShots && !stage2.Stage2Ready)
            return "shape_runtime";
        if (!screenplay.DraftExists && book.BookTextExists)
            return "draft_screenplay";
        if (screenplay.DraftExists && (!screenplay.Signed || screenplay.Dirty))
            return "sign_screenplay";
        if (!stage1.Present || stage1.SceneCount == 0)
            return SignScreenplayOrImport(screenplay);
        if (!cast.ReadyForShots)
            return "pin_characters";
        if (!stage2.Stage2Ready)
            return "run_stage2";
        if (stage2.Stage2Stale)
            return "replan_stage2";
        return "generate_clips";
    }

    private static bool HasAdaptationSource(
        BookSourceStatus book, ScreenplayStatus screenplay, Stage1Status stage1) =>
        book.PdfExists || book.BookTextExists || screenplay.DraftExists ||
        (stage1.Present && stage1.SceneCount > 0);

    private static bool NeedsFixBookText(
        BookSourceStatus book, ScreenplayStatus screenplay, Stage1Status stage1) =>
        (!stage1.Present || stage1.SceneCount == 0) && book.BookTextExists && !book.ReadyForStage1 &&
        !screenplay.DraftExists;

    private static string SignScreenplayOrImport(ScreenplayStatus screenplay) =>
        screenplay.DraftExists ? "sign_screenplay" : "import_book";

    /// <summary>
    /// Cast is ready when every member has a voice profile and (if a single on-screen face)
    /// a <em>locked</em> ref image — not merely a variant draft (<see cref="CharacterSummary.HasPreferred"/>).
    /// <see cref="CharacterSummary.VoiceOnly"/> skips the locked-image requirement.
    /// <see cref="CharacterSummary.IsGroup"/> is ignored for readiness (hidden on Characters UI).
    /// G3: when project <c>production_mode=draft</c>, locked plates are optional (first-watch soft gate).
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

            var platesOptional = IsDraftProductionMode(projectId);
            var ready = 0;
            var missing = new List<string>();
            foreach (var c in rows)
            {
                if (!c.UsedInPlan)
                    continue;
                if (CharacterCountsAsReady(c, platesOptional))
                    ready++;
                else
                    missing.Add(c.Key);
            }

            status.Ready = ready;
            status.Missing = missing;
            var usedCount = rows.Count(c => c.UsedInPlan);
            if (usedCount > 0 && usedCount < rows.Count)
                status.Total = usedCount;
            status.ReadyForShots = missing.Count == 0 && status.Total > 0;
        }
        catch
        {
            // leave defaults
        }

        return status;
    }

    private static bool IsSilentNonHumanWithoutVoice(CharacterSummary c)
    {
        var isNonHuman = c.SpeciesKind is { Length: > 0 } sk
            && !sk.Trim().Equals("human", StringComparison.OrdinalIgnoreCase);
        return isNonHuman && !c.Speaks && string.IsNullOrWhiteSpace(c.VoiceProfile);
    }

    private static bool CharacterCountsAsReady(CharacterSummary c, bool platesOptional)
    {
        var hasVoice = VoiceUsable(c);
        // Voice-only: need voice profile, no portrait.
        // Group/chorus: production extras — not shown on Characters UI; never block readiness.
        if (c.IsGroup)
            return true;
        if (c.VoiceOnly)
            return hasVoice;

        // A SILENT non-human seed (animal that never speaks) does NOT require a voice — only a locked
        // image if it appears on screen. A talking animal (has dialogue) is a speaking role and needs
        // a voice like any speaker, so it falls through below. Mirrors GetCastNotReadyForVideo so this
        // readiness gate (Scenes "Cast incomplete" banner + Generate button) agrees with the spend gate.
        if (IsSilentNonHumanWithoutVoice(c))
            return c.Locked || platesOptional;

        // Locked only — HasPreferred can be unlocked variant_01 and is not enough to spend on video.
        // G3 draft: require voice (when speaking) but plates optional.
        if (platesOptional)
            return hasVoice;
        return c.Locked && hasVoice;
    }

    /// <summary>G3/G4 — true when project production_mode is draft (plates optional).</summary>
    public bool IsDraftProductionMode(string projectId)
    {
        try
        {
            return ProductionModes.IsDraftConfig(GetConfigSync(projectId));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Project-wide cast gate before any video spend: every <em>speaking</em> seed needs a voice profile;
    /// every single on-screen face needs a locked ref image (not just a variant draft).
    /// <see cref="CharacterSummary.VoiceOnly"/> skips the locked-image requirement.
    /// <see cref="CharacterSummary.IsGroup"/> never blocks (not shown for operator pin).
    /// A non-speaking seed (no voice profile, no voice label, no clone sample — e.g. an animal like the
    /// Lamb) does NOT require a voice; it still needs a locked image if it appears on screen.
    /// G3: when <c>production_mode=draft</c>, locked plates are optional (voice still required for speakers).
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

        var platesOptional = IsDraftProductionMode(projectId);

        foreach (var c in rows)
        {
            var reason = CastNotReadyReason(c, platesOptional);
            if (reason is not null)
                missing.Add(reason);
        }

        return missing;
    }

    private static string? CastNotReadyReason(CharacterSummary c, bool platesOptional)
    {
        // Don't require plates/voice for seeds that never appear in the shot plan.
        // Groups are not operator-pinned; never block video gen.
        if (!c.UsedInPlan || c.IsGroup)
            return null;
        if (c.VoiceOnly)
            return VoiceProfileMissingReason(c);
        // A SILENT non-human seed (animal that never speaks) does NOT require a voice — only a locked
        // image if it appears on screen. A talking animal (has dialogue) is a speaking role and needs a
        // voice like any speaker, so it falls through to the voice+image requirement below.
        if (IsSilentNonHumanWithoutVoice(c))
            return LockedImageMissingReason(c, platesOptional);
        if (platesOptional)
            return VoiceProfileMissingReason(c);
        return FullModeCastNotReadyReason(c);
    }

    // "Has a voice" = a profile that pins sex + age (VoiceProfileGuard.IsLocked). A sexless/ageless
    // profile lets each clip re-cast the voice (Mary19 narrator, 2026-08-19) — same as no voice.
    private static string? VoiceProfileMissingReason(CharacterSummary c) =>
        VoiceUnlockedReason(c) is { } why ? $"{c.Key}: {why}" : null;

    /// <summary>Null when the role's voice is usable for video. A SPEAKING role (or a voice-only role,
    /// which exists to speak) needs the sex+age
    /// lock (its profile is the cross-clip voice identity); a role with no lines only needs some
    /// profile text ("does not speak; soft breath") — there is no voice to keep consistent.</summary>
    private static string? VoiceUnlockedReason(CharacterSummary c)
    {
        if (c.Speaks || c.VoiceOnly)
            return VoiceProfileGuard.UnlockedReason(c.VoiceProfile);
        if (string.IsNullOrWhiteSpace(c.VoiceProfile))
            return "voice profile";
        return null;
    }

    private static bool VoiceUsable(CharacterSummary c) => VoiceUnlockedReason(c) is null;

    private static string? LockedImageMissingReason(CharacterSummary c, bool platesOptional) =>
        c.Locked || platesOptional ? null : $"{c.Key}: locked image";

    private static string? FullModeCastNotReadyReason(CharacterSummary c)
    {
        var voiceWhy = VoiceUnlockedReason(c);
        var hasVoice = voiceWhy is null;
        if (!hasVoice && !c.Locked)
            return $"{c.Key}: {voiceWhy} + locked image";
        if (!hasVoice)
            return $"{c.Key}: {voiceWhy}";
        if (!c.Locked)
            return $"{c.Key}: locked image";
        return null;
    }

    public async Task<string> SaveBookUploadAsync(
        string projectId,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        var dir = await GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var source = Path.Combine(dir, StoreLit.Source);
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
            // Plain-text uploads skip BookPrepare — strip Project Gutenberg legal preamble
            // here so book_full.txt / xAI file_id never carry the license block.
            string text;
            try
            {
                text = Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                text = Encoding.Latin1.GetString(bytes);
            }
            // Drop UTF-8 BOM if present
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];
            if (GutenbergCleaner.HasGutenbergHeader(text))
            {
                text = GutenbergCleaner.StripHeaderAndFooter(text);
            }
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";
            await File.WriteAllTextAsync(bookFull, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            return bookFull;
        }

        var dest = Path.Combine(source, safe);
        await File.WriteAllBytesAsync(dest, bytes, ct);
        return dest;
    }

    private BookSourceStatus ReadBookSourceStatus(string projectId, string projectDir)
    {
        var source = Path.Combine(projectDir, StoreLit.Source);
        var bookPath = Path.Combine(source, "book_full.txt");
        var metaPath = Path.Combine(source, "extract_meta.json");
        var imgDir = Path.Combine(source, "book_images");
        var pdfName = FindSourcePdfName(source);
        var bookExists = File.Exists(bookPath);

        var status = new BookSourceStatus
        {
            PdfExists = !string.IsNullOrEmpty(pdfName),
            PdfName = pdfName,
            BookTextExists = bookExists,
            BookTextPath = bookExists ? bookPath : null,
            BookTextBytes = bookExists ? new FileInfo(bookPath).Length : 0,
            PageImageCount = CountBookPageImages(imgDir),
        };

        ApplyExtractMeta(status, metaPath);
        OverlayRuntimeConfig(status, projectId);
        ResolveReadyForStage1(status, metaPath);
        FillBookPreview(status, bookPath);
        UnlockStage1IfScenesExist(status, projectDir);
        return status;
    }

    private static string? FindSourcePdfName(string source)
    {
        if (!Directory.Exists(source))
            return null;
        try
        {
            // One DirectoryInfo scan (Length already available; avoid re-stat via FileInfo).
            return new DirectoryInfo(source).EnumerateFiles()
                .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name.Contains("nick", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(f => f.Length)
                .Select(f => f.Name)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int CountBookPageImages(string imgDir)
    {
        if (!Directory.Exists(imgDir))
            return 0;
        try
        {
            return new DirectoryInfo(imgDir).EnumerateFiles()
                .Count(f =>
                {
                    var e = f.Extension.ToLowerInvariant();
                    return e is ".jpg" or ".jpeg" or ".png" or ".webp";
                });
        }
        catch
        {
            return 0;
        }
    }

    private static void ApplyExtractMeta(BookSourceStatus status, string metaPath)
    {
        if (!File.Exists(metaPath))
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            ApplyExtractMetaRoot(status, doc.RootElement);
        }
        catch { /* ignore */ }
    }

    private static void ApplyExtractMetaRoot(BookSourceStatus status, JsonElement root)
    {
        status.TextQuality = root.TryGetProperty("text_quality", out var tq) ? tq.GetString() : null;
        ApplyExtractMetaEnums(status, root);
        ApplyExtractMetaInts(status, root);
        ApplyExtractMetaAnalysis(status, root);
        ApplyExtractMetaNotes(status, root);
    }

    private static void ApplyExtractMetaEnums(BookSourceStatus status, JsonElement root)
    {
        if (root.TryGetProperty("book_kind", out var bk) && bk.ValueKind == JsonValueKind.String &&
            Enum.TryParse<SourceDocumentType>(bk.GetString(), ignoreCase: true, out var parsedKind))
            status.BookKind = parsedKind;
        status.TextEngine = root.TryGetProperty("text_engine", out var te)
            ? TextEngineKindExtensions.TryParse(te.GetString())
            : null;
        if (root.TryGetProperty("runtime_mode", out var rmode) && rmode.ValueKind == JsonValueKind.String &&
            Enum.TryParse<RuntimeMode>(rmode.GetString(), true, out var parsedRm))
            status.RuntimeMode = parsedRm;
        if (root.TryGetProperty("ready_for_stage1", out var r) &&
            (r.ValueKind is JsonValueKind.True or JsonValueKind.False))
            status.ReadyForStage1 = r.GetBoolean();
    }

    private static void ApplyExtractMetaInts(BookSourceStatus status, JsonElement root)
    {
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
        if (root.TryGetProperty("suggested_chunk_pages", out var sc) && sc.TryGetInt32(out var chunks))
            status.SuggestedChunkPages = chunks;
    }

    private static void ApplyExtractMetaAnalysis(BookSourceStatus status, JsonElement root)
    {
        if (!root.TryGetProperty("analysis", out var an) || an.ValueKind != JsonValueKind.Object)
            return;
        if (an.TryGetProperty("garbage_score", out var gs) && gs.TryGetDouble(out var gsv))
            status.GarbageScore = gsv;
        if (string.IsNullOrEmpty(status.TextQuality) && an.TryGetProperty("text_quality", out var atq))
            status.TextQuality = atq.GetString();
    }

    private static void ApplyExtractMetaNotes(BookSourceStatus status, JsonElement root)
    {
        if (!root.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.Array)
            return;
        foreach (var n in notes.EnumerateArray())
        {
            var s = n.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                status.Notes.Add(s);
        }
    }

    private void OverlayRuntimeConfig(BookSourceStatus status, string projectId)
    {
        try
        {
            var cfg = GetConfigSync(projectId);
            if (TryReadPositiveConfigInt(cfg, "target_runtime_minutes", out var ctm))
            {
                status.TargetRuntimeMinutes = FilmRuntime.ClampMinutes(ctm);
                status.SuggestedTotalMinutes = status.TargetRuntimeMinutes;
            }
            if (TryReadPositiveConfigInt(cfg, "natural_runtime_minutes", out var cnat))
                status.NaturalRuntimeMinutes = FilmRuntime.ClampMinutes(cnat);
            if (TryReadConfigEnum<RuntimeMode>(cfg, "runtime_mode", out var parsedRmCfg))
                status.RuntimeMode = parsedRmCfg;
            if (status.RuntimeMode is null &&
                status.NaturalRuntimeMinutes is int n && status.TargetRuntimeMinutes is int tg)
                status.RuntimeMode = InferRuntimeMode(n, tg);
        }
        catch { /* ignore */ }
    }

    private static bool TryReadPositiveConfigInt(
        Dictionary<string, JsonElement> cfg, string key, out int value)
    {
        value = 0;
        return cfg.TryGetValue(key, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value)
            && value > 0;
    }

    private static bool TryReadConfigEnum<T>(
        Dictionary<string, JsonElement> cfg, string key, out T value)
        where T : struct, Enum
    {
        value = default;
        return cfg.TryGetValue(key, out var el)
            && el.ValueKind == JsonValueKind.String
            && Enum.TryParse(el.GetString(), true, out value);
    }

    private static RuntimeMode InferRuntimeMode(int natural, int target)
    {
        if (target == natural)
            return RuntimeMode.Natural;
        if (target < natural)
            return RuntimeMode.Reduced;
        return RuntimeMode.Custom;
    }

    private static void ResolveReadyForStage1(BookSourceStatus status, string metaPath)
    {
        // Prefer extract_meta.ready_for_stage1 when present (set by BookPrepareService strategy).
        // Only fall back to heuristics when meta is missing or incomplete.
        var metaReadySet = File.Exists(metaPath) && status.TextQuality is not null;
        if (!status.BookTextExists)
        {
            status.ReadyForStage1 = false;
            return;
        }
        if (!metaReadySet)
        {
            ApplyReadyWhenMetaMissing(status);
            return;
        }
        if (status.ReadyForStage1)
            return;
        if (!string.Equals(status.TextQuality, "good", StringComparison.OrdinalIgnoreCase) ||
            status.GarbageScore >= 0.45 ||
            status.BookTextBytes <= 200)
            return;
        status.ReadyForStage1 = true;
        if (status.Notes.All(n => !n.Contains("Stage 1 unlocked", StringComparison.OrdinalIgnoreCase)))
            status.Notes.Add(
                "Stage 1 unlocked: text quality is good enough (vision still optional for better OCR).");
    }

    private static void ApplyReadyWhenMetaMissing(BookSourceStatus status)
    {
        if (status.TextQuality is null && status.BookTextBytes > 200)
        {
            // No meta yet — allow Stage 1 if plain text looks present (user may have uploaded .txt)
            status.TextQuality = "unknown";
            status.ReadyForStage1 = true;
            return;
        }
        if (string.Equals(status.TextQuality, "good", StringComparison.OrdinalIgnoreCase) &&
            status.GarbageScore < 0.45)
        {
            status.ReadyForStage1 = true;
        }
    }

    private static void FillBookPreview(BookSourceStatus status, string bookPath)
    {
        if (!status.BookTextExists)
            return;
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

    private static void UnlockStage1IfScenesExist(BookSourceStatus status, string projectDir)
    {
        // Re-run path: existing scenes.json + book text is enough even if prepare still flags "not ready"
        try
        {
            var scenesPath = Path.Combine(projectDir, StoreLit.ScenesJson);
            if (status.ReadyForStage1 ||
                !status.BookTextExists ||
                status.BookTextBytes <= 200 ||
                !File.Exists(scenesPath) ||
                new FileInfo(scenesPath).Length <= 64)
                return;
            status.ReadyForStage1 = true;
            if (status.Notes.All(n => !n.Contains("Re-run Stage 1", StringComparison.OrdinalIgnoreCase)))
                status.Notes.Add(
                    "Re-run Stage 1 enabled: scenes.json already exists and book_full.txt is present.");
        }
        catch { /* ignore */ }
    }

    private Stage1Status ReadStage1Status(string projectId)
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

        return new Stage1Status { Present = false, ScenesFile = StoreLit.ScenesJson };
    }

    private Stage2PlanStatus ReadStage2PlanStatus(string projectId, Stage1Status stage1)
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
            FillStage2Counts(status, doc.RootElement);
            ApplyStage2Meta(status, doc.RootElement, bpPath);
            DetectStage2Stale(status, projectId, bpPath);
        }
        catch { /* ignore */ }

        return status;
    }

    private static void FillStage2Counts(Stage2PlanStatus status, JsonElement root)
    {
        if (!root.TryGetProperty(StoreLit.Scenes, out var scenes) || scenes.ValueKind != JsonValueKind.Array)
            return;
        status.Stage2Scenes = scenes.GetArrayLength();
        foreach (var s in scenes.EnumerateArray())
        {
            if (s.TryGetProperty(StoreLit.VeoClips, out var vc) && vc.ValueKind == JsonValueKind.Array)
                status.Stage2Clips += vc.GetArrayLength();
        }
        status.Stage2Ready = status.Stage2Scenes > 0 && status.Stage2Clips > 0;
    }

    private static void ApplyStage2Meta(Stage2PlanStatus status, JsonElement root, string bpPath)
    {
        if (root.TryGetProperty("stage2_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("completed_at", out var ca))
                status.LastCompletedAt = ca.GetString();
            else if (meta.TryGetProperty("last_partial_at", out var lp))
                status.LastCompletedAt = lp.GetString();
            else
                status.LastCompletedAt = null;
            status.LastRunMessage = meta.TryGetProperty("last_run_message", out var lm)
                ? lm.GetString()
                : null;
            if (meta.TryGetProperty("validation_issue_count", out var vic) && vic.TryGetInt32(out var n))
                status.ValidationIssueCount = n;
        }

        if (!string.IsNullOrEmpty(status.LastCompletedAt))
            return;
        try
        {
            status.LastCompletedAt = File.GetLastWriteTime(bpPath).ToString(StoreLit.IsoDateTime);
        }
        catch { /* ignore */ }
    }

    private void DetectStage2Stale(Stage2PlanStatus status, string projectId, string bpPath)
    {
        // Stale when Stage 1 bible is newer than blueprint
        var s1Path = ResolveScenesJsonPath(projectId);
        if (!File.Exists(s1Path) || !status.Stage2Ready)
            return;
        try
        {
            var s1m = File.GetLastWriteTimeUtc(s1Path);
            var bpm = File.GetLastWriteTimeUtc(bpPath);
            status.Stage2Stale = s1m > bpm.AddSeconds(1);
        }
        catch { /* ignore */ }
    }

    public string? ResolveCompositePath(string projectId, int sceneNumber)
    {
        var dir = GetProjectDir(projectId);
        // Remux writes scene_XX.mp4; older/python path used scene_XX_complete.mp4
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, StoreLit.Assets, StoreLit.Video, $"scene_{sceneNumber:D2}.mp4"),
                     Path.Combine(dir, StoreLit.Assets, StoreLit.Scenes, $"scene_{sceneNumber:D2}.mp4"),
                     Path.Combine(dir, StoreLit.Assets, StoreLit.Video, $"scene_{sceneNumber:D2}_complete.mp4"),
                     Path.Combine(dir, StoreLit.Assets, StoreLit.Scenes, $"scene_{sceneNumber:D2}_complete.mp4"),
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
        var videoDir = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Video);
        var wipFullPath = ResolveWipMovieFullPath(projectId);
        FillWipExists(result, projectId, projectDir, wipFullPath);

        var clipsByScene = IndexExactClipsByScene(videoDir);
        var blueprintScenes = GetBlueprintSceneNumbers(projectId);
        var scenesToRemux = ListScenesToRemuxForWip(clipsByScene, blueprintScenes);
        result.ScenesToRemux = scenesToRemux;

        // Stale = missing composite, clips newer, no .sources.json,
        // or manifest clip set ≠ current exact/blueprint clips.
        result.StaleScenes = scenesToRemux
            .Where(sn => IsSceneCompositeDirty(projectId, sn, videoDir, clipsByScene))
            .ToList();

        // WIP sources = Stage 2 scene composites (blueprint-filtered when available)
        var currentSources = ListWipSourceFilesForProject(projectId, videoDir, blueprintScenes);
        result.CanBuild = scenesToRemux.Count > 0 || currentSources.Count > 0;

        if (!result.CanBuild)
        {
            result.Stale = true;
            result.Reason = "No scene or clip videos on disk to build WIP";
            return result;
        }

        if (result.StaleScenes.Count > 0)
        {
            result.Stale = true;
            result.Reason =
                $"Scene composite(s) dirty (missing/out of date): {string.Join(", ", result.StaleScenes.Select(n => $"S{n:D2}"))}";
            return result;
        }

        if (!result.Exists || wipFullPath is null)
        {
            result.Stale = true;
            result.Reason = "WIP missing — needs rebuild";
            return result;
        }

        if (TryMarkWipStaleFromBlueprint(result, projectId, wipFullPath, blueprintScenes))
            return result;
        if (TryMarkWipStaleFromSources(result, wipFullPath, currentSources))
            return result;

        result.Stale = false;
        result.Reason = "Up to date";
        return result;
    }

    private void FillWipExists(WipFreshness result, string projectId, string projectDir, string? wipFullPath)
    {
        if (wipFullPath is not null &&
            File.Exists(wipFullPath) &&
            new FileInfo(wipFullPath).Length >= 1024)
        {
            var fi = new FileInfo(wipFullPath);
            result.Exists = true;
            result.Path = Path.GetRelativePath(projectDir, wipFullPath).Replace('\\', '/');
            result.Bytes = fi.Length;
            result.UpdatedAt = fi.LastWriteTime.ToString(StoreLit.IsoDateTime);
            return;
        }

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

    private bool TryMarkWipStaleFromBlueprint(
        WipFreshness result, string projectId, string wipFullPath, List<int>? blueprintScenes)
    {
        // Stage 2 blueprint newer than last WIP build → always remux
        var bpPath = FindBlueprintPathSync(projectId);
        DateTime? bpMtime = bpPath is not null && File.Exists(bpPath)
            ? new FileInfo(bpPath).LastWriteTimeUtc
            : null;
        var manifestPath = ClipFileNaming.WipSourcesManifestPath(wipFullPath);
        if (bpMtime is DateTime bpm && File.Exists(manifestPath))
            return TryMarkWipStaleFromManifest(result, manifestPath, bpm, blueprintScenes);

        if (bpMtime is DateTime bpm2)
        {
            var wipMtime = new FileInfo(wipFullPath).LastWriteTimeUtc;
            if (bpm2 > wipMtime.AddSeconds(1))
            {
                result.Stale = true;
                result.Reason = "Stage 2 blueprint newer than WIP — remux all scenes + rebuild";
                return true;
            }
        }
        return false;
    }

    private static bool TryMarkWipStaleFromManifest(
        WipFreshness result, string manifestPath, DateTime bpm, List<int>? blueprintScenes)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (IsBlueprintNewerThanManifest(doc.RootElement, bpm, out var reason))
            {
                result.Stale = true;
                result.Reason = reason;
                return true;
            }
            if (SceneListChangedSinceManifest(doc.RootElement, blueprintScenes))
            {
                result.Stale = true;
                result.Reason = "Stage 2 scene list changed — remux all scenes + rebuild";
                return true;
            }
        }
        catch { /* fall through */ }
        return false;
    }

    private static bool IsBlueprintNewerThanManifest(JsonElement root, DateTime bpm, out string reason)
    {
        reason = "";
        if (root.TryGetProperty("blueprintMtimeUtc", out var bm) &&
            bm.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(bm.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var recordedBp))
        {
            if (bpm > recordedBp.ToUniversalTime().AddSeconds(1))
            {
                reason = "Stage 2 blueprint changed since last WIP — remux all scenes + rebuild";
                return true;
            }
            return false;
        }
        if (root.TryGetProperty("builtAtUtc", out var built) &&
            built.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(built.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var builtAt) &&
            bpm > builtAt.ToUniversalTime().AddSeconds(1))
        {
            reason = "Stage 2 blueprint newer than WIP — remux all scenes + rebuild";
            return true;
        }
        return false;
    }

    private static bool SceneListChangedSinceManifest(JsonElement root, List<int>? blueprintScenes)
    {
        if (!root.TryGetProperty("sceneNumbers", out var sns) ||
            sns.ValueKind != JsonValueKind.Array ||
            blueprintScenes is not { Count: > 0 })
            return false;
        var recorded = sns.EnumerateArray()
            .Select(e => e.TryGetInt32(out var n) ? n : 0)
            .Where(n => n > 0)
            .OrderBy(n => n)
            .ToList();
        var planned = blueprintScenes.OrderBy(n => n).ToList();
        return !recorded.SequenceEqual(planned);
    }

    private static bool TryMarkWipStaleFromSources(
        WipFreshness result, string wipFullPath, List<string> currentSources)
    {
        // Manifest: detect added/removed/replaced sources vs last successful WIP build
        var manifestMismatch = CompareWipSourcesManifest(wipFullPath, currentSources);
        if (manifestMismatch is { Length: > 0 })
        {
            result.Stale = true;
            result.Reason = manifestMismatch;
            return true;
        }

        // No manifest (old WIP): fall back to mtime — any source newer than WIP
        var manifestPath = ClipFileNaming.WipSourcesManifestPath(wipFullPath);
        if (File.Exists(manifestPath))
            return false;
        var wipMtime = new FileInfo(wipFullPath).LastWriteTimeUtc;
        foreach (var src in currentSources)
        {
            try
            {
                if (new FileInfo(src).LastWriteTimeUtc > wipMtime.AddSeconds(1))
                {
                    result.Stale = true;
                    result.Reason = "Sources newer than WIP (no build manifest — rebuild recommended)";
                    return true;
                }
            }
            catch { /* ignore */ }
        }
        return false;
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
        videoDir ??= Path.Combine(GetProjectDir(projectId), StoreLit.Assets, StoreLit.Video);
        clipsByScene ??= IndexExactClipsByScene(videoDir);

        var expectedNames = GetExpectedClipFileNames(projectId, sceneNum, videoDir, clipsByScene);
        if (expectedNames.Count == 0)
            return false;

        var composite = ResolveCompositePath(projectId, sceneNum);
        if (composite is null || !File.Exists(composite))
            return true;

        if (SceneCompositeClipsNewerThanFile(videoDir, expectedNames, composite))
            return true;

        return SceneCompositeManifestDiffers(videoDir, sceneNum, composite, expectedNames);
    }

    private static bool SceneCompositeClipsNewerThanFile(
        string videoDir, List<string> expectedNames, string composite)
    {
        var maxClipMtime = DateTime.MinValue;
        foreach (var name in expectedNames)
        {
            var path = Path.Combine(videoDir, name);
            if (!File.Exists(path)) continue;
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt > maxClipMtime) maxClipMtime = mt;
        }
        return maxClipMtime > File.GetLastWriteTimeUtc(composite).AddSeconds(1);
    }

    private static bool SceneCompositeManifestDiffers(
        string videoDir, int sceneNum, string composite, List<string> expectedNames)
    {
        var manifestPath = ClipFileNaming.SceneSourcesManifestPath(composite);
        var remuxOut = Path.Combine(videoDir, $"scene_{sceneNum:D2}.mp4");
        if (!File.Exists(manifestPath) && File.Exists(remuxOut))
            manifestPath = ClipFileNaming.SceneSourcesManifestPath(remuxOut);

        // No strict manifest → treat as dirty (old remux may have concat'd .native + orphans)
        if (!File.Exists(manifestPath))
            return true;

        var recorded = ReadSceneCompositeManifestNames(manifestPath);
        if (recorded is null)
            return true;

        var expectedSorted = expectedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var recSorted = recorded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return !expectedSorted.SequenceEqual(recSorted, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string>? ReadSceneCompositeManifestNames(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty(StoreLit.Clips, out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
                return null;

            var recorded = new List<string>();
            foreach (var el in clipsEl.EnumerateArray())
            {
                if (el.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    recorded.Add(name);
            }
            return recorded;
        }
        catch
        {
            return null;
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
        AddExistingExactClipNames(names, clipsByScene, sceneNum, allowed);
        AddMissingBlueprintClipFiles(names, allowed, videoDir, sceneNum);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddExistingExactClipNames(
        List<string> names,
        Dictionary<int, List<FileInfo>> clipsByScene,
        int sceneNum,
        HashSet<int>? allowed)
    {
        if (!clipsByScene.TryGetValue(sceneNum, out var files))
            return;
        foreach (var name in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(fi => fi.Name))
        {
            if (!ClipFileNaming.IsExactClipFileName(name)) continue;
            if (!ClipFileNameAllowed(name, allowed)) continue;
            names.Add(name);
        }
    }

    private static bool ClipFileNameAllowed(string name, HashSet<int>? allowed)
    {
        if (allowed is not { Count: > 0 })
            return true;
        return int.TryParse(name.AsSpan(14, 2), out var cn) && allowed.Contains(cn);
    }

    private static void AddMissingBlueprintClipFiles(
        List<string> names, HashSet<int>? allowed, string videoDir, int sceneNum)
    {
        if (allowed is not { Count: > 0 })
            return;
        foreach (var cn in allowed.OrderBy(c => c))
        {
            var name = $"scene_{sceneNum:D2}_clip_{cn:D2}.mp4";
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(videoDir, name);
            if (File.Exists(path) && new FileInfo(path).Length >= 1024)
                names.Add(name);
        }
    }

    private HashSet<int>? TryBlueprintClipNumbers(string projectId, int sceneNum)
    {
        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is null) return null;
            if (!bp.RootElement.TryGetProperty(StoreLit.Scenes, out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            return ClipNumbersForScene(scenes, sceneNum);
        }
        catch { /* ignore */ }
        return null;
    }

    private static HashSet<int>? ClipNumbersForScene(JsonElement scenes, int sceneNum)
    {
        foreach (var s in scenes.EnumerateArray())
        {
            if (ReadSceneNumberOrZero(s) != sceneNum)
                continue;
            return CollectVeoClipNumbers(s);
        }
        return null;
    }

    private static int ReadSceneNumberOrZero(JsonElement s) =>
        s.TryGetProperty(JsonKeys.SceneNumber, out var snEl) && snEl.TryGetInt32(out var v) ? v : 0;

    private static HashSet<int>? CollectVeoClipNumbers(JsonElement scene)
    {
        if (!scene.TryGetProperty(StoreLit.VeoClips, out var clips) || clips.ValueKind != JsonValueKind.Array)
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

    /// <summary>Stage 2 scene numbers from blueprint, or null if no plan.</summary>
    public List<int>? GetBlueprintSceneNumbers(string projectId)
    {
        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is null) return null;
            if (!bp.RootElement.TryGetProperty(StoreLit.Scenes, out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<int>();
            foreach (var s in scenes.EnumerateArray())
            {
                if (s.TryGetProperty(JsonKeys.SceneNumber, out var sn) && sn.TryGetInt32(out var n) && n > 0)
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
            IndexExactClipsByScene(Path.Combine(GetProjectDir(projectId), StoreLit.Assets, StoreLit.Video)),
            GetBlueprintSceneNumbers(projectId));

    private static List<int> ListScenesToRemuxForWip(
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
        var videoDir = Path.Combine(GetProjectDir(projectId), StoreLit.Assets, StoreLit.Video);
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
            return Path.Combine(projectDir, StoreLit.Assets, "movie_wip.mp4");

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

            var recorded = ReadRecordedWipSources(arr);
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

            var setChange = DescribeWipSourceSetChange(current, recSorted);
            if (setChange is not null)
                return setChange;

            var changed = FindChangedWipSource(current, recSorted);
            if (changed is not null)
                return changed;

            if (current.Count != recSorted.Count)
                return $"Source count {current.Count} vs last build {recSorted.Count}";

            return null;
        }
        catch
        {
            return "WIP manifest unreadable — rebuild needed";
        }
    }

    private static List<(string Name, long Bytes, DateTime Mtime)> ReadRecordedWipSources(JsonElement arr)
    {
        var recorded = new List<(string Name, long Bytes, DateTime Mtime)>();
        foreach (var el in arr.EnumerateArray())
        {
            var entry = ReadWipSourceEntry(el);
            if (entry is not null)
                recorded.Add(entry.Value);
        }
        return recorded;
    }

    private static (string Name, long Bytes, DateTime Mtime)? ReadWipSourceEntry(JsonElement el)
    {
        var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (name.Length == 0)
            return null;
        long bytes = 0;
        if (el.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bl))
            bytes = bl;
        var mtime = DateTime.MinValue;
        if (el.TryGetProperty("mtimeUtc", out var m) &&
            m.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(m.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var mt))
            mtime = mt.ToUniversalTime();
        return (name, bytes, mtime);
    }

    private static string? DescribeWipSourceSetChange(
        List<(string Name, long Bytes, DateTime Mtime)> current,
        List<(string Name, long Bytes, DateTime Mtime)> recSorted)
    {
        var curNames = current.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recNames = recSorted.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = curNames.Except(recNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var removed = recNames.Except(curNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return null;
        var parts = new List<string>();
        if (added.Count > 0)
            parts.Add("added " + string.Join(", ", added));
        if (removed.Count > 0)
            parts.Add("removed " + string.Join(", ", removed));
        return "Scene set changed (" + string.Join("; ", parts) + ")";
    }

    private static string? FindChangedWipSource(
        List<(string Name, long Bytes, DateTime Mtime)> current,
        List<(string Name, long Bytes, DateTime Mtime)> recSorted)
    {
        foreach (var c in current)
        {
            var r = recSorted.FirstOrDefault(x =>
                string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            if (r.Name is null)
                continue;
            if (c.Bytes != r.Bytes || Math.Abs((c.Mtime - r.Mtime).TotalSeconds) > 1.5)
                return $"Source changed: {c.Name}";
        }
        return null;
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
            var parentIndex = await GetDirIndexAsync(Path.Combine(parentDir, StoreLit.Assets, StoreLit.Video), ct).ConfigureAwait(false);
            foreach (var kv in parentIndex.Where(kv => !index.ContainsKey(kv.Key)))
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

    /// <summary>E5: clip video likely out of date vs plan or dialogue QA.</summary>
    private static (bool IsStale, string? Reason) EvaluateClipStale(
        bool onDisk,
        string? clipPath,
        ClipDialogueVerificationResult? ver,
        string? blueprintPath)
    {
        if (!onDisk) return (false, null);
        if (ver is not null)
        {
            var st = (ver.Status ?? "").Trim().ToLowerInvariant();
            if (st is "mismatch" or "speaker_swap")
                return (true, "dialogue_qa");
            if (st == "no_speech" && !string.IsNullOrWhiteSpace(ver.ExpectedDialogue))
                return (true, "dialogue_qa_no_speech");
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(clipPath) && File.Exists(clipPath)
                && !string.IsNullOrWhiteSpace(blueprintPath) && File.Exists(blueprintPath))
            {
                var clipM = File.GetLastWriteTimeUtc(clipPath);
                var bpM = File.GetLastWriteTimeUtc(blueprintPath);
                if (bpM > clipM.AddSeconds(2))
                    return (true, "plan_newer");
            }
        }
        catch { /* ignore */ }
        return (false, null);
    }

    /// <summary>
    /// Bulk variant of "is this clip present" for scene-list rendering — takes a directory scan
    /// preloaded once per request (videoIndex) rather than a per-clip check, so it stays sync and
    /// dictionary-based instead of routing through MediaSyncLocator (SQL-backed, async): that
    /// would mean one registry query per clip instead of one directory scan per scene. See
    /// MediaSyncLocator's doc comment and FilmJobService.ClipPresentOnServerOrClient for the
    /// single-clip sibling of this same "why not just unify them" call.
    /// </summary>
    internal static bool ClipOnDisk(Dictionary<string, long> videoIndex, int scene, int clip)
    {
        var basePrefix = $"scene_{scene:D2}_clip_{clip:D2}";
        var mp4Name = basePrefix + ".mp4";

        if (videoIndex.TryGetValue(mp4Name, out var sz) && sz >= 1024)
            return true;
        if (videoIndex.ContainsKey(mp4Name + ClientMarkerExtension))
            return true;
        if (videoIndex.ContainsKey(basePrefix + StoreLit.ClipJsonSuffix))
            return true;

        // A client marker counts only when it marks the clip VIDEO (scene_XX_clip_YY*.mp4.client.json).
        // The register endpoint once wrote markers for synced sidecars too (…clip.json.client.json) —
        // those must not make a clip look present after its sidecar and video are both gone.
        return videoIndex.Keys.Any(k =>
            k.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) &&
            (k.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
             k.EndsWith(StoreLit.ClipJsonSuffix, StringComparison.OrdinalIgnoreCase) ||
             k.EndsWith(".mp4" + ClientMarkerExtension, StringComparison.OrdinalIgnoreCase)));
    }

    private Dictionary<string, JsonElement> LoadCharacterSeeds(string projectId) =>
        TryLoadSeedsFromCastFile(projectId, StoreLit.CharacterSeedTokens)
        ?? TryLoadSeedsFromBlueprintGpv(projectId, StoreLit.CharacterSeedTokens)
        ?? TryLoadSeedsFromFountainModel(projectId, StoreLit.CharacterSeedTokens)
        ?? TryLoadSeedsFromScenesGpv(projectId, StoreLit.CharacterSeedTokens, swallowErrors: false)
        ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// location_seed_tokens from cast_seeds / blueprint / scenes — same precedence as character seeds.
    /// </summary>
    private Dictionary<string, JsonElement> LoadLocationSeeds(string projectId) =>
        TryLoadSeedsFromCastFile(projectId, StoreLit.LocationSeedTokens)
        ?? TryLoadSeedsFromBlueprintGpv(projectId, StoreLit.LocationSeedTokens)
        ?? TryLoadSeedsFromFountainModel(projectId, StoreLit.LocationSeedTokens)
        ?? TryLoadSeedsFromScenesGpv(projectId, StoreLit.LocationSeedTokens, swallowErrors: true)
        ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement>? CloneObjectIfAny(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in el.EnumerateObject())
            dict[p.Name] = p.Value.Clone();
        return dict.Count > 0 ? dict : null;
    }

    private static Dictionary<string, JsonElement>? TryReadSeedTokens(JsonElement root, string tokenKey)
    {
        if (root.TryGetProperty(tokenKey, out var s) && s.ValueKind == JsonValueKind.Object)
            return CloneObjectIfAny(s);
        if (root.TryGetProperty(StoreLit.GlobalProductionVariables, out var g) &&
            g.TryGetProperty(tokenKey, out var s2) &&
            s2.ValueKind == JsonValueKind.Object)
            return CloneObjectIfAny(s2);
        return null;
    }

    private Dictionary<string, JsonElement>? TryLoadSeedsFromCastFile(string projectId, string tokenKey)
    {
        try
        {
            foreach (var name in new[] { ScreenplayService.CastSeedsFileName })
            {
                var castPath = Path.Combine(GetProjectDir(projectId), StoreLit.Source, name);
                if (!File.Exists(castPath)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(castPath));
                var dict = TryReadSeedTokens(doc.RootElement, tokenKey);
                if (dict is { Count: > 0 })
                    return dict;
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private Dictionary<string, JsonElement>? TryLoadSeedsFromBlueprintGpv(string projectId, string tokenKey)
    {
        try
        {
            using var bp = LoadBlueprintSync(projectId);
            if (bp is not null &&
                bp.RootElement.TryGetProperty(StoreLit.GlobalProductionVariables, out var gpv) &&
                gpv.TryGetProperty(tokenKey, out var seeds) &&
                seeds.ValueKind == JsonValueKind.Object)
            {
                return CloneObjectIfAny(seeds);
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private Dictionary<string, JsonElement>? TryLoadSeedsFromFountainModel(string projectId, string tokenKey)
    {
        try
        {
            var model = ScreenplayService.TryBuildModelFromProject(this, projectId);
            if (model is not null &&
                model.TryGetValue(StoreLit.GlobalProductionVariables, out var gpvObj) &&
                gpvObj is Dictionary<string, object?> gpv &&
                gpv.TryGetValue(tokenKey, out var obj) &&
                obj is Dictionary<string, object?> dictObj &&
                dictObj.Count > 0)
            {
                var json = JsonSerializer.Serialize(dictObj);
                using var doc = JsonDocument.Parse(json);
                return CloneObjectIfAny(doc.RootElement);
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private Dictionary<string, JsonElement>? TryLoadSeedsFromScenesGpv(
        string projectId, string tokenKey, bool swallowErrors)
    {
        var scenesPath = GetScenesPath(projectId);
        if (!File.Exists(scenesPath))
            return null;
        if (!swallowErrors)
            return ReadScenesGpvTokens(scenesPath, tokenKey);
        try
        {
            return ReadScenesGpvTokens(scenesPath, tokenKey);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? ReadScenesGpvTokens(string scenesPath, string tokenKey)
    {
        using var scenesDoc = JsonDocument.Parse(File.ReadAllText(scenesPath));
        if (scenesDoc.RootElement.TryGetProperty(StoreLit.GlobalProductionVariables, out var g2) &&
            g2.TryGetProperty(tokenKey, out var s3) &&
            s3.ValueKind == JsonValueKind.Object)
            return CloneObjectIfAny(s3) ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        return null;
    }

    private Dictionary<string, JsonElement> LoadWardrobeLocks(string projectId)
    {
        // Prefer cast_seeds.json, then blueprint, then scenes.json — same precedence as
        // LoadCharacterSeeds, since UpdateWardrobeLockText keeps all three in sync.
        static Dictionary<string, JsonElement>? TryRead(JsonElement root)
        {
            JsonElement el = default;
            if (root.TryGetProperty(StoreLit.WardrobeLockTokens, out var s) && s.ValueKind == JsonValueKind.Object)
                el = s;
            else if (root.TryGetProperty(StoreLit.GlobalProductionVariables, out var g) &&
                     g.TryGetProperty(StoreLit.WardrobeLockTokens, out var s2) &&
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

    private static bool IsVoiceOnly(JsonElement info)
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
            if (s.TryGetProperty(StoreLit.DisplayName, out var dn)) display = dn.GetString();
            if (s.TryGetProperty(JsonKeys.Description, out var d)) desc = d.GetString();
        }
        return CastKindClassifier.IsGroup(key, display, castKind, desc);
    }

    /// <summary>
    /// Blueprint may list Character_Suitor_1 while only Character_Suitors exists in cast_seeds.
    /// North Star: treat that as covered by the ensemble group (no solo plate).
    /// </summary>
    private bool ResolvesToExistingGroupCast(string projectId, string onScreenKey)
    {
        try
        {
            var seeds = LoadCharacterSeeds(projectId);
            if (seeds.Count == 0) return false;
            var dict = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, el) in seeds)
                dict[k] = el;
            return PageToMovie.Adaptation.Validation.CastPackageCrossCheck
                .TryResolveNumberedToGroupKey(onScreenKey, dict) is not null;
        }
        catch
        {
            return false;
        }
    }



    private string ResolveWorkspaceRoot()
    {
        if (TryResolveConfiguredWorkspaceRoot(out var configured))
            return configured;

        // Persistent Docker / Railway volume mounts
        if (Directory.Exists("/data"))
            return "/data";
        if (Directory.Exists("/app/data"))
            return "/app/data";

        return FindRepoRootFromBaseDirectory()
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private bool TryResolveConfiguredWorkspaceRoot(out string configured)
    {
        configured = "";
        if (string.IsNullOrWhiteSpace(_opts.WorkspaceRoot))
            return false;
        try
        {
            var full = Path.GetFullPath(_opts.WorkspaceRoot);
            if (!Directory.Exists(full))
                Directory.CreateDirectory(full);
            configured = full;
            return true;
        }
        catch { /* fallback below */ }
        return false;
    }

    private static string? FindRepoRootFromBaseDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (IsRepoRoot(dir))
                return dir.FullName;
            // running from host/PageToMovie.Api/bin/...
            if (dir.Name.Equals("host", StringComparison.OrdinalIgnoreCase) &&
                dir.Parent is not null &&
                Directory.Exists(Path.Combine(dir.Parent.FullName, StoreLit.Projects)))
            {
                return dir.Parent.FullName;
            }
        }
        return null;
    }

    private static bool IsRepoRoot(DirectoryInfo dir) =>
        Directory.Exists(Path.Combine(dir.FullName, StoreLit.Projects)) &&
        (Directory.Exists(Path.Combine(dir.FullName, "prompts")) ||
         Directory.Exists(Path.Combine(dir.FullName, "host")));
}
