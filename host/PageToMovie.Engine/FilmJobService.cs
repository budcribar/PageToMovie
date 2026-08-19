using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelBacked;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

public interface IJobProgressSink
{
    Task OnJobUpdatedAsync(JobSnapshot snapshot, CancellationToken ct = default);
    Task OnJobLogAsync(string message, CancellationToken ct = default);
}

/// <summary>
/// Native C# film job orchestrator (no Python): Stage 1/2, book prepare,
/// character design, multi-ref video, remux/WIP with SignalR progress.
/// Phase C: multi-job concurrency via ApiWorkerPool, scene locks, metrics.
/// </summary>
public sealed class FilmJobService
{
    private const string StatusRunning = "running";
    private const string StatusQueued = "queued";
    private const string StatusCancelled = "cancelled";
    private const string StatusError = "error";
    private const string StatusPartial = "partial";
    private const string StatusDone = "done";
    private const string CancelledByUser = "Cancelled by user";
    private const string ProjectIdRequired = "projectId required";
    private const string KindStage2 = "stage2";
    private const string AssetsFolder = "assets";
    private const string VideoFolder = "video";
    private const string VoiceNotConfigured = "Voice not configured";
    private const string ScenesKey = "scenes";
    private const string VeoClipsKey = "veo_clips";

    private static readonly AsyncLocal<JobRunState?> CurrentRun = new();
    private static readonly TimeSpan DefaultLockTtl = TimeSpan.FromHours(2);

    private readonly ProjectStore _projects;
    private readonly IVideoClient _grok;
    private readonly CharacterDesignService _characters;
    private readonly LocationDesignService _locations;
    private readonly CharacterBookPlateService _plates;
    private readonly BookPrepareService _books;
    private readonly IChatClient _chat;
    private readonly Stage1Service _stage1;
    private readonly Stage2PlannerService _stage2;
    private readonly VoicePreviewService _voicePreview;
    private readonly ClipAutoReviewService _clipAutoReview;
    private readonly ReviewIndexService _reviewIndex;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ProjectArtifactIndexService _artifactIndex;
    private readonly ReviewEventStore _learning;
    private readonly ProjectRulesService _projectRules;
    private readonly CostReportService _costs;
    private readonly IJobStore _jobs;
    private readonly ILockService _locks;
    private readonly ApiWorkerPool _apiPool;
    private readonly YouTubeAuthService _youTube;
    private readonly IServerMetricsService _metrics;
    private readonly MediaProxyTicketStore _mediaProxy;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<FilmJobService> _log;
    private readonly ConcurrentQueue<string> _logLines = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCts =
        new(StringComparer.OrdinalIgnoreCase);
    private IJobProgressSink? _sink;
    private readonly IUserContext _user;
    private readonly IUserApiKeyProvider _keys;
    private readonly PageToMovie.Engine.Collaboration.IProjectAclService? _acl;
    private readonly ClipSidecarService? _sidecars;
    private readonly ClipDialogueVerificationService? _dialogueVerification;
    private readonly GlobalTimingCalibrationService? _timingCalibration;
    private readonly ActionCameraOverheadLedger? _timingLedger;
    private readonly AiActionOverheadClassifier? _timingClassifier;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAudioClient _audio;
    private readonly SceneMusicScoringService _musicScoring;
    private readonly MusicSidecarService? _musicSidecars;
    private readonly GenerationErrorLogger? _errorLogger;
    private readonly IVoiceClient _voiceClient;
    private readonly IVoiceCloneClient _voiceClone;
    private readonly BookTextRegistryService? _bookRegistry;
    private readonly PageToMovie.Core.Abstractions.IBookFileSessionFactory? _bookFileSessionFactory;
    private readonly PageToMovie.Core.Abstractions.IFountainFileSessionFactory? _fountainFileSessionFactory;
    private readonly XaiResponsesClient? _xaiResponses;
    private readonly VoiceAlignmentStore? _voiceAlignment;
    private readonly IVideoEditClient? _videoEdit;
    private readonly CastFromScreenplayService? _castExtract;

    public FilmJobService(
        ProjectStore projects,
        IVideoClient grok,
        CharacterDesignService characters,
        LocationDesignService locations,
        CharacterBookPlateService plates,
        BookPrepareService books,
        IChatClient chat,
        Stage1Service stage1,
        Stage2PlannerService stage2,
        VoicePreviewService voicePreview,
        ClipAutoReviewService clipAutoReview,
        ReviewIndexService reviewIndex,
        ProjectTelemetryService telemetry,
        ProjectArtifactIndexService artifactIndex,
        ReviewEventStore learning,
        EditLogService editLogs,
        ProjectRulesService projectRules,
        CostReportService costs,
        IJobStore jobs,
        ILockService locks,
        ApiWorkerPool apiPool,
        YouTubeAuthService youTube,
        IServerMetricsService metrics,
        MediaProxyTicketStore mediaProxy,
        IOptions<PageToMovieOptions> opts,
        ILogger<FilmJobService> log,
        IUserContext user,
        IUserApiKeyProvider keys,
        IHttpClientFactory httpFactory,
        IAudioClient audio,
        SceneMusicScoringService musicScoring,
        IVoiceClient voiceClient,
        IVoiceCloneClient voiceClone,
        ClipSidecarService? sidecars = null,
        ClipDialogueVerificationService? dialogueVerification = null,
        GlobalTimingCalibrationService? timingCalibration = null,
        ActionCameraOverheadLedger? timingLedger = null,
        AiActionOverheadClassifier? timingClassifier = null,
        MusicSidecarService? musicSidecars = null,
        GenerationErrorLogger? errorLogger = null,
        BookTextRegistryService? bookRegistry = null,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessionFactory = null,
        XaiResponsesClient? xaiResponses = null,
        VoiceAlignmentStore? voiceAlignment = null,
        IVideoEditClient? videoEdit = null,
        CastFromScreenplayService? castExtract = null,
        PageToMovie.Engine.Collaboration.IProjectAclService? acl = null,
        PageToMovie.Core.Abstractions.IFountainFileSessionFactory? fountainFileSessionFactory = null)
    {
        _httpFactory = httpFactory;
        _projects = projects;
        _grok = grok;
        _characters = characters;
        _locations = locations;
        _plates = plates;
        _books = books;
        _chat = chat;
        _stage1 = stage1;
        _stage2 = stage2;
        _voicePreview = voicePreview;
        _clipAutoReview = clipAutoReview;
        _reviewIndex = reviewIndex;
        _telemetry = telemetry;
        _artifactIndex = artifactIndex;
        _learning = learning;
        _projectRules = projectRules;
        _costs = costs;
        _jobs = jobs;
        _locks = locks;
        _apiPool = apiPool;
        _youTube = youTube;
        _mediaProxy = mediaProxy;
        _metrics = metrics;
        _opts = opts.Value;
        _log = log;
        _user = user;
        _keys = keys;
        _acl = acl;
        _audio = audio;
        _musicScoring = musicScoring;
        _voiceClient = voiceClient;
        _voiceClone = voiceClone;
        _sidecars = sidecars;
        _dialogueVerification = dialogueVerification;
        _timingCalibration = timingCalibration;
        _timingLedger = timingLedger;
        _timingClassifier = timingClassifier;
        _musicSidecars = musicSidecars;
        _errorLogger = errorLogger;
        _bookRegistry = bookRegistry;
        _bookFileSessionFactory = bookFileSessionFactory;
        _fountainFileSessionFactory = fountainFileSessionFactory;
        _xaiResponses = xaiResponses;
        _voiceAlignment = voiceAlignment;
        _videoEdit = videoEdit;
        _castExtract = castExtract;
    }


    /// <summary>Lab-mode catalog rows are admin-only; block regular users from running them.</summary>
    private void EnsureLabModelsAllowed(IReadOnlyDictionary<string, JsonElement>? cfg)
    {
        if (_user.IsAdmin || cfg is null) return;
        foreach (var key in new[]
                 {
                     "video_model_name", "image_model_name", "planning_model_name", "vision_model_name",
                     "video_review_model_name", "audio_model_name", "voice_model_name"
                 })
        {
            if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
                continue;
            var id = el.GetString();
            if (SupportedModelCatalog.IsLabModel(id))
                throw new InvalidOperationException(
                    $"Model '{id}' is lab-mode (admin-only). Pick a production model in Configuration.");
        }
    }

    public void SetProgressSink(IJobProgressSink sink) => _sink = sink;

    /// <summary>
    /// Primary job for the current caller (Phase F: no global singleton job).
    /// Prefers this user's running job, else their most recent, else idle.
    /// </summary>
    public JobSnapshot GetSnapshot()
    {
        var userId = string.IsNullOrWhiteSpace(_user.UserId) ? null : _user.UserId;
        var primary = _jobs.GetPrimary(userId);
        if (primary is not null)
            return primary.ToSnapshot();
        // Fallback: active AsyncLocal run (background worker thread)
        if (CurrentRun.Value?.Snapshot is { } live &&
            !string.Equals(live.Status, "idle", StringComparison.OrdinalIgnoreCase))
            return Clone(live);
        return new JobSnapshot { Status = "idle", UserId = userId };
    }

    public Task<JobSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetSnapshot());
    }

    public JobSnapshot? GetJob(string jobId) => _jobs.Get(jobId)?.ToSnapshot();

    public IReadOnlyList<JobSnapshot> ListJobs(string? userId = null, string? projectId = null, int take = 50) =>
        _jobs.List(userId, projectId, take).Select(j => j.ToSnapshot()).ToList();

    public bool IsRunning => _jobs.CountRunning() > 0;

    /// <summary>O(1) count of jobs currently running (hot path for /api/capacity).</summary>
    public int RunningCount => _jobs.CountRunning();

    public CapacityOptions Capacity => _opts.Capacity ?? new CapacityOptions();

    public ILockService Locks => _locks;

    public IServerMetricsService Metrics => _metrics;

    /// <summary>
    /// Cancel one job by id, or cancel active jobs in scope.
    /// </summary>
    /// <param name="jobId">When set, cancel only this job (ownership is enforced at the API).</param>
    /// <param name="userId">
    /// When canceling without <paramref name="jobId"/> and <paramref name="cancelAllUsers"/> is false,
    /// only cancel jobs owned by this user. Required for bulk cancel unless canceling all users.
    /// </param>
    /// <param name="cancelAllUsers">
    /// When true (admin only at API), cancel every active job regardless of owner.
    /// </param>
    /// <returns>Number of jobs that were marked cancelled / had CTS cancelled.</returns>
    public Task<int> CancelAsync(
        string? jobId = null,
        string? userId = null,
        bool cancelAllUsers = false)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult(CancelOneJob(jobId) ? 1 : 0);

        // Refuse unscoped bulk cancel — callers must pass userId or cancelAllUsers.
        if (!cancelAllUsers && string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(0);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cancelled = CancelActiveStoreJobs(cancelAllUsers, userId, seen);
        cancelled += CancelOrphanCtsJobs(userId, cancelAllUsers, seen);
        return Task.FromResult(cancelled);
    }

    private int CancelActiveStoreJobs(bool cancelAllUsers, string? userId, HashSet<string> seen)
    {
        var cancelled = 0;
        // Prefer store records (have UserId) over bare CTS keys.
        var records = cancelAllUsers
            ? _jobs.List(userId: null, take: 200)
            : _jobs.List(userId: userId, take: 200);

        foreach (var rec in records)
        {
            if (!IsActiveJobStatus(rec.Status))
                continue;
            if (!seen.Add(rec.JobId))
                continue;
            if (CancelOneJob(rec.JobId))
                cancelled++;
        }
        return cancelled;
    }

    private int CancelOrphanCtsJobs(string? userId, bool cancelAllUsers, HashSet<string> seen)
    {
        var cancelled = 0;
        // CTS entries that might lack a store row (edge case)
        foreach (var key in _jobCts.Keys.ToArray())
        {
            if (!seen.Add(key))
                continue;
            var rec = _jobs.Get(key);
            if (!IsInBulkCancelScope(rec?.UserId, userId, cancelAllUsers))
                continue;
            if (CancelOneJob(key))
                cancelled++;
        }
        return cancelled;
    }

    /// <summary>Whether a job owner matches bulk-cancel scope (unit-tested).</summary>
    public static bool IsInBulkCancelScope(
        string? jobUserId,
        string? requestUserId,
        bool cancelAllUsers)
    {
        if (cancelAllUsers)
            return true;
        if (string.IsNullOrWhiteSpace(requestUserId))
            return false;
        return string.Equals(jobUserId, requestUserId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveJobStatus(string? status) =>
        string.Equals(status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, StatusQueued, StringComparison.OrdinalIgnoreCase);

    private bool CancelOneJob(string jobId)
    {
        var storeHit = _jobs.TryCancel(jobId);
        var ctsHit = false;
        if (_jobCts.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
                ctsHit = true;
            }
            catch
            {
                /* ignore */
            }
        }
        return storeHit || ctsHit;
    }

    private void EnsureCanStart(string? userId)
    {
        var cap = Capacity;
        // Soft gate: a full global worker pool still accepts work until the per-user queue is full.
        // Workers wait for a slot. Reject only when this user's queue is full.
        if (!string.IsNullOrWhiteSpace(userId) &&
            _jobs.CountQueuedForUser(userId) >= Math.Max(1, cap.MaxQueuePerUser))
        {
            _metrics.NoteCapacityReject();
            throw new CapacityRejectedException(
                $"User queue full: MaxQueuePerUser={cap.MaxQueuePerUser}.");
        }

        // Hard reject if global running already >> 2x cap (runaway protection)
        var running = _jobs.CountRunning();
        var max = Math.Max(1, cap.MaxVideoInFlight);
        if (running >= max + Math.Max(1, cap.MaxQueuePerUser))
        {
            _metrics.NoteCapacityReject();
            throw new CapacityRejectedException(
                $"At capacity: running={running}, MaxVideoInFlight={max}.");
        }
    }

    private static JobSnapshot Snapshot
    {
        get => CurrentRun.Value?.Snapshot
               ?? throw new InvalidOperationException("No active job run context.");
        set
        {
            var run = CurrentRun.Value
                      ?? throw new InvalidOperationException("No active job run context.");
            run.Snapshot = value;
        }
    }

    /// <summary>
    /// Assign a fresh running snapshot (0/100 progress, started now, empty log), register it as the
    /// active job, and publish. Callers pass a snapshot carrying only the job-specific fields
    /// (Kind, ProjectId, Scene/Clip/CharKey, Message).
    /// </summary>
    private async Task InitAndPublishJobAsync(JobSnapshot snapshot)
    {
        snapshot.Status = StatusRunning;
        snapshot.Index = 0;
        snapshot.Total = 100;
        snapshot.StartedAt = DateTimeOffset.UtcNow;
        snapshot.Log = new List<string>();
        Snapshot = snapshot;
        RegisterActiveJob();
        await PublishAsync();
    }

    private static string? ActiveJobId
    {
        get => CurrentRun.Value?.ActiveJobId;
        set
        {
            if (CurrentRun.Value is not null)
                CurrentRun.Value.ActiveJobId = value;
        }
    }

    /// <summary>
    /// Promote pre-created queued job to running (or create if none). Publishes SignalR.
    /// </summary>
    private void RegisterActiveJob()
    {
        var run = CurrentRun.Value
                  ?? throw new InvalidOperationException("No active job run context.");
        if (string.IsNullOrWhiteSpace(Snapshot.UserId))
            Snapshot.UserId = run.UserId;
        Snapshot.QueuedAt ??= run.QueuedAt;
        Snapshot.StartedAt ??= DateTimeOffset.UtcNow;
        Snapshot.Status = StatusRunning;
        run.StartedAt = Snapshot.StartedAt;

        if (!string.IsNullOrWhiteSpace(run.ActiveJobId))
        {
            // Promote existing queued → running
            Snapshot.JobId = run.ActiveJobId;
            _jobs.Update(run.ActiveJobId, rec =>
            {
                rec.Status = StatusRunning;
                rec.Kind = Snapshot.Kind;
                rec.Message = Snapshot.Message;
                rec.ProjectId = Snapshot.ProjectId;
                rec.UserId = Snapshot.UserId;
                rec.CharKey = Snapshot.CharKey;
                rec.Scene = Snapshot.Scene;
                rec.Clip = Snapshot.Clip;
                rec.Index = Snapshot.Index;
                rec.Total = Snapshot.Total;
                rec.Log = Snapshot.Log.ToList();
                rec.StartedAt = Snapshot.StartedAt;
                rec.QueuedAt = Snapshot.QueuedAt ?? rec.QueuedAt;
            });
            foreach (var res in run.HeldLocks)
            {
                var existing = _locks.Get(res);
                if (existing is not null &&
                    string.Equals(existing.UserId, run.UserId, StringComparison.OrdinalIgnoreCase))
                {
                    _locks.TryAcquire(res, run.UserId, DefaultLockTtl, existing.Reason, run.ActiveJobId);
                }
            }
            _metrics.NoteJobStarted(Snapshot.Kind ?? "job", run.UserId, run.QueuedAt);
            _ = PublishAsync();
            return;
        }

        // Fallback: create running job when no pre-queued record
        var recNew = _jobs.Create(new JobRecord
        {
            Status = Snapshot.Status,
            Kind = Snapshot.Kind,
            ProjectId = Snapshot.ProjectId,
            UserId = Snapshot.UserId,
            CharKey = Snapshot.CharKey,
            Scene = Snapshot.Scene,
            Clip = Snapshot.Clip,
            Message = Snapshot.Message,
            Index = Snapshot.Index,
            Total = Snapshot.Total,
            QueuedAt = run.QueuedAt,
            StartedAt = Snapshot.StartedAt ?? DateTimeOffset.UtcNow,
            Log = Snapshot.Log.ToList(),
        });
        ActiveJobId = recNew.JobId;
        Snapshot.JobId = recNew.JobId;
        Snapshot.QueuedAt = recNew.QueuedAt;
        _jobCts[recNew.JobId] = run.Cts;
        foreach (var res in run.HeldLocks)
        {
            var existing = _locks.Get(res);
            if (existing is not null &&
                string.Equals(existing.UserId, run.UserId, StringComparison.OrdinalIgnoreCase))
            {
                _locks.TryAcquire(res, run.UserId, DefaultLockTtl, existing.Reason, recNew.JobId);
            }
        }
        _metrics.NoteJobStarted(Snapshot.Kind ?? "job", run.UserId, run.QueuedAt);
        _ = PublishAsync();
    }

    private sealed class JobEnqueueMeta
    {
        public string? Kind { get; set; }
        public string? ProjectId { get; set; }
        public int? Scene { get; set; }
        public int? Clip { get; set; }
        public string? CharKey { get; set; }
        public string Message { get; set; } = "Queued — waiting for worker…";
    }

    /// <summary>
    /// Phase 2: accept job as <c>queued</c> immediately, wait for locks + worker slot, then run.
    /// Hard 409 only when user queue is full, or <paramref name="failIfLocked"/> and lock held by other.
    /// </summary>
    private async Task<JobSnapshot> StartBackgroundJobAsync(
        Func<CancellationToken, Task> work,
        JobEnqueueMeta meta,
        IReadOnlyList<string>? lockResources = null,
        string? lockReason = null,
        bool failIfLocked = false)
    {
        var userId = string.IsNullOrWhiteSpace(_user.UserId) ? "local" : _user.UserId.Trim();
        EnsureCanStart(userId);

        var resources = (lockResources ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ThrowIfLockedByOther(failIfLocked, resources, userId);

        var keys = await ResolveJobApiKeysAsync(userId, meta.ProjectId).ConfigureAwait(false);

        var queuedAt = DateTimeOffset.UtcNow;
        var cts = new CancellationTokenSource();
        var kind = meta.Kind ?? "job";
        var rec = _jobs.Create(new JobRecord
        {
            Status = StatusQueued,
            Kind = kind,
            ProjectId = meta.ProjectId,
            UserId = userId,
            CharKey = meta.CharKey,
            Scene = meta.Scene,
            Clip = meta.Clip,
            Message = meta.Message,
            QueuedAt = queuedAt,
            Log = new List<string> { meta.Message },
        });

        var run = new JobRunState
        {
            UserId = userId,
            ApiKey = keys.ApiKey,
            KeyMode = keys.KeyMode,
            KeyUserId = keys.KeyUserId,
            GeminiApiKey = keys.GeminiApiKey,
            AnthropicApiKey = keys.AnthropicApiKey,
            FalApiKey = keys.FalApiKey,
            SunoApiKey = keys.SunoApiKey,
            AiMusicApiKey = keys.AiMusicApiKey,
            ElevenLabsApiKey = keys.ElevenLabsApiKey,
            QueuedAt = queuedAt,
            Cts = cts,
            ActiveJobId = rec.JobId,
            HeldLocks = new List<string>(),
            Snapshot = rec.ToSnapshot(),
            PendingLockResources = resources,
            LockReason = lockReason,
        };
        _jobCts[rec.JobId] = cts;
        _metrics.NoteJobQueued(kind, userId);
        _ = PublishSnapshotAsync(run.Snapshot);

        _ = Task.Run(() => ExecuteQueuedJobAsync(work, meta, run, userId, kind), CancellationToken.None);

        return rec.ToSnapshot();
    }

    /// <summary>
    /// Hard reject only when the client asks FailIfLocked and the lock is held by someone else.
    /// </summary>
    private void ThrowIfLockedByOther(bool failIfLocked, List<string> resources, string userId)
    {
        if (!failIfLocked)
            return;
        foreach (var res in resources)
        {
            var held = _locks.Get(res);
            if (held is null) continue;
            if (string.Equals(held.UserId, userId, StringComparison.OrdinalIgnoreCase))
                continue;
            _metrics.NoteLockConflict();
            throw new LockConflictException(res, held.UserId, held.ExpiresAt);
        }
    }

    private sealed class JobApiKeys
    {
        public string KeyUserId { get; init; } = "";
        public string KeyMode { get; init; } = "";
        public string? ApiKey { get; init; }
        public string? GeminiApiKey { get; init; }
        public string? AnthropicApiKey { get; init; }
        public string? FalApiKey { get; init; }
        public string? SunoApiKey { get; init; }
        public string? AiMusicApiKey { get; init; }
        public string? ElevenLabsApiKey { get; init; }
    }

    /// <summary>
    /// I5 / P2: shared → owner's keys; personal → actor's keys. Fail-open to personal on ACL errors.
    /// </summary>
    private async Task<JobApiKeys> ResolveJobApiKeysAsync(string userId, string? projectId)
    {
        var keyUserId = userId;
        var keyMode = PageToMovie.Engine.Collaboration.ProjectKeyModes.Personal;
        if (!string.IsNullOrWhiteSpace(projectId) && _acl is not null)
        {
            try
            {
                var aclDoc = await _acl.GetOrCreateAclAsync(projectId, userId).ConfigureAwait(false);
                keyMode = PageToMovie.Engine.Collaboration.ProjectKeyModes.Normalize(aclDoc.KeyMode);
                if (PageToMovie.Engine.Collaboration.ProjectKeyModes.IsShared(keyMode)
                    && !string.IsNullOrWhiteSpace(aclDoc.OwnerUserId))
                    keyUserId = aclDoc.OwnerUserId.Trim();
            }
            catch { /* fail-open personal */ }
        }

        var apiKey = await ResolveGrokApiKeyAsync(keyUserId).ConfigureAwait(false);
        var geminiKey = await _keys.GetKeyAsync(keyUserId, "gemini").ConfigureAwait(false);
        var anthropicKey = await _keys.GetKeyAsync(keyUserId, "anthropic").ConfigureAwait(false);
        var falKey = await _keys.GetKeyAsync(keyUserId, "fal").ConfigureAwait(false);
        var sunoKey = await _keys.GetKeyAsync(keyUserId, "suno").ConfigureAwait(false);
        var aiMusicApiKey = await _keys.GetKeyAsync(keyUserId, "aimusicapi").ConfigureAwait(false);
        var elevenLabsKey = await _keys.GetKeyAsync(keyUserId, "elevenlabs").ConfigureAwait(false);
        return new JobApiKeys
        {
            KeyUserId = keyUserId,
            KeyMode = keyMode,
            ApiKey = apiKey,
            GeminiApiKey = geminiKey,
            AnthropicApiKey = anthropicKey,
            FalApiKey = falKey,
            SunoApiKey = sunoKey,
            AiMusicApiKey = aiMusicApiKey,
            ElevenLabsApiKey = elevenLabsKey,
        };
    }

    private async Task<string?> ResolveGrokApiKeyAsync(string keyUserId)
    {
        if (!string.IsNullOrWhiteSpace(_user.RequestApiKey))
            return _user.RequestApiKey;
        return await _keys.GetKeyAsync(keyUserId, "grok").ConfigureAwait(false);
    }

    private async Task ExecuteQueuedJobAsync(
        Func<CancellationToken, Task> work,
        JobEnqueueMeta meta,
        JobRunState run,
        string userId,
        string kind)
    {
        CurrentRun.Value = run;
        using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["grok"] = run.ApiKey,
            ["gemini"] = run.GeminiApiKey,
            ["anthropic"] = run.AnthropicApiKey,
            ["fal"] = run.FalApiKey,
            ["suno"] = run.SunoApiKey,
            ["aimusicapi"] = run.AiMusicApiKey,
            ["elevenlabs"] = run.ElevenLabsApiKey,
        }))
        using (UserApiCallScope.Push(run.UserId))
        {
            var startedAt = DateTimeOffset.UtcNow;
            var success = false;
            try
            {
                // Wait for locks (queued stays visible via SignalR messages)
                await WaitForLocksAsync(run, run.Cts.Token);

                await UpdateQueuedMessageAsync(run, "Waiting for worker slot…");

                await _apiPool.RunAsync(
                    userId,
                    ct => RunQueuedWorkAsync(work, meta, run, ct),
                    run.Cts.Token);

                var status = CurrentRun.Value?.Snapshot.Status;
                success = string.Equals(status, StatusDone, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                await TryFinishCancelledQueuedJobAsync();
            }
            catch (LockConflictException ex)
            {
                await TryFinishLockConflictJobAsync(ex);
            }
            catch (Exception ex)
            {
                await TryFinishFailedQueuedJobAsync(ex);
            }
            finally
            {
                FinalizeQueuedJob(run, userId, kind, startedAt, success);
            }
        }
    }

    private static async Task RunQueuedWorkAsync(
        Func<CancellationToken, Task> work,
        JobEnqueueMeta meta,
        JobRunState run,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token, ct);
        // Bind api_calls telemetry to this job's project for the async flow
        using var tel = CreateJobTelemetryScope(meta.ProjectId);
        await work(linked.Token);
    }

    private static IDisposable? CreateJobTelemetryScope(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return null;
        return ProjectTelemetryService.UseProject(projectId);
    }

    private async Task TryFinishCancelledQueuedJobAsync()
    {
        try
        {
            if (CurrentRun.Value?.Snapshot is { } s &&
                !string.Equals(s.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.Status, StatusDone, StringComparison.OrdinalIgnoreCase))
            {
                await FinishAsync(StatusCancelled, CancelledByUser);
            }
        }
        catch { /* ignore */ }
    }

    private async Task TryFinishLockConflictJobAsync(LockConflictException ex)
    {
        _metrics.NoteLockConflict();
        try
        {
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
        catch { /* ignore */ }
    }

    private async Task TryFinishFailedQueuedJobAsync(Exception ex)
    {
        _log.LogError(ex, "Background job failed");
        try
        {
            if (CurrentRun.Value?.Snapshot is { } s &&
                (string.Equals(s.Status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.Status, StatusQueued, StringComparison.OrdinalIgnoreCase)))
            {
                await FinishAsync(StatusError, ex.Message, ex.Message);
            }
        }
        catch { /* ignore */ }
    }

    private void FinalizeQueuedJob(
        JobRunState run,
        string userId,
        string kind,
        DateTimeOffset startedAt,
        bool success)
    {
        var kindDone = CurrentRun.Value?.Snapshot.Kind ?? kind;
        var q = run.QueuedAt;
        var st = run.StartedAt ?? startedAt;
        var snapStatus = CurrentRun.Value?.Snapshot.Status;
        success = ResolveQueuedJobSuccess(snapStatus, success);

        _metrics.NoteJobFinished(kindDone, userId, success, q, st);

        foreach (var res in run.HeldLocks)
            _locks.Release(res, userId);

        ReleaseQueuedJobLocks(run);

        CurrentRun.Value = null;
    }

    private static bool ResolveQueuedJobSuccess(string? snapStatus, bool success)
    {
        if (string.Equals(snapStatus, StatusDone, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(snapStatus, StatusError, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapStatus, StatusCancelled, StringComparison.OrdinalIgnoreCase))
            return false;
        return success;
    }

    private void ReleaseQueuedJobLocks(JobRunState run)
    {
        if (string.IsNullOrWhiteSpace(run.ActiveJobId))
            return;
        if (_jobCts.TryRemove(run.ActiveJobId, out var finishedCts))
            finishedCts.Dispose();
        _locks.ReleaseAllForJob(run.ActiveJobId);
    }

    private async Task WaitForLocksAsync(JobRunState run, CancellationToken ct)
    {
        var resources = run.PendingLockResources;
        if (resources.Count == 0)
            return;

        await UpdateQueuedMessageAsync(run, "Waiting for resource lock…");

        while (!ct.IsCancellationRequested)
        {
            ThrowIfQueuedJobCancelled(run);

            if (TryAcquireAllPendingLocks(run, resources, out var acquired, out var blockedResource, out var blockedOwner))
            {
                run.HeldLocks = acquired;
                await UpdateQueuedMessageAsync(run, "Lock acquired — waiting for worker…");
                return;
            }

            ReleaseAcquiredLocks(acquired, run.UserId);
            await UpdateQueuedMessageAsync(run, FormatLockWaitMessage(blockedResource, blockedOwner));
            await Task.Delay(300, ct);
        }

        throw new OperationCanceledException("Cancelled while waiting for lock");
    }

    private void ThrowIfQueuedJobCancelled(JobRunState run)
    {
        var job = !string.IsNullOrEmpty(run.ActiveJobId) ? _jobs.Get(run.ActiveJobId) : null;
        if (job is not null &&
            string.Equals(job.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationCanceledException("Job cancelled");
        }
    }

    private bool TryAcquireAllPendingLocks(
        JobRunState run,
        List<string> resources,
        out List<string> acquired,
        out string? blockedResource,
        out string? blockedOwner)
    {
        acquired = new List<string>();
        blockedResource = null;
        blockedOwner = null;
        foreach (var res in resources)
        {
            if (TryAcquireOnePendingLock(run, res, acquired, out var holderUserId))
                continue;
            blockedResource = res;
            blockedOwner = holderUserId;
            return false;
        }
        return true;
    }

    private bool TryAcquireOnePendingLock(JobRunState run, string res, List<string> acquired, out string? holderUserId)
    {
        holderUserId = null;
        if (_locks.TryAcquire(res, run.UserId, DefaultLockTtl, run.LockReason, run.ActiveJobId))
        {
            acquired.Add(res);
            return true;
        }

        var holder = _locks.Get(res);
        holderUserId = holder?.UserId;
        if (holder is not null &&
            string.Equals(holder.UserId, run.UserId, StringComparison.OrdinalIgnoreCase))
        {
            // Already ours
            acquired.Add(res);
            return true;
        }

        return false;
    }

    private void ReleaseAcquiredLocks(List<string> acquired, string userId)
    {
        foreach (var a in acquired)
            _locks.Release(a, userId);
    }

    private static string FormatLockWaitMessage(string? blockedResource, string? blockedOwner) =>
        string.IsNullOrEmpty(blockedOwner)
            ? $"Waiting for lock {blockedResource}…"
            : $"Waiting for lock (held by {blockedOwner})…";

    private async Task UpdateQueuedMessageAsync(JobRunState run, string message)
    {
        if (string.IsNullOrEmpty(run.ActiveJobId)) return;
        run.Snapshot.Message = message;
        run.Snapshot.Status = StatusQueued;
        if (run.Snapshot.Log.Count == 0 || run.Snapshot.Log[^1] != message)
        {
            run.Snapshot.Log.Add(message);
            if (run.Snapshot.Log.Count > 120)
                run.Snapshot.Log = run.Snapshot.Log.TakeLast(120).ToList();
        }
        _jobs.Update(run.ActiveJobId, rec =>
        {
            if (string.Equals(rec.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase))
                return;
            rec.Status = StatusQueued;
            rec.Message = message;
            rec.Log = run.Snapshot.Log.ToList();
        });
        await PublishSnapshotAsync(run.Snapshot);
    }

    private async Task PublishSnapshotAsync(JobSnapshot snap)
    {
        if (_sink is not null)
            await _sink.OnJobUpdatedAsync(Clone(snap));
    }

    public Task<JobSnapshot> StartSceneGenAsync(StartSceneGenRequest req)
    {
        if (req.Scene <= 0)
            throw new InvalidOperationException("scene required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunSceneGenAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "scene",
                ProjectId = projectId,
                Scene = req.Scene,
                Clip = req.Clip,
                Message = $"Queued scene S{req.Scene:D2} gen…",
            },
            lockResources: new[] { LockKeys.Scene(projectId, req.Scene) },
            lockReason: $"scene gen S{req.Scene:D2}",
            failIfLocked: req.FailIfLocked);
    }

    public Task<JobSnapshot> StartBatchGenAsync(StartBatchGenRequest req)
    {
        var hasClips = req.Clips is { Count: > 0 };
        if ((req.Scenes is null || req.Scenes.Count == 0) && !hasClips)
            throw new InvalidOperationException("At least one scene or clip is required.");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        var sceneNumbers = hasClips
            ? (req.Clips ?? new List<ClipTarget>()).Select(c => c.Scene)
            : req.Scenes ?? new List<int>();
        var locks = sceneNumbers
            .Where(s => s > 0)
            .Select(s => LockKeys.Scene(projectId, s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var queuedMsg = hasClips
            ? $"Queued batch gen ({(req.Clips ?? new List<ClipTarget>()).Count} clip(s))…"
            : $"Queued batch gen ({(req.Scenes ?? new List<int>()).Count} scenes)…";
        return StartBackgroundJobAsync(
            ct => RunBatchGenAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "batch",
                ProjectId = projectId,
                Message = queuedMsg,
            },
            lockResources: locks,
            lockReason: "batch scene gen",
            failIfLocked: req.FailIfLocked);
    }

    /// <summary>
    /// Resolve the effective project id (from the request, else the active project) — throwing
    /// ProjectIdRequired when neither is set — and default a blank character key to the
    /// narrator pseudo-character. Shared prologue for voice/speak job starts.
    /// </summary>
    private (string projectId, string charKey) ResolveProjectAndCharKey(string? reqProjectId, string? reqCharKey)
    {
        if (string.IsNullOrWhiteSpace(reqProjectId) && string.IsNullOrWhiteSpace(_projects.ActiveProjectId))
            throw new InvalidOperationException(ProjectIdRequired);
        var projectId = string.IsNullOrWhiteSpace(reqProjectId)
            ? _projects.ActiveProjectId
            : reqProjectId.Trim();
        var charKey = string.IsNullOrWhiteSpace(reqCharKey) ? "Character_Narrator" : reqCharKey.Trim();
        return (projectId, charKey);
    }

    /// <summary>
    /// Batch TTS for re-voice: synthesize dialogue with the character's stored clone voice id.
    /// Writes <c>assets/audio/revoice/scene_XX_clip_YY.*</c> and hands each file to the client via
    /// <see cref="JobSnapshot.ClientMediaUrl"/> (SignalR). Provider keys stay on the server.
    /// </summary>
    public Task<JobSnapshot> StartSpeakBatchAsync(StartSpeakBatchRequest req)
    {
        var (projectId, charKey) = ResolveProjectAndCharKey(req.ProjectId, req.CharKey);

        // Locks: character seed (voice) + any scenes we know up front; full scene set may expand
        // after blueprint load inside the runner.
        var locks = new List<string> { LockKeys.Character(projectId, charKey) };
        if (req.Clips is { Count: > 0 })
        {
            foreach (var sn in req.Clips.Select(c => c.Scene).Where(s => s > 0).Distinct())
                locks.Add(LockKeys.Scene(projectId, sn));
        }

        var n = req.Clips?.Count ?? 0;
        var queuedMsg = n > 0
            ? $"Queued speak-batch ({n} clip(s)) for {charKey}…"
            : $"Queued speak-batch (auto lines) for {charKey}…";

        return StartBackgroundJobAsync(
            ct => RunSpeakBatchAsync(req, projectId, charKey, ct),
            new JobEnqueueMeta
            {
                Kind = "speak-batch",
                ProjectId = projectId,
                CharKey = charKey,
                Message = queuedMsg,
            },
            lockResources: locks,
            lockReason: $"speak-batch {charKey}",
            failIfLocked: req.FailIfLocked);
    }

    /// <summary>Book → Fountain draft + approve. Requires XAI_API_KEY.</summary>
    public Task<JobSnapshot> StartStage1Async(StartStage1Request req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunStage1Async(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "stage1",
                ProjectId = projectId,
                Message = "Queued Stage 1…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "stage1");
    }

    /// <summary>Stage 2 planner (Fountain → blueprint). Deterministic C#; no API key.</summary>
    public Task<JobSnapshot> StartStage2Async(StartStage2Request req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunStage2Async(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = KindStage2,
                ProjectId = projectId,
                Message = "Queued Stage 2…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: KindStage2);
    }

    /// <summary>C# PDF extract + optional Grok vision OCR → book_full.txt (prepare only).</summary>
    public Task<JobSnapshot> StartBookPrepareAsync(StartBookPrepareRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException(ProjectIdRequired);
        return StartBackgroundJobAsync(
            ct => RunBookPrepareAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "book_prepare",
                ProjectId = req.ProjectId,
                Message = "Queued book prepare…",
            },
            lockResources: new[] { LockKeys.Stage(req.ProjectId) },
            lockReason: "book prepare");
    }

    /// <summary>
    /// Full import path: prepare book text (unless skipped) then book→Fountain draft.
    /// Use for PDF/TXT Import; Screenplay “draft from book” can set <see cref="StartBookImportRequest.SkipPrepare"/>.
    /// </summary>
    public Task<JobSnapshot> StartBookImportAsync(StartBookImportRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException(ProjectIdRequired);
        return StartBackgroundJobAsync(
            ct => RunBookImportAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "book_import",
                ProjectId = req.ProjectId,
                Message = req.SkipPrepare
                    ? "Queued screenplay draft from book…"
                    : "Queued book import (prepare + screenplay)…",
            },
            lockResources: new[] { LockKeys.Stage(req.ProjectId) },
            lockReason: "book import");
    }

    private async Task RunBookPrepareAsync(StartBookPrepareRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        await _projects.RequireProjectAsync(projectId, ct);
        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "book_prepare",
            ProjectId = projectId,
            Message = "Preparing book (PDF extract / vision OCR)…",
            Index = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync("Book prepare (C# PdfPig + optional Grok vision)");
            var result = await _books.PrepareAsync(
                projectId,
                forceExtract: req.ForceExtract,
                forceVision: req.ForceVision,
                autoVision: req.AutoVision,
                visionModel: await ResolveVisionModelAsync(projectId, req.VisionModel, ct).ConfigureAwait(false),
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    if (line.Contains("Extract", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = 1; s.Message = line; });
                    else if (line.Contains("Vision", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("page", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = Math.Max(s.Index, 2); s.Message = line; });
                    else
                        _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            await UpdateAsync(s => s.Index = 3);
            var msg = result.ReadyForStage1
                ? $"Book ready · {result.TextWords} words · quality={result.TextQuality} · {result.TextEngine}"
                : $"Book prepared but Stage 1 not ready · {result.Strategy}: {result.StrategyReason}";
            await FinishAsync(result.Ok ? StatusDone : StatusError, msg, result.Ok ? null : msg);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Book prepare failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private async Task RunBookImportAsync(StartBookImportRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);

        // Progress: 0–4 prepare, 5–10 adapt (chunk messages bump index)
        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "book_import",
            ProjectId = projectId,
            Message = req.SkipPrepare
                ? "Writing screenplay from book…"
                : "Importing book (prepare + screenplay)…",
            Index = 0,
            Total = 10,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync().ConfigureAwait(false);

        try
        {
            // Ambient job scope is pushed before this runs — log which key source is active.
            await AppendLogAsync($"AI key source for import: {ResolveImportKeySourceHint()}").ConfigureAwait(false);

            if (!_chat.IsConfigured)
            {
                await FinishAsync(StatusError,
                    "API key missing. A Grok key is set in Configuration only if it decrypts for this user. " +
                    "Re-save the key after each redeploy unless Railway has a Volume at /data. " +
                    "Or set server env XAI_API_KEY.",
                    "API key missing. Re-save Grok key in Configuration (needs Volume at /data) or set XAI_API_KEY env.").ConfigureAwait(false);
                return;
            }

            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
            var needPrepare = !req.SkipPrepare;

            // TXT may already have book_full after upload; still allow force extract for PDF
            NoteLightSkipPrepareIfApplicable(needPrepare, bookPath, req);

            if (!await TryRunBookImportPreparePhaseAsync(req, projectId, needPrepare, ct).ConfigureAwait(false))
                return;

            if (!File.Exists(bookPath))
            {
                await FinishAsync(StatusError, "No book text after prepare",
                    "No book text after prepare").ConfigureAwait(false);
                return;
            }

            var save = await TryRunBookImportAdaptPhaseAsync(req, projectId, ct).ConfigureAwait(false);
            if (save is null)
                return;

            await UpdateAsync(s => s.Index = 10).ConfigureAwait(false);
            await FinishAsync(
                StatusDone,
                save.Message ?? "Screenplay draft ready — review and approve").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Book import failed");
            await FinishAsync(StatusError, ex.Message, ex.Message).ConfigureAwait(false);
        }
    }

    private static string ResolveImportKeySourceHint()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyScope.Current))
            return "personal/scope";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY")))
            return "server XAI_API_KEY env";
        return "none";
    }

    /// <summary>
    /// TXT may already have book_full after upload; still allow force extract for PDF.
    /// Light skip if text already good and not forcing — still run prepare for PDF path consistency.
    /// Import always sets ForceExtract=true for PDF; SkipPrepare for re-draft only.
    /// </summary>
    private static void NoteLightSkipPrepareIfApplicable(bool needPrepare, string bookPath, StartBookImportRequest req)
    {
        if (needPrepare && File.Exists(bookPath) && !req.ForceExtract && !req.ForceVision)
        {
            // Light skip if text already good and not forcing — still run prepare for PDF path consistency
            // Import always sets ForceExtract=true for PDF; SkipPrepare for re-draft only.
        }
    }

    private static void ApplyBookPrepareProgress(JobSnapshot s, string line)
    {
        s.Message = line;
        if (line.Contains("Extract", StringComparison.OrdinalIgnoreCase))
            s.Index = Math.Max(s.Index, 2);
        else if (ContainsAnyIgnoreCase(line, "Vision", "page"))
            s.Index = Math.Max(s.Index, 3);
        else
            s.Index = Math.Max(s.Index, 2);
    }

    private static void ApplyChunkAdaptIndex(JobSnapshot s, string line)
    {
        var m = CommonRegex.Match(line, @"(\d+)\s*/\s*(\d+)");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var cur) &&
            int.TryParse(m.Groups[2].Value, out var tot) &&
            tot > 0)
        {
            s.Index = 5 + (int)Math.Round(4.0 * Math.Clamp(cur, 0, tot) / tot);
        }
        else
            s.Index = Math.Max(s.Index, 6);
    }

    private static void ApplyBookAdaptProgress(JobSnapshot s, string line)
    {
        s.Message = line;
        // Map adapt progress into 5–9
        if (line.Contains("chunk", StringComparison.OrdinalIgnoreCase))
            ApplyChunkAdaptIndex(s, line);
        else if (ContainsAnyIgnoreCase(line, "Merge", "Stitch"))
            s.Index = Math.Max(s.Index, 9);
        else if (ContainsAnyIgnoreCase(line, "repair", "retry"))
            s.Index = Math.Max(s.Index, 8);
        else
            s.Index = Math.Max(s.Index, 6);
    }

    private static bool ContainsAnyIgnoreCase(string line, params string[] needles) =>
        needles.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> TryRunBookImportPreparePhaseAsync(
        StartBookImportRequest req,
        string projectId,
        bool needPrepare,
        CancellationToken ct)
    {
        if (!needPrepare)
        {
            await AppendLogAsync("Skipping prepare — using existing book text").ConfigureAwait(false);
            return true;
        }

        await AppendLogAsync("Phase 1: prepare book text").ConfigureAwait(false);
        await UpdateAsync(s =>
        {
            s.Index = 1;
            s.Message = "Reading book…";
        }).ConfigureAwait(false);

        var prep = await _books.PrepareAsync(
            projectId,
            forceExtract: req.ForceExtract,
            forceVision: req.ForceVision,
            autoVision: req.AutoVision,
            visionModel: await ResolveVisionModelAsync(projectId, req.VisionModel, ct).ConfigureAwait(false),
            onProgress: line =>
            {
                _ = AppendLogAsync(line);
                _ = UpdateAsync(s => ApplyBookPrepareProgress(s, line));
            },
            ct: ct).ConfigureAwait(false);

        if (!prep.Ok)
        {
            await FinishAsync(StatusError, prep.StrategyReason ?? "Book prepare failed",
                prep.StrategyReason ?? "Book prepare failed").ConfigureAwait(false);
            return false;
        }

        await AppendLogAsync(
            $"Book text ready · {prep.TextWords} words · {prep.TextEngine}").ConfigureAwait(false);
        return true;
    }

    private async Task<ScreenplayService.SaveResult?> TryRunBookImportAdaptPhaseAsync(
        StartBookImportRequest req,
        string projectId,
        CancellationToken ct)
    {
        await UpdateAsync(s =>
        {
            s.Index = 5;
            s.Message = "Writing screenplay draft…";
        }).ConfigureAwait(false);
        await AppendLogAsync("Phase 2: book → Fountain screenplay").ConfigureAwait(false);

        if (!_chat.IsConfigured)
        {
            await FinishAsync(StatusError, "Chat service not configured",
                "Chat service not configured").ConfigureAwait(false);
            return null;
        }

        var model = await ResolvePlanningModelAsync(projectId, req.Model, ct).ConfigureAwait(false);
        var save = await ScreenplayService.CreateDraftFromBookAsync(
            _projects,
            projectId,
            _chat,
            model: model,
            adaptationDefaults: _opts.AdaptationDefaults,
            onProgress: line =>
            {
                _ = AppendLogAsync(line);
                _ = UpdateAsync(s => ApplyBookAdaptProgress(s, line));
            },
            ct: ct,
            errorLogger: _errorLogger,
            jobId: Snapshot.JobId,
            bookRegistry: _bookRegistry,
            cacheUserId: _user.UserId,
            bookFileSessionFactory: _bookFileSessionFactory,
            responses: _xaiResponses,
            useFakes: _opts.UseFakes,
            fountainFileSessionFactory: _fountainFileSessionFactory).ConfigureAwait(false);

        if (save.Ok)
            return save;

        await FinishAsync(StatusError, save.Error ?? "Screenplay draft failed",
            save.Error ?? "Screenplay draft failed").ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Prompt-based edit of an already-generated clip (xAI /v1/videos/edits) — explicit,
    /// human-triggered, per-clip only. Its own job kind ("video_edit"), entirely separate from
    /// <see cref="StartSceneGenAsync"/>/<see cref="StartBatchGenAsync"/>'s automatic pipeline; never
    /// called from either.
    /// </summary>
    public Task<JobSnapshot> StartVideoEditAsync(StartVideoEditRequest req)
    {
        if (req.Scene <= 0 || req.Clip <= 0)
            throw new InvalidOperationException("scene and clip required");
        if (string.IsNullOrWhiteSpace(req.Prompt))
            throw new InvalidOperationException("prompt required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunVideoEditAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "video_edit",
                ProjectId = projectId,
                Scene = req.Scene,
                Clip = req.Clip,
                Message = $"Queued AI edit for S{req.Scene:D2}C{req.Clip:D2}…",
            },
            lockResources: new[] { LockKeys.Scene(projectId, req.Scene) },
            lockReason: $"video edit S{req.Scene:D2}C{req.Clip:D2}");
    }

    /// <summary>Generate portrait variants via C# Grok image API.</summary>
    public Task<JobSnapshot> StartCharacterVariantsAsync(StartCharacterVariantsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CharKey))
            throw new InvalidOperationException("charKey required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunCharacterVariantsAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "character_variants",
                ProjectId = projectId,
                CharKey = req.CharKey,
                Message = $"Queued portrait gen for {req.CharKey}…",
            },
            lockResources: new[] { LockKeys.Character(projectId, req.CharKey) },
            lockReason: $"char variants {req.CharKey}");
    }

    /// <summary>Generate location set plate variants via Grok image API.</summary>
    public Task<JobSnapshot> StartLocationVariantsAsync(StartLocationVariantsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LocKey))
            throw new InvalidOperationException("locKey required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunLocationVariantsAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "location_variants",
                ProjectId = projectId,
                CharKey = req.LocKey,
                Message = $"Queued set plate gen for {req.LocKey}…",
            },
            lockResources: new[] { LockKeys.Location(projectId, req.LocKey) },
            lockReason: $"loc variants {req.LocKey}");
    }

    /// <summary>
    /// Batch generate + vision auto-lock looks for every used-in-plan cast face and location.
    /// </summary>
    public Task<JobSnapshot> StartPlanLooksAsync(StartPlanLooksRequest req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(ProjectIdRequired);
        return StartBackgroundJobAsync(
            ct => RunPlanLooksAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "plan_looks",
                ProjectId = projectId,
                Message = "Queued looks for plan cast + places…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "plan looks batch");
    }

    /// <summary>
    /// Admin / one-shot: enrich the full-length screenplay (visual action from the book).
    /// Long chat pass — must not be a blocking HTTP request.
    /// </summary>
    public Task<JobSnapshot> StartEmbellishAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            projectId = _projects.ActiveProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(ProjectIdRequired);
        return StartBackgroundJobAsync(
            ct => RunEmbellishAsync(projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "embellish",
                ProjectId = projectId,
                Message = "Queued screenplay enrich…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "screenplay enrich");
    }

    /// <summary>Background music (or singing) for one scene via audio API (client saves the
    /// segment(s)). <paramref name="model"/> overrides the project's configured audio_model_name for
    /// this run only; <paramref name="isVocal"/> requests sung vocals (Suno-family models only).</summary>
    public Task<JobSnapshot> StartSceneMusicGenAsync(string projectId, int scene, string? model = null, bool isVocal = false)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            projectId = _projects.ActiveProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(ProjectIdRequired);
        return StartBackgroundJobAsync(
            ct => RunSceneMusicGenAsync(projectId, scene, model, isVocal, ct),
            new JobEnqueueMeta
            {
                Kind = "music",
                ProjectId = projectId,
                Message = isVocal
                    ? $"Queued singing for Scene {scene:D2}…"
                    : $"Queued background music for Scene {scene:D2}…",
            },
            lockResources: new[] { LockKeys.Wip(projectId) },
            lockReason: $"scene {scene} music gen");
    }

    private sealed class SceneMusicGenRun
    {
        public required string ProjectDir { get; init; }
        public required int Scene { get; init; }
        public required SupportedModelEntry Entry { get; init; }
        public required string Prompt { get; init; }
        public required string? Lyrics { get; init; }
        public required bool EffectiveIsVocal { get; init; }
        public required string TakeId { get; init; }
        public required int TotalDuration { get; init; }
        public required int SegLen { get; init; }
        public required int SegmentCount { get; init; }
    }

    private async Task RunSceneMusicGenAsync(string projectId, int scene, string? modelOverride, bool isVocal, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);
        await InitAndPublishJobAsync(new JobSnapshot
        {
            Kind = "music",
            ProjectId = projectId,
            Scene = scene,
            Message = $"Generating background music for Scene {scene:D2}…",
        });
        try
        {
            var run = await TryPrepareSceneMusicGenAsync(projectId, scene, modelOverride, isVocal, ct)
                .ConfigureAwait(false);
            if (run is null)
                return;

            var (savedSegments, lastProviderNote, segmentFileNames) =
                await GenerateMusicSegmentsAsync(run, ct).ConfigureAwait(false);
            if (savedSegments == 0)
            {
                await FinishSceneMusicNoSegmentsAsync(run.Entry, lastProviderNote).ConfigureAwait(false);
                return;
            }

            await TryWriteMusicSidecarAsync(run, segmentFileNames, ct).ConfigureAwait(false);
            await UpdateAsync(s => s.Index = 100);
            await FinishAsync(StatusDone,
                $"{(run.EffectiveIsVocal ? "Singing" : "Background music")} ready ({savedSegments} segment(s)) — save to media folder");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scene music gen failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private async Task<SceneMusicGenRun?> TryPrepareSceneMusicGenAsync(
        string projectId, int scene, string? modelOverride, bool isVocal, CancellationToken ct)
    {
        if (!_audio.IsConfigured)
        {
            await FinishAsync(StatusError, "Audio synthesis API key missing.", "Audio synthesis API key missing.");
            return null;
        }

        var pDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        EnsureLabModelsAllowed(cfg);

        var enableMusic = SceneMusicScoringService.GetConfigBool(cfg, "enable_background_music", true);
        var configuredModel = SceneMusicScoringService.GetConfigStr(cfg, "audio_model_name", "");
        // Per-run override wins when given; otherwise fall back to the project's configured
        // default (itself blank-safe — SupportedModelCatalog.ResolveOrDefault below resolves an
        // empty/unset model to the catalog's own dynamic default) — the Configuration page's
        // global picker is unaffected either way.
        var audioModel = string.IsNullOrWhiteSpace(modelOverride) ? configuredModel : modelOverride;
        if (!enableMusic || string.Equals(audioModel, "none", StringComparison.OrdinalIgnoreCase))
        {
            await FinishAsync(StatusDone, "Background music disabled in settings.");
            return null;
        }

        var detail = await _projects.GetSceneDetailAsync(projectId, scene, probeDurations: false, ct: ct).ConfigureAwait(false);
        var totalDuration = Math.Max(1, (int)Math.Ceiling(detail?.DurationSeconds ?? 10));
        var screenplay = detail?.Setting ?? "";
        var planningModel = ProjectModelSelection.RequirePlanning(cfg, "Scene music composition");

        var prompt = await _musicScoring.GetOrComposeMusicPromptAsync(
            pDir, scene, screenplay, totalDuration, planningModel, ct).ConfigureAwait(false);
        await AppendLogAsync($"Music prompt: {prompt}");

        var entry = SupportedModelCatalog.ResolveOrDefault(audioModel, ModelCapability.Audio);

        // Catalog SupportsVocals only — never infer from provider family.
        var canSing = entry.SupportsVocals;
        var effectiveIsVocal = isVocal && canSing;
        if (isVocal && !canSing)
            await AppendLogAsync($"  [{entry.DisplayName}] has no vocal capability (supportsVocals=false) — generating instrumental instead.");

        string? lyrics = null;
        if (effectiveIsVocal)
        {
            lyrics = await _musicScoring.ComposeSceneLyricsAsync(screenplay, totalDuration, planningModel, ct).ConfigureAwait(false);
            await AppendLogAsync($"Lyrics: {lyrics}");
        }

        // Ties every segment of this run together in take history — minted once, not per segment
        // (segments are generated minutes apart via real provider polling, so each computing its
        // own timestamp would scatter one take's files across unrelated-looking history entries).
        var takeId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        // Catalog maxAudioDurationSeconds only — never invent full-scene length as a segment cap.
        if (entry.MaxAudioDurationSeconds is not { } maxAudio || maxAudio <= 0)
            throw new InvalidOperationException(
                $"Audio model '{entry.Id}' has no maxAudioDurationSeconds in models_catalog.json. " +
                "Add the real API limit — do not invent a default.");
        var segLen = Math.Max(1, maxAudio);
        return new SceneMusicGenRun
        {
            ProjectDir = pDir,
            Scene = scene,
            Entry = entry,
            Prompt = prompt,
            Lyrics = lyrics,
            EffectiveIsVocal = effectiveIsVocal,
            TakeId = takeId,
            TotalDuration = totalDuration,
            SegLen = segLen,
            SegmentCount = (int)Math.Ceiling(totalDuration / (double)segLen),
        };
    }

    private async Task<(int Saved, string? LastProviderNote, List<string> FileNames)> GenerateMusicSegmentsAsync(
        SceneMusicGenRun run, CancellationToken ct)
    {
        var savedSegments = 0;
        var segmentFileNames = new List<string>();
        string? lastProviderNote = null;
        var entry = run.Entry;

        for (var seg = 1; seg <= run.SegmentCount; seg++)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = run.TotalDuration - (seg - 1) * run.SegLen;
            var segDuration = Math.Clamp(remaining, 1, run.SegLen);

            await AppendLogAsync($"  [{entry.DisplayName}] generating segment {seg}/{run.SegmentCount} ({segDuration}s)…");
            var url = await _audio.GenerateMusicTrackAsync(
                run.Prompt, segDuration, entry.Id, ct,
                onProgress: msg => { lastProviderNote = msg; _ = AppendLogAsync("  " + msg); },
                isVocal: run.EffectiveIsVocal, lyrics: run.Lyrics).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                await AppendLogAsync($"  [{entry.DisplayName}] segment {seg} failed — stopping.");
                break;
            }

            var relPath = MediaRegistryService.MusicSegmentRelativePath(run.Scene, seg);
            var ticket = _mediaProxy.Issue(url, TimeSpan.FromMinutes(45));
            var clientUrl = $"/api/media/proxy/{ticket}";
            var takeId = run.TakeId;
            var scene = run.Scene;
            var segmentCount = run.SegmentCount;
            await UpdateAsync(s =>
            {
                s.ClientMediaUrl = clientUrl;
                s.ClientRelativePath = relPath;
                s.MusicTakeId = takeId;
                s.Scene = scene;
                s.Index = (int)Math.Round(seg * 100.0 / segmentCount);
            });
            await AppendLogAsync($"  segment {seg} ready for client save → {relPath}");
            segmentFileNames.Add(Path.GetFileName(relPath));
            savedSegments++;
        }

        return (savedSegments, lastProviderNote, segmentFileNames);
    }

    private async Task FinishSceneMusicNoSegmentsAsync(SupportedModelEntry entry, string? lastProviderNote)
    {
        // Name the resolved provider — the top-level _audio.IsConfigured gate only checks
        // that *some* audio provider has a key, not that the one audio_model_name actually
        // routes to (MultiProviderAudioClient) does. A key set for a different provider than
        // the configured audio_model_name fails every scene this way, silently otherwise.
        // Surface the provider's real reason instead of a generic "synthesis failed": the
        // audio client sends its HTTP error via onProgress before returning null.
        var providerDetail = FormatMusicProviderDetail(entry.DisplayName, lastProviderNote);
        await FinishAsync(StatusError,
            $"No music came back from {entry.DisplayName}.{providerDetail} " +
            "Most likely its API key isn’t configured — the audio gate only checks that some " +
            "provider has a key, not the one this model uses. Add its key in Configuration, or pick a different audio model.",
            $"Music synthesis failed for all segments via {entry.DisplayName} ({entry.Id}).{providerDetail}");
    }

    private static string FormatMusicProviderDetail(string displayName, string? lastProviderNote)
    {
        if (string.IsNullOrWhiteSpace(lastProviderNote))
            return "";
        if (!lastProviderNote.Contains("fail", StringComparison.OrdinalIgnoreCase)
            && !lastProviderNote.Contains(StatusError, StringComparison.OrdinalIgnoreCase)
            && !lastProviderNote.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
            && !lastProviderNote.Contains("key", StringComparison.OrdinalIgnoreCase))
            return "";
        return $" {displayName} said: “{lastProviderNote.Trim()}”.";
    }

    private async Task TryWriteMusicSidecarAsync(SceneMusicGenRun run, List<string> segmentFileNames, CancellationToken ct)
    {
        if (_musicSidecars is null)
            return;
        try
        {
            await _musicSidecars.WriteActiveSidecarAsync(
                run.ProjectDir, run.Scene, run.TakeId, run.Entry.Id, run.EffectiveIsVocal,
                run.Prompt, run.Lyrics, segmentFileNames, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed writing music take sidecar for scene {Scene}", run.Scene);
        }
    }

    /// <summary>
    /// Short Grok video with VOICE LOCK + dialogue, extract MP3 for Characters Play sample.
    /// </summary>
    public Task<JobSnapshot> StartVoicePreviewAsync(StartVoicePreviewRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CharKey))
            throw new InvalidOperationException("charKey required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunVoicePreviewAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "voice-preview",
                ProjectId = projectId,
                CharKey = req.CharKey,
                Message = req.Force
                    ? $"Queued voice regenerate for {req.CharKey}…"
                    : $"Queued voice sample for {req.CharKey}…",
            },
            lockResources: new[] { LockKeys.Character(projectId, req.CharKey) },
            lockReason: $"voice preview {req.CharKey}");
    }

    /// <summary>AI per-clip review (frames + prev tail) → draft suggestions for Apply → Regen.</summary>
    public Task<JobSnapshot> StartClipAutoReviewAsync(StartClipAutoReviewRequest req)
    {
        if (req.Scene <= 0 || req.Clip <= 0)
            throw new InvalidOperationException("scene and clip required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunClipAutoReviewAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "clip-auto-review",
                ProjectId = projectId,
                Scene = req.Scene,
                Clip = req.Clip,
                Message = $"Queued AI review S{req.Scene:D2}C{req.Clip:D2}…",
            },
            lockResources: new[] { LockKeys.Scene(projectId, req.Scene) },
            lockReason: $"auto-review S{req.Scene:D2}C{req.Clip:D2}");
    }

    /// <summary>
    /// Batch AI review (server walk). Prefer client-orchestrated batch: browser samples frames
    /// per clip then calls single auto-review. Server batch cannot sample video (browser frames required).
    /// </summary>
    public Task<JobSnapshot> StartClipAutoReviewBatchAsync(StartClipAutoReviewBatchRequest req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(ProjectIdRequired);

        // Server no longer extracts frames; batch must be driven from the browser Review page.
        throw new InvalidOperationException(
            "Batch auto-review must run from the browser (samples frames with ffmpeg.wasm). " +
            "Use Review → Auto-review all.");
    }

    private async Task RunClipAutoReviewAsync(StartClipAutoReviewRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        await InitAndPublishJobAsync(new JobSnapshot
        {
            Kind = "clip-auto-review",
            ProjectId = projectId,
            Scene = req.Scene,
            Clip = req.Clip,
            Message = $"Reviewing S{req.Scene:D2}C{req.Clip:D2}…",
        });

        try
        {
            var frameCount = req.Frames?.Count ?? 0;
            await AppendLogAsync(
                frameCount > 0
                    ? $"AI review = {frameCount} browser frame(s) → vision (key stays on server) → draft"
                    : "AI review requires browser-sampled frames (no server ffmpeg)");
            var draft = await _clipAutoReview.ReviewAsync(
                projectId,
                req.Scene,
                req.Clip,
                onProgress: (index, total, line) =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s =>
                    {
                        s.Index = Math.Clamp(index, 0, Math.Max(1, total));
                        s.Total = Math.Max(1, total);
                        s.Message = line;
                    });
                },
                ct: ct,
                clientFrames: req.Frames);

            await AppendLogAsync(
                $"Draft: {draft.Suggestion}/{draft.Category} · {draft.Suggestions.Count} suggestion(s)");
            await FinishAsync(
                StatusDone,
                $"Review ready S{req.Scene:D2}C{req.Clip:D2} — {draft.Suggestion} ({draft.Suggestions.Count} suggestions)");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, "Clip review cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clip auto-review failed S{Scene}C{Clip}", req.Scene, req.Clip);
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private async Task RunClipAutoReviewBatchAsync(StartClipAutoReviewBatchRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        var coords = ListAutoReviewBatchCoords(req, projectId);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "clip-auto-review-batch",
            ProjectId = projectId,
            Scene = AutoReviewBatchScene(req.Scene),
            Message = AutoReviewBatchMessage(coords.Count),
            Index = 0,
            Total = Math.Max(1, coords.Count),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            if (coords.Count == 0)
            {
                await FinishEmptyAutoReviewBatchAsync(req, projectId, ct).ConfigureAwait(false);
                return;
            }

            await AppendLogAsync(FormatAutoReviewBatchLog(req, coords.Count));
            var (ok, failed) = await ReviewBatchClipsAsync(coords, projectId, ct).ConfigureAwait(false);
            await TryRebuildReviewIndexAsync(projectId, req.Scene, ct).ConfigureAwait(false);
            await FinishAsync(
                StatusDone,
                $"Batch auto-review done: {ok} ok, {failed} failed of {coords.Count}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, "Batch auto-review cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch auto-review failed for {ProjectId}", projectId);
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private List<(int Scene, int Clip)> ListAutoReviewBatchCoords(StartClipAutoReviewBatchRequest req, string projectId) =>
        _reviewIndex.ListOnDiskClipCoords(projectId, req.Scene)
            .Where(c => !req.OnlyMissing || !_reviewIndex.HasDraft(projectId, c.Scene, c.Clip))
            .ToList();

    private static int? AutoReviewBatchScene(int? scene) =>
        scene is int s0 && s0 > 0 ? s0 : null;

    private static string AutoReviewBatchMessage(int coordCount) =>
        coordCount == 0
            ? "No clips to auto-review"
            : $"Batch reviewing {coordCount} clip(s)…";

    private static string FormatAutoReviewBatchLog(StartClipAutoReviewBatchRequest req, int count) =>
        $"Batch auto-review: {count} clip(s)" +
        (req.OnlyMissing ? " (only missing drafts)" : " (all)") +
        (req.Scene is int sn && sn > 0 ? $" scene S{sn:D2}" : "");

    private async Task FinishEmptyAutoReviewBatchAsync(
        StartClipAutoReviewBatchRequest req, string projectId, CancellationToken ct)
    {
        try { await _reviewIndex.RebuildAsync(projectId, req.Scene, ct); } catch { /* non-fatal */ }
        await FinishAsync(StatusDone, "Batch auto-review: nothing to do (no missing drafts)");
    }

    private async Task<(int Ok, int Failed)> ReviewBatchClipsAsync(
        List<(int Scene, int Clip)> coords, string projectId, CancellationToken ct)
    {
        var ok = 0;
        var failed = 0;
        for (var i = 0; i < coords.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (scene, clip) = coords[i];
            await UpdateAsync(s =>
            {
                s.Index = i;
                s.Total = coords.Count;
                s.Scene = scene;
                s.Clip = clip;
                s.Message = $"Reviewing S{scene:D2}C{clip:D2} ({i + 1}/{coords.Count})…";
            });
            await AppendLogAsync($"--- S{scene:D2}C{clip:D2} ({i + 1}/{coords.Count}) ---");

            if (await TryReviewOneBatchClipAsync(projectId, scene, clip, ct).ConfigureAwait(false))
                ok++;
            else
                failed++;
        }
        return (ok, failed);
    }

    private async Task<bool> TryReviewOneBatchClipAsync(string projectId, int scene, int clip, CancellationToken ct)
    {
        try
        {
            var draft = await _clipAutoReview.ReviewAsync(
                projectId,
                scene,
                clip,
                onProgress: (index, total, line) =>
                {
                    _ = AppendLogAsync($"  {line}");
                },
                ct: ct);
            await AppendLogAsync(
                $"  → {draft.Suggestion}/{draft.Category} · {draft.Suggestions.Count} suggestion(s)");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Batch auto-review failed S{Scene}C{Clip}", scene, clip);
            await AppendLogAsync($"  → ERROR: {ex.Message}");
            return false;
        }
    }

    private async Task TryRebuildReviewIndexAsync(string projectId, int? scene, CancellationToken ct)
    {
        try
        {
            var index = await _reviewIndex.RebuildAsync(projectId, scene, ct: ct);
            await AppendLogAsync(
                $"Review index rebuilt: {index.Clips.Count} row(s) → assets/review/index.json");
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"Review index rebuild skipped: {ex.Message}");
        }
    }

    private async Task RunVoicePreviewAsync(StartVoicePreviewRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        await InitAndPublishJobAsync(new JobSnapshot
        {
            Kind = "voice-preview",
            ProjectId = projectId,
            CharKey = req.CharKey,
            Message = req.Force
                ? $"Regenerating voice for {req.CharKey}…"
                : $"Generating voice sample for {req.CharKey}…",
        });

        try
        {
            await AppendLogAsync(
                "Voice sample = short film video (voice style + dialogue), kept as MP4");

            var path = await _voicePreview.GenerateAsync(
                projectId,
                req.CharKey,
                req.VoiceProfile,
                req.VoiceLabel,
                req.DisplayName,
                req.SampleText,
                force: req.Force,
                onProgress: (index, total, line) =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s =>
                    {
                        s.Index = Math.Clamp(index, 0, Math.Max(1, total));
                        s.Total = Math.Max(1, total);
                        s.Message = line;
                    });
                },
                ct: ct);

            await AppendLogAsync($"Saved {Path.GetFileName(path)}");
            await FinishAsync(StatusDone, $"Voice sample ready for {req.CharKey}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, "Voice sample cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voice preview failed for {Char}", req.CharKey);
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    /// <summary>
    /// Classify book images → which characters appear, write plates to scenes.json.
    /// Uses the project's vision model when that catalog row is usable; otherwise heuristic.
    /// Cancellable.
    /// </summary>
    public Task<JobSnapshot> StartSortCharacterPlatesAsync(AttachCharacterPlatesRequest req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunSortCharacterPlatesAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "character-plates",
                ProjectId = projectId,
                Message = "Queued character plate sort…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "character plates");
    }

    /// <summary>
    /// AI cast extract (Fountain + book → cast_seeds + locations). Background job so reverse proxies
    /// do not 502 a multi-minute chat pass.
    /// </summary>
    public Task<JobSnapshot> StartExtractCastAsync(string projectId, bool force = true, string? model = null)
    {
        if (_castExtract is null)
            throw new InvalidOperationException("Cast extract service is not configured.");
        if (string.IsNullOrWhiteSpace(projectId))
            projectId = _projects.ActiveProjectId;
        return StartBackgroundJobAsync(
            ct => RunExtractCastAsync(projectId, force, model, ct),
            new JobEnqueueMeta
            {
                Kind = "cast-extract",
                ProjectId = projectId,
                Message = "Queued cast extract from screenplay…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "cast extract");
    }

    private async Task RunExtractCastAsync(string projectId, bool force, string? model, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        if (_castExtract is null)
            throw new InvalidOperationException("Cast extract service is not configured.");

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "cast-extract",
            ProjectId = projectId,
            Message = "Building cast from screenplay (AI)…",
            Index = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync().ConfigureAwait(false);

        try
        {
            await AppendLogAsync("Cast extract: closed cast + looks + locations from Fountain (+ book)").ConfigureAwait(false);
            var result = await _castExtract.ExtractAsync(
                projectId,
                model: model,
                force: force,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s =>
                    {
                        s.Message = line;
                        if (line.Contains("Calling", StringComparison.OrdinalIgnoreCase))
                            s.Index = 1;
                        else if (line.Contains("Scrubbing", StringComparison.OrdinalIgnoreCase)
                                 || line.Contains("location", StringComparison.OrdinalIgnoreCase))
                            s.Index = 2;
                        else if (line.Contains("Writing", StringComparison.OrdinalIgnoreCase)
                                 || line.Contains("Cast ready", StringComparison.OrdinalIgnoreCase))
                            s.Index = 3;
                    });
                },
                ct: ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                await FinishAsync(StatusError, result.Error ?? "Cast extract failed", result.Error).ConfigureAwait(false);
                return;
            }

            var n = result.CharacterCount;
            var msg = $"Cast ready · {n} character(s)"
                      + (result.CharacterKeys is { Count: > 0 }
                          ? " — " + string.Join(", ", result.CharacterKeys.Take(12))
                          : "");
            await FinishAsync(StatusDone, msg).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(StatusCancelled, "Cast extract cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cast extract failed for {Project}", projectId);
            await FinishAsync(StatusError, ex.Message, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task RunSortCharacterPlatesAsync(AttachCharacterPlatesRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "character-plates",
            ProjectId = projectId,
            Message = "Sorting book images onto characters…",
            Index = 0,
            Total = Math.Max(1, req.MaxImages),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync(
                "Character plate sort (vision when the selected model is usable; otherwise heuristic)");

            var result = await _plates.AttachAsync(
                projectId,
                force: true, // job is always an explicit re-sort from UI
                copyIntoAssets: req.CopyIntoAssets,
                onlyCharKey: req.CharKey,
                visionModel: string.IsNullOrWhiteSpace(req.VisionModel) ? null : req.VisionModel,
                maxImages: req.MaxImages > 0 ? req.MaxImages : 32,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    // "Vision 3/20: …"
                    var m = CommonRegex.Match(
                        line, @"Vision\s+(\d+)/(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success &&
                        int.TryParse(m.Groups[1].Value, out var i) &&
                        int.TryParse(m.Groups[2].Value, out var t))
                    {
                        _ = UpdateAsync(s =>
                        {
                            s.Index = i;
                            s.Total = t;
                            s.Message = line;
                        });
                    }
                    else
                        _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            if (result.AlreadySorted)
            {
                await FinishAsync(StatusDone, $"Already sorted ({result.SortedAt})");
                return;
            }

            if (!result.Ok && !string.IsNullOrEmpty(result.Reason))
            {
                await FinishAsync(StatusError, result.Reason, result.Reason);
                return;
            }

            await UpdateAsync(s =>
            {
                s.Index = Math.Max(s.Index, result.ImagesClassified);
                if (result.ImagesClassified > 0)
                    s.Total = Math.Max(s.Total, result.ImagesClassified);
            });
            await AppendLogAsync(
                $"method={result.Method} updated={result.CharactersUpdated} " +
                $"skipped={result.CharactersSkipped} classified={result.ImagesClassified} " +
                $"text_skipped={result.ImagesSkippedText}");
            await FinishAsync(
                StatusDone,
                $"Plates sorted ({result.Method}): {result.CharactersUpdated} character(s) updated");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character plate sort failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    /// <summary>Lock/unlock character reference images (locks run vision style gate).</summary>
    public async Task<string> RunCharacterDesignActionAsync(
        string projectId,
        string action,
        string charKey,
        int variantIndex = 1,
        string? imagePath = null,
        bool allowStyleOverride = false,
        CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("A generation job is already running.");

        ct.ThrowIfCancellationRequested();
        return action switch
        {
            "lock-variant" =>
                await _characters.LockVariantAsync(
                    projectId, charKey, Math.Clamp(variantIndex, 1, 3), allowStyleOverride, ct).ConfigureAwait(false),
            "lock-image" when !string.IsNullOrWhiteSpace(imagePath) =>
                await _characters.LockFromPathAsync(
                    projectId,
                    charKey,
                    await ResolveLockImagePathAsync(projectId, imagePath, ct).ConfigureAwait(false),
                    allowStyleOverride,
                    ct).ConfigureAwait(false),
            "lock-bookref" =>
                await _characters.LockBookRefAsync(
                    projectId, charKey, Math.Max(0, variantIndex), allowStyleOverride, ct).ConfigureAwait(false),
            "unlock" =>
                _characters.Unlock(projectId, charKey)
                    ? $"Unlocked {charKey} — previous lock kept as variant 1 (best so far)"
                    : $"No locked ref for {charKey}",
            _ => throw new InvalidOperationException($"Unknown character action: {action}"),
        };
    }

    private async Task<string> ResolveLockImagePathAsync(string projectId, string imagePath, CancellationToken ct = default)
    {
        if (File.Exists(imagePath))
            return Path.GetFullPath(imagePath);
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var cand = Path.Combine(projectDir, imagePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(cand))
            return Path.GetFullPath(cand);
        throw new InvalidOperationException($"Image not found: {imagePath}");
    }

    /// <summary>
    /// Runs the actual xAI edit call for <see cref="StartVideoEditAsync"/>. Re-validates
    /// eligibility server-side (never trusts the client-only UI gate), tries the active clip's
    /// stored file_id first when unexpired, downloads the edited result, archives the current
    /// active clip into <c>assets/video/history/</c>, and writes the edited bytes as the new
    /// active clip + sidecar — so it shows up as a new take in the existing Takes UI for free.
    /// </summary>
    private async Task RunVideoEditAsync(StartVideoEditRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "video_edit",
            ProjectId = projectId,
            Scene = req.Scene,
            Clip = req.Clip,
            Message = $"Editing S{req.Scene:D2}C{req.Clip:D2}…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            if (_videoEdit is null)
                throw new InvalidOperationException("Video edit is not configured on this server.");
            if (!_videoEdit.IsConfigured)
                throw new InvalidOperationException("Video edit: connect xAI (XAI_API_KEY) in Configuration.");

            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var videoDir = Path.Combine(projectDir, AssetsFolder, VideoFolder);
            var activeMp4Path = Path.Combine(videoDir, $"scene_{req.Scene:D2}_clip_{req.Clip:D2}.mp4");
            if (!File.Exists(activeMp4Path))
                throw new InvalidOperationException($"Scene {req.Scene} clip {req.Clip}: no clip on disk to edit.");

            // ResolveOrDefault doesn't consult the capability's own defaultModelId automatically —
            // an explicit fallback is required, or a null/omitted req.Model (the common case: no
            // per-request override) throws instead of resolving the catalog default.
            var entry = SupportedModelCatalog.ResolveOrDefault(
                req.Model, ModelCapability.VideoEdit,
                fallbackId: SupportedModelCatalog.DefaultModelIdForCapability("video-edit"));

            // Eligibility + file_id/take lookup, both from the clip's own version list — never
            // trust the client-only UI gate. Duration cap is catalog-driven, never hardcoded.
            var versions = await _projects.GetClipVersionsAsync(projectId, req.Scene, req.Clip).ConfigureAwait(false);
            var current = versions.FirstOrDefault(v => v.IsCurrent);
            if (current is not null && entry.MaxEditInputDurationSeconds is { } cap &&
                current.DurationSeconds > cap + 0.01)
            {
                throw new InvalidOperationException(
                    $"Scene {req.Scene} clip {req.Clip} is {current.DurationSeconds:0.#}s — " +
                    $"Grok can only edit clips up to {cap:0.#}s.");
            }

            string? sourceFileId = null;
            if (current?.SourceFileId is { Length: > 0 } fid &&
                (current.SourceFileExpiresAtUnixSeconds is not { } exp ||
                 exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            {
                sourceFileId = fid;
            }
            await AppendLogAsync(sourceFileId is null
                ? "No stored file reference on this clip — uploading it"
                : "Trying the clip's stored file reference first");

            var url = await _videoEdit.EditClipAsync(
                activeMp4Path, req.Prompt, sourceFileId, entry.Id,
                onProgress: msg => _ = AppendLogAsync($"  [Edit] {msg}"),
                ct).ConfigureAwait(false);

            await UpdateAsync(s => s.Message = "Downloading edited clip…");
            // Via IVideoEditClient.DownloadToFileAsync, not a raw HttpClient GET — a fake
            // implementation's "URL" isn't necessarily real http(s) (see FakeGrokVideoClient's own
            // DownloadToFileAsync precedent for the same reason).
            var tempPath = Path.Combine(Path.GetTempPath(), $"video-edit-{Guid.NewGuid():N}.mp4");
            byte[] bytes;
            try
            {
                await _videoEdit.DownloadToFileAsync(url, tempPath, ct).ConfigureAwait(false);
                bytes = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }

            var newFileName = _projects.ArchiveActiveAndReplaceClipBytesAsync(projectId, req.Scene, req.Clip, bytes);

            if (_sidecars is not null)
            {
                var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                await _sidecars.WriteSidecarWithTakeAsync(
                    projectDir, req.Scene, req.Clip,
                    take: (current?.Take ?? 0) + 1,
                    prompt: req.Prompt,
                    scriptText: current?.ScriptText ?? "",
                    model: entry.Id,
                    resolution: current?.Resolution ?? "",
                    durationSeconds: current?.DurationSeconds ?? 0,
                    sha256: sha256,
                    sizeBytes: bytes.LongLength,
                    mp4FileName: newFileName,
                    editedFromTake: current?.Take,
                    ct: ct).ConfigureAwait(false);
            }

            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                ProjectId = projectId,
                Kind = "video_edit",
                Model = entry.Id,
                Scene = req.Scene,
                Clip = req.Clip,
                Ok = true,
            }, ct).ConfigureAwait(false);

            await FinishAsync(StatusDone, "Edited clip ready — saved as a new take.");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Video edit failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private async Task RunLocationVariantsAsync(StartLocationVariantsRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "location_variants",
            ProjectId = projectId,
            CharKey = req.LocKey,
            Message = $"Generating set plates for {req.LocKey}…",
            Index = 0,
            Total = req.Count > 0 ? req.Count : 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync($"Location design (Grok image) for {req.LocKey}");
            await UpdateAsync(s => s.Message = "Building set prompt…");

            var result = await _locations.GenerateVariantsAsync(
                projectId,
                req.LocKey,
                n: req.Count,
                descriptionOverride: req.DescriptionOverride,
                visualLockOverride: req.VisualLockOverride,
                imageEditInstruction: req.ImageEditInstruction,
                persistDescription: req.PersistDescription,
                onProgress: ApplyLocationVariantProgress,
                ct: ct);

            await UpdateAsync(s =>
            {
                s.Index = result.Paths.Count;
                s.Total = Math.Max(s.Total, result.Paths.Count);
            });
            await AppendLogAsync($"mode={result.Mode} · {result.Paths.Count} file(s)");

            await FinishLocationVariantsAsync(req, projectId, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Location variants failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private void ApplyLocationVariantProgress(string line)
    {
        _ = AppendLogAsync(line);
        var m = CommonRegex.Match(line, @"saved variant (\d+)/(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var idx))
            _ = UpdateAsync(s => { s.Index = idx; s.Message = line; });
        else if (line.Contains("generating", StringComparison.OrdinalIgnoreCase))
            ApplyLocationGeneratingProgress(line);
        else
            _ = UpdateAsync(s => s.Message = line);
    }

    private void ApplyLocationGeneratingProgress(string line)
    {
        var g = CommonRegex.Match(line, @"generating\s+(\d+)");
        if (g.Success && int.TryParse(g.Groups[1].Value, out var total) && total > 0)
            _ = UpdateAsync(s => { s.Total = total; s.Message = line; });
        else
            _ = UpdateAsync(s => s.Message = line);
    }

    private async Task FinishLocationVariantsAsync(
        StartLocationVariantsRequest req,
        string projectId,
        LocationDesignResult result,
        CancellationToken ct)
    {
        if (result.PreviousVariantIndex is int prevLoc && result.NewVariantIndex is int nextLoc)
        {
            await FinishAsync(
                StatusDone,
                $"New look is #{nextLoc} — current lock is still #{prevLoc}. Click a lock to keep old or switch.");
            return;
        }

        if (result.LockedAsPreferred)
        {
            await FinishAsync(
                StatusDone,
                $"Plate tweaked for {req.LocKey} — new look is locked. Tweak again with words if needed.");
            return;
        }

        if (req.AutoLockBest && string.IsNullOrWhiteSpace(req.ImageEditInstruction) && result.Paths.Count > 0)
        {
            await AutoLockLocationBestAsync(req, projectId, result, ct).ConfigureAwait(false);
            return;
        }

        await FinishAsync(
            StatusDone,
            $"Set plates ready for {req.LocKey} ({result.Mode}, {result.Paths.Count} image(s))");
    }

    private async Task AutoLockLocationBestAsync(
        StartLocationVariantsRequest req,
        string projectId,
        LocationDesignResult result,
        CancellationToken ct)
    {
        await UpdateAsync(s => s.Message = $"AI picking best set for {req.LocKey}…");
        var (best, _) = await _locations.AutoLockBestVariantAsync(
            projectId, req.LocKey, maxVariants: Math.Max(req.Count, result.Paths.Count),
            onProgress: line =>
            {
                _ = AppendLogAsync(line);
                _ = UpdateAsync(s => s.Message = line);
            },
            ct: ct);
        await FinishAsync(
            StatusDone,
            $"Set plates ready for {req.LocKey} — auto-locked variant {best}");
    }

    private async Task RunCharacterVariantsAsync(StartCharacterVariantsRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "character",
            ProjectId = projectId,
            CharKey = req.CharKey,
            Message = CharacterVariantJobMessage(req),
            Index = 0,
            Total = CharacterVariantJobTotal(req),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync($"Character design (C# / Grok image API) for {req.CharKey}");
            await UpdateAsync(s => s.Message = "Resolving refs + design prompt…");

            var result = await _characters.GenerateVariantsAsync(
                projectId,
                req.CharKey,
                n: req.Count,
                seedOptions: req,
                onProgress: ApplyCharacterVariantProgress,
                ct: ct);

            await UpdateAsync(s =>
            {
                s.Index = result.Paths.Count;
                s.Total = Math.Max(s.Total, result.Paths.Count);
            });
            await AppendLogAsync(FormatCharacterVariantFilesLog(result));

            await FinishCharacterVariantsAsync(req, projectId, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character variants failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private static string CharacterVariantJobMessage(StartCharacterVariantsRequest req) =>
        req.IterativeEdit
            ? $"Tweaking portrait for {req.CharKey}…"
            : $"Generating portraits for {req.CharKey}…";

    private static int CharacterVariantJobTotal(StartCharacterVariantsRequest req)
    {
        if (req.Count > 0)
            return req.Count;
        return req.IterativeEdit ? 1 : 3;
    }

    private static string FormatCharacterVariantFilesLog(CharacterDesignResult result) =>
        $"mode={result.Mode} · {result.Paths.Count} file(s)" +
        (result.BookRefs.Count > 0
            ? $" · book refs: {string.Join(", ", result.BookRefs)}"
            : "");

    private void ApplyCharacterVariantProgress(string line)
    {
        _ = AppendLogAsync(line);
        var idx = TryParseVariantProgress(line);
        if (idx > 0)
            _ = UpdateAsync(s => { s.Index = idx; s.Message = line; });
        else if (line.Contains("generating", StringComparison.OrdinalIgnoreCase))
            ApplyCharacterGeneratingProgress(line);
        else if (IsCharacterVariantStatusLine(line))
            _ = UpdateAsync(s =>
            {
                s.Index = Math.Max(s.Index, 1);
                s.Message = line;
            });
    }

    private void ApplyCharacterGeneratingProgress(string line)
    {
        // "generating 1 variant(s)" / "generating 3 variants"
        var m = CommonRegex.Match(line, @"generating\s+(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var total) && total > 0)
            _ = UpdateAsync(s => { s.Total = total; s.Message = line; });
        else
            _ = UpdateAsync(s => s.Message = line);
    }

    private static bool IsCharacterVariantStatusLine(string line) =>
        line.Contains("edit variant", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Grok", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("book ref", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("ref image", StringComparison.OrdinalIgnoreCase);

    private async Task FinishCharacterVariantsAsync(
        StartCharacterVariantsRequest req,
        string projectId,
        CharacterDesignResult result,
        CancellationToken ct)
    {
        if (result.PreviousVariantIndex is int prevChar && result.NewVariantIndex is int nextChar)
        {
            await FinishAsync(
                StatusDone,
                $"New look is #{nextChar} — current lock is still #{prevChar}. Pick one.");
            return;
        }

        if (result.LockedAsPreferred)
        {
            await FinishAsync(
                StatusDone,
                $"Portrait tweaked for {req.CharKey} — new look is locked. Tweak again with words if needed.");
            return;
        }

        if (req.AutoLockBest && !req.IterativeEdit && result.Paths.Count > 0)
        {
            await AutoLockCharacterBestAsync(req, projectId, result, ct).ConfigureAwait(false);
            return;
        }

        await FinishAsync(
            StatusDone,
            $"Variants ready for {req.CharKey} ({result.Mode}, {result.Paths.Count} image(s))");
    }

    private async Task AutoLockCharacterBestAsync(
        StartCharacterVariantsRequest req,
        string projectId,
        CharacterDesignResult result,
        CancellationToken ct)
    {
        await UpdateAsync(s => s.Message = $"AI picking best look for {req.CharKey}…");
        var (best, _) = await _characters.AutoLockBestVariantAsync(
            projectId, req.CharKey, maxVariants: Math.Max(3, result.Paths.Count),
            onProgress: line =>
            {
                _ = AppendLogAsync(line);
                _ = UpdateAsync(s => s.Message = line);
            },
            ct: ct);
        await FinishAsync(
            StatusDone,
            $"Portraits ready for {req.CharKey} — auto-locked variant {best}");
    }

    private async Task RunEmbellishAsync(string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "embellish",
            ProjectId = projectId,
            Message = "Enriching screenplay…",
            Index = 0,
            Total = 0,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync($"Enrich screenplay for {projectId} (visual detail from the book; dialogue unchanged)");
            await UpdateAsync(s => { s.Index = 0; s.Message = "Loading draft + book text…"; });

            var medium = await TryReadEnrichMediumAsync(projectId, ct).ConfigureAwait(false);
            var result = await RunEmbellishModelCallAsync(projectId, medium, ct).ConfigureAwait(false);
            await FinishEmbellishResultAsync(projectId, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Screenplay enrich failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private async Task<string?> TryReadEnrichMediumAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var dir = await _projects.GetProjectDirAsync(projectId, ct);
            return ProjectVisionMeta.TryRead(dir)?.VisualMedium
                   ?? ProjectVisionMeta.GetAdaptationMediumPreference(dir);
        }
        catch { return null; /* enrich without medium */ }
    }

    private async Task<ScreenplayService.DraftEditResult> RunEmbellishModelCallAsync(
        string projectId, string? medium, CancellationToken ct)
    {
        await UpdateAsync(s =>
        {
            s.Index = 2;
            s.Message = "Generating the full screenplay… one long rewrite (10–20 min is normal).";
        });
        await AppendLogAsync("Model call started — no per-scene ticks; the clock keeps running.");

        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = DateTimeOffset.UtcNow;
        var heartbeat = HeartbeatEnrichAsync(started, hbCts.Token);
        try
        {
            return await ScreenplayService.EmbellishDraftAsync(
                _projects,
                projectId,
                medium,
                _chat,
                model: "",
                onProgress: line => ApplyEmbellishProgress(line),
                ct: ct,
                responses: _xaiResponses,
                bookRegistry: _bookRegistry,
                bookFileSessions: _bookFileSessionFactory,
                useFakes: _opts.UseFakes);
        }
        finally
        {
            await hbCts.CancelAsync();
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Heartbeat task is cancelled together with the enrich job.
            }
        }
    }

    private void ApplyEmbellishProgress(string line)
    {
        _ = AppendLogAsync(line);
        _ = UpdateAsync(s =>
        {
            s.Message = line;
            if (TryParseSceneProgress(line, out var idx, out var tot))
            {
                s.Index = idx;
                s.Total = tot;
            }
        });
    }

    private async Task FinishEmbellishResultAsync(string projectId, ScreenplayService.DraftEditResult result)
    {
        if (!result.Ok)
        {
            await FinishAsync(StatusError, result.Error ?? "Enrich failed.", result.Error);
            return;
        }

        await UpdateAsync(s =>
        {
            if (s.Total > 0) s.Index = s.Total;
            s.Message = "Saving enriched draft…";
        });
        if (result.Applied)
            _projects.TriggerAutoGitCommit(projectId, "ptm:stage=embellish");
        await FinishAsync(
            StatusDone,
            result.Message
            ?? $"Enriched {projectId} ({result.SceneCountAfter} scenes). Re-approve, then Fit length if you use a target runtime.");
    }

    private async Task HeartbeatEnrichAsync(DateTimeOffset started, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                var elapsed = DateTimeOffset.UtcNow - started;
                var msg = $"Still generating… {ElapsedClock.Format(elapsed)} elapsed. Scene-by-scene enrich — this is normal.";
                await UpdateAsync(s =>
                {
                    s.Message = msg;
                    if (s.Log.Count > 0 && s.Log[^1].StartsWith("Still generating…", StringComparison.Ordinal))
                        s.Log[^1] = msg;
                    else
                        s.Log.Add(msg);
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* expected when the model call returns */
        }
    }

    private static bool TryParseSceneProgress(string? line, out int index, out int total)
    {
        index = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var m = CommonRegex.Match(line, @"scene\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out index)) return false;
        if (!int.TryParse(m.Groups[2].Value, out total) || total <= 0) return false;
        return true;
    }

    private async Task RunPlanLooksAsync(StartPlanLooksRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        var count = req.Count > 0 ? req.Count : 3;
        var (cast, locs) = CollectPlanLookTargets(req, projectId);
        var total = cast.Count + locs.Count;
        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "plan_looks",
            ProjectId = projectId,
            Message = total == 0
                ? "Nothing to generate — all plan looks already locked"
                : $"Looks for plan: {cast.Count} cast + {locs.Count} places…",
            Index = 0,
            Total = Math.Max(1, total),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        if (total == 0)
        {
            await FinishAsync(StatusDone, "All used cast/places already have locked looks (or none in plan).");
            return;
        }

        try
        {
            await GeneratePlanLooksAsync(projectId, count, cast, locs, total, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Plan looks batch failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private (List<CharacterSummary> Cast, List<LocationSummary> Locs) CollectPlanLookTargets(
        StartPlanLooksRequest req, string projectId)
    {
        var cast = req.IncludeCast
            ? _projects.ListCharacters(projectId)
                .Where(c => c.UsedInPlan && !c.IsGroup && !c.VoiceOnly)
                .Where(c => !req.SkipAlreadyLocked || !c.Locked)
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<CharacterSummary>();
        var locs = req.IncludeLocations
            ? _projects.ListLocations(projectId)
                .Where(l => l.UsedInPlan)
                .Where(l => !req.SkipAlreadyLocked || !l.Locked)
                .OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<LocationSummary>();
        return (cast, locs);
    }

    private async Task GeneratePlanLooksAsync(
        string projectId,
        int count,
        List<CharacterSummary> cast,
        List<LocationSummary> locs,
        int total,
        CancellationToken ct)
    {
        var done = 0;
        var lockedN = 0;
        var failed = new List<string>();
        await AppendLogAsync(
            $"Plan looks: {cast.Count} cast · {locs.Count} locations · {count} variants each · auto-lock best");

        foreach (var c in cast)
        {
            ct.ThrowIfCancellationRequested();
            lockedN += await GenerateOneCastPlanLookAsync(projectId, c, count, done, total, failed, ct)
                .ConfigureAwait(false);
            done++;
            await UpdateAsync(s => s.Index = done);
        }

        foreach (var loc in locs)
        {
            ct.ThrowIfCancellationRequested();
            lockedN += await GenerateOneLocationPlanLookAsync(projectId, loc, count, done, total, failed, ct)
                .ConfigureAwait(false);
            done++;
            await UpdateAsync(s => s.Index = done);
        }

        await FinishPlanLooksAsync(lockedN, total, failed).ConfigureAwait(false);
    }

    private async Task<int> GenerateOneCastPlanLookAsync(
        string projectId, CharacterSummary c, int count, int done, int total, List<string> failed, CancellationToken ct)
    {
        await UpdateAsync(s =>
        {
            s.Message = $"Cast look {done + 1}/{total}: {c.DisplayName}";
            s.Index = done;
            s.CharKey = c.Key;
        });
        await AppendLogAsync($"── Cast {c.Key} ({c.DisplayName}) ──");
        try
        {
            var result = await _characters.GenerateVariantsAsync(
                projectId,
                c.Key,
                n: count,
                seedOptions: new StartCharacterVariantsRequest
                {
                    ProjectId = projectId,
                    CharKey = c.Key,
                    Count = count,
                    SeedMode = "auto",
                    AutoLockBest = false,
                },
                onProgress: line => _ = AppendLogAsync("  " + line),
                ct: ct);
            if (result.Paths.Count == 0)
                throw new InvalidOperationException("no variants produced");
            var (best, _) = await _characters.AutoLockBestVariantAsync(
                projectId, c.Key, maxVariants: count,
                onProgress: line => _ = AppendLogAsync("  " + line),
                ct: ct);
            await AppendLogAsync($"  locked variant {best}");
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failed.Add($"{c.Key}: {ex.Message}");
            await AppendLogAsync($"  FAILED: {ex.Message}");
            _log.LogWarning(ex, "Plan looks cast failed {Key}", c.Key);
            return 0;
        }
    }

    private async Task<int> GenerateOneLocationPlanLookAsync(
        string projectId, LocationSummary loc, int count, int done, int total, List<string> failed, CancellationToken ct)
    {
        await UpdateAsync(s =>
        {
            s.Message = $"Place look {done + 1}/{total}: {loc.DisplayName}";
            s.Index = done;
            s.CharKey = loc.Key;
        });
        await AppendLogAsync($"── Location {loc.Key} ({loc.DisplayName}) ──");
        try
        {
            var result = await _locations.GenerateVariantsAsync(
                projectId,
                loc.Key,
                n: count,
                onProgress: line => _ = AppendLogAsync("  " + line),
                ct: ct);
            if (result.Paths.Count == 0)
                throw new InvalidOperationException("no variants produced");
            var (best, _) = await _locations.AutoLockBestVariantAsync(
                projectId, loc.Key, maxVariants: count,
                onProgress: line => _ = AppendLogAsync("  " + line),
                ct: ct);
            await AppendLogAsync($"  locked variant {best}");
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failed.Add($"{loc.Key}: {ex.Message}");
            await AppendLogAsync($"  FAILED: {ex.Message}");
            _log.LogWarning(ex, "Plan looks location failed {Key}", loc.Key);
            return 0;
        }
    }

    private async Task FinishPlanLooksAsync(int lockedN, int total, List<string> failed)
    {
        var summary = $"Plan looks done: locked {lockedN}/{total}"
            + (failed.Count > 0 ? $" · {failed.Count} failed" : "");
        if (failed.Count > 0 && lockedN == 0)
            await FinishAsync(StatusError, summary, string.Join("; ", failed.Take(5)));
        else if (failed.Count > 0)
            await FinishAsync(StatusPartial, summary);
        else
            await FinishAsync(StatusDone, summary);
    }

    private static int TryParseVariantProgress(string line)
    {
        var m = CommonRegex.Match(
            line, @"variant[_\s-]*0*([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return n;
        m = CommonRegex.Match(line, @"\b([1-3])\s*/\s*3\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        m = CommonRegex.Match(
            line, @"saved variant\s+([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        return 0;
    }

    private async Task RunStage1Async(StartStage1Request req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        // Progress: 10 fixed phases (same scale as book_import) so the UI bar never sticks at
        // the "Total=0 → 35%" placeholder during a long single-pass adapt call.
        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "stage1",
            ProjectId = projectId,
            Message = "Writing the full screenplay. Long books often take 20–60 minutes.",
            Index = 0,
            Total = 10,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync("Screenplay: book → draft → approve");
            // Sequential progress pump — no GetAwaiter; preserves line order for SignalR
            var progress = System.Threading.Channels.Channel.CreateUnbounded<string>(
                new System.Threading.Channels.UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
            var progressPump = Task.Run(async () =>
            {
                await foreach (var line in progress.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await ReportStage1ProgressAsync(line).ConfigureAwait(false);
            }, CancellationToken.None);

            Stage1Result result;
            try
            {
                result = await _stage1.RunAsync(
                    projectId,
                    chunkPages: Math.Clamp(req.ChunkPages, 5, 30),
                    totalMinutes: req.TotalMinutes,
                    model: await ResolvePlanningModelAsync(projectId, req.Model, ct).ConfigureAwait(false),
                    resume: req.Resume,
                    maxChunks: req.MaxChunks,
                    onProgress: line => progress.Writer.TryWrite(line),
                    ct: ct);
            }
            finally
            {
                progress.Writer.TryComplete();
                try { await progressPump.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* job cancelled */ }
            }

            var msg =
                $"Screenplay ready: {result.SceneCount} scenes · {result.CharacterCount} cast · " +
                $"{result.LocationCount} locations · V.O. {result.VoCueCount}/{result.TotalDialogueCues} ({result.VoPercent}%)";
            if (result.TotalDialogueCues > 0 && result.VoPercent >= 45)
                msg += " — narration-heavy (clip gen will lean on V.O.)";
            if (result.HardErrors.Count > 0)
                msg += $" · {result.HardErrors.Count} issue(s)";
            await FinishAsync(result.Ok || result.SceneCount > 0 ? StatusDone : StatusError, msg,
                result.Ok ? null : string.Join("; ", result.HardErrors.Take(3)));
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 1 failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private async Task RunStage2Async(StartStage2Request req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = KindStage2,
            ProjectId = projectId,
            Message = "Building shot plan…",
            Index = 0,
            Total = 10,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync("Building shot plan from screenplay");
            ct.ThrowIfCancellationRequested();
            var resolution = await ResolveVideoResolutionAsync(projectId, req.Resolution, ct);
            var result = await _stage2.PlanAsync(
                projectId,
                resolution: resolution,
                scenes: string.IsNullOrWhiteSpace(req.Scenes) ? "all" : req.Scenes,
                onProgress: ReportStage2ProgressLine,
                ct: ct);

            await FinishAsync(
                StatusDone,
                $"Stage 2 complete: {result.SceneCount} scenes · {result.ClipCount} clips · ~{result.DurationSeconds}s");

            // North Star: after shot plan, auto-generate looks for used cast + places
            // (3 variants, AI lock best; skip already locked). Operator can override anytime.
            await TryEnqueuePlanLooksAfterStage2Async(projectId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 2 failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    /// <summary>
    /// After a successful Stage‑2, queue plan_looks when any used face/place still needs a plate.
    /// Deferred so Stage locks / CurrentRun clear first; no-op when nothing missing.
    /// </summary>
    private async Task TryEnqueuePlanLooksAfterStage2Async(string projectId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectId)) return;

            var castNeed = _projects.ListCharacters(projectId)
                .Count(c => c.UsedInPlan && !c.IsGroup && !c.VoiceOnly && !c.Locked);
            var locNeed = _projects.ListLocations(projectId)
                .Count(l => l.UsedInPlan && !l.Locked);
            if (castNeed + locNeed == 0)
            {
                await AppendLogAsync("Plan looks: all used cast/places already locked — skip auto looks");
                return;
            }

            await AppendLogAsync(
                $"Plan looks: will auto-queue after shot plan ({castNeed} cast · {locNeed} places need plates)");

            // Defer until RunStage2Async + StartBackgroundJobAsync finally releases CurrentRun/locks.
            var pid = projectId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    await StartPlanLooksAsync(new StartPlanLooksRequest
                    {
                        ProjectId = pid,
                        Count = 3,
                        SkipAlreadyLocked = true,
                        IncludeCast = true,
                        IncludeLocations = true,
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Auto plan_looks after Stage 2 failed for {ProjectId}", pid);
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto plan_looks after Stage 2 failed to schedule for {ProjectId}", projectId);
            try { await AppendLogAsync("Plan looks auto-queue failed: " + ex.Message); } catch { /* best effort */ }
        }
    }


    public Task<JobSnapshot> StartYouTubeUploadAsync(StartYouTubeUploadRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException(ProjectIdRequired);
        var projectId = req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunYouTubeUploadAsync(req, projectId, ct),
            new JobEnqueueMeta
            {
                Kind = "youtube_upload",
                ProjectId = projectId,
                Message = "Queued YouTube upload…",
            },
            lockResources: new[] { LockKeys.YouTube(projectId) },
            lockReason: "youtube upload");
    }

    private async Task RunYouTubeUploadAsync(StartYouTubeUploadRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        await InitAndPublishJobAsync(new JobSnapshot
        {
            Kind = "youtube_upload",
            ProjectId = projectId,
            Message = "Connecting to YouTube…",
        });

        try
        {
            var path = await ResolveWipMoviePathAsync(projectId, ct).ConfigureAwait(false);
            var uploadBytes = await ReadUploadBytesAsync(path, ct).ConfigureAwait(false);
            var publishGate = await FilmBuildService.ApplyUploadHashGateAsync(_projects, projectId, uploadBytes, ct: ct).ConfigureAwait(false);
            await AppendLogAsync(
                $"Film publish path: {publishGate.Path} (upload sha {publishGate.UploadSha256[..Math.Min(12, publishGate.UploadSha256.Length)]}…)");

            var youtube = await _youTube.GetServiceAsync(ct)
                ?? throw new InvalidOperationException("YouTube is not connected — connect it from Review first.");

            var title = string.IsNullOrWhiteSpace(req.Title) ? $"{projectId} — WIP" : req.Title.Trim();
            var privacy = req.PrivacyStatus is "private" or "unlisted" or "public"
                ? req.PrivacyStatus
                : "unlisted";

            var videoId = await UploadWipVideoAsync(youtube, path, title, privacy, req.Description ?? "", ct)
                .ConfigureAwait(false);
            var url = $"https://youtu.be/{videoId}";
            await _projects.SaveYouTubeUploadInfoAsync(projectId, new YouTubeUploadInfo
            {
                VideoId = videoId,
                Url = url,
                Title = title,
                PrivacyStatus = privacy,
                UploadedAt = DateTimeOffset.UtcNow,
            }, ct);

            await RecordPublishAndLearningAsync(projectId, uploadBytes, videoId, url, ct).ConfigureAwait(false);
            TryDeleteStagedMovie(path);
            await FinishAsync(StatusDone, $"Uploaded to YouTube: {url}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, "YouTube upload cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "YouTube upload failed for {ProjectId}", projectId);
            var errMessage = ex is Google.GoogleApiException gex
                ? $"Google API Error ({gex.HttpStatusCode}): {gex.Message} — {gex.Error?.Message}"
                : ex.Message;
            await AppendLogAsync($"❌ YouTube upload exception: {errMessage}");
            await FinishAsync(StatusError, errMessage, errMessage);
        }
    }

    private async Task<string> ResolveWipMoviePathAsync(string projectId, CancellationToken ct)
    {
        var path = _projects.ResolveWipMoviePath(projectId);
        if (path is null || !File.Exists(path))
        {
            var pDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var altWip = Path.Combine(pDir, AssetsFolder, VideoFolder, "wip_movie.mp4");
            if (File.Exists(altWip)) path = altWip;
        }
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException("No WIP movie file found on server — publish Demo from a browser stitch first.");
        return path;
    }

    private static async Task<byte[]> ReadUploadBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not read WIP movie for hash gate: " + ex.Message, ex);
        }
    }

    private async Task<string> UploadWipVideoAsync(
        Google.Apis.YouTube.v3.YouTubeService youtube,
        string path,
        string title,
        string privacy,
        string description,
        CancellationToken ct)
    {
        var video = new Video
        {
            Snippet = new VideoSnippet
            {
                Title = title,
                Description = description,
                CategoryId = "1", // Film & Animation
            },
            Status = new VideoStatus
            {
                PrivacyStatus = privacy,
                Embeddable = true,
            },
        };

        var bytes = new FileInfo(path).Length;
        await AppendLogAsync($"Uploading {Path.GetFileName(path)} ({bytes / (1024 * 1024)} MB, {privacy})…");

        await using var stream = File.OpenRead(path);
        var upload = youtube.Videos.Insert(video, "snippet,status", stream, "video/mp4");
        string? videoId = null;
        upload.ResponseReceived += v => videoId = v.Id;
        upload.ProgressChanged += p => ApplyYouTubeUploadProgress(p, bytes);

        var result = await upload.UploadAsync(ct);
        if (result.Status == UploadStatus.Completed && videoId is not null)
            return videoId;
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        var errDetail = FormatYouTubeUploadError(result);
        await AppendLogAsync($"❌ YouTube upload failed: {errDetail}");
        throw result.Exception ?? new InvalidOperationException($"YouTube upload failed: {errDetail}");
    }

    private void ApplyYouTubeUploadProgress(IUploadProgress p, long bytes)
    {
        var pct = bytes > 0 ? (int)Math.Clamp(p.BytesSent * 100 / bytes, 0, 100) : 0;
        _ = UpdateAsync(s =>
        {
            s.Index = pct;
            s.Total = 100;
            s.Message = p.Status switch
            {
                UploadStatus.Uploading => $"Uploading… {pct}%",
                UploadStatus.Completed => "Upload complete — finalizing…",
                UploadStatus.Failed => $"Upload failed: {p.Exception?.Message}",
                _ => s.Message,
            };
        });
    }

    private static string FormatYouTubeUploadError(IUploadProgress result) =>
        result.Exception is Google.GoogleApiException gerr
            ? $"Google API {gerr.HttpStatusCode}: {gerr.Message} — {gerr.Error?.Message}"
            : result.Exception?.Message ?? $"YouTube upload status: {result.Status}";

    private async Task RecordPublishAndLearningAsync(
        string projectId, byte[] uploadBytes, string videoId, string url, CancellationToken ct)
    {
        try
        {
            var finalPublish = await FilmBuildService.ApplyUploadHashGateAsync(
                _projects, projectId, uploadBytes,
                youtubeVideoId: videoId, youtubeUrl: url, ct: ct).ConfigureAwait(false);
            await AppendLogAsync($"Film build publish recorded ({finalPublish.Path}).");
            if (string.Equals(finalPublish.Path, FilmBuildPublish.PathStudioIntact, StringComparison.Ordinal))
            {
                var lp = await LearningPackageService.CreateFromProjectAsync(
                    _projects, projectId, workspaceRoot: TryFindWorkspaceRoot(), ct: ct).ConfigureAwait(false);
                await AppendLogAsync($"Learning package {lp.PackageId} → {lp.ProjectRelativePath}");
            }
            else
            {
                await AppendLogAsync(
                    "Learning package skipped (upload not studio_intact — external edit suspected).");
            }
        }
        catch (Exception lpEx)
        {
            _log.LogWarning(lpEx, "Publish provenance / learning package failed for {Project}", projectId);
            await AppendLogAsync("Publish provenance note: " + lpEx.Message);
        }
    }

    private void TryDeleteStagedMovie(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to clean up temporary staged movie file {Path} after YouTube upload", path);
        }
    }

    private static string? TryFindWorkspaceRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var evals = Path.Combine(dir.FullName, "evals");
                var prompts = Path.Combine(dir.FullName, "prompts");
                if (Directory.Exists(prompts) || Directory.Exists(evals))
                    return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "PageToMovie.sln")) ||
                    File.Exists(Path.Combine(dir.FullName, "host", "PageToMovie.sln")))
                    return dir.FullName;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private async Task RunSpeakBatchAsync(
        StartSpeakBatchRequest req,
        string projectId,
        string charKey,
        CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "speak-batch",
            ProjectId = projectId,
            CharKey = charKey,
            Message = "Speak-batch: building work list…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync().ConfigureAwait(false);

        try
        {
            var (ctx, ctxErr) = await ResolveSpeakContextAsync(projectId, charKey, req.Model, ct).ConfigureAwait(false);
            if (ctx is null)
            {
                await FinishAsync(StatusError, ctxErr ?? VoiceNotConfigured, ctxErr ?? VoiceNotConfigured)
                    .ConfigureAwait(false);
                return;
            }
            var providerId = ctx.ProviderId;

            var work = await BuildSpeakBatchWorkAsync(req, projectId, charKey, ct).ConfigureAwait(false);
            if (work.Count == 0)
            {
                await AppendLogAsync("Speak-batch: nothing to synthesize (only_missing or no dialogue).")
                    .ConfigureAwait(false);
                await FinishAsync(StatusDone, "No lines to speak").ConfigureAwait(false);
                return;
            }

            var maxParallel = Math.Clamp(req.MaxParallel <= 0 ? 3 : req.MaxParallel, 1, 8);

            await UpdateAsync(s =>
            {
                s.Total = work.Count;
                s.Index = 0;
                s.Message = $"Speak-batch: {work.Count} line(s) · parallel {maxParallel} · {providerId}";
            }).ConfigureAwait(false);
            await AppendLogAsync(Snapshot.Message ?? "").ConfigureAwait(false);

            var state = new SpeakBatchRunState
            {
                Ctx = ctx,
                ProjectId = projectId,
                CharKey = charKey,
                WorkCount = work.Count,
                MaxLen = ctx.MaxLen,
                Gate = new SemaphoreSlim(maxParallel, maxParallel),
                HandoffGate = new SemaphoreSlim(1, 1),
            };
            try
            {
                var tasks = work.Select(item => ProcessSpeakBatchItemAsync(state, item, ct)).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Dispose();
                state.HandoffGate.Dispose();
            }

            await FinishSpeakBatchAsync(Volatile.Read(ref state.Failed), work.Count).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, "Speak-batch cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Speak-batch failed for {ProjectId}", projectId);
            await FinishAsync(StatusError, ex.Message, ex.Message).ConfigureAwait(false);
        }
    }

    private sealed class SpeakBatchRunState
    {
        public required SpeakContext Ctx { get; init; }
        public required string ProjectId { get; init; }
        public required string CharKey { get; init; }
        public required int WorkCount { get; init; }
        public required int MaxLen { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public required SemaphoreSlim HandoffGate { get; init; }
        public int Done;
        public int Failed;
    }

    private async Task ProcessSpeakBatchItemAsync(SpeakBatchRunState state, SpeakWorkItem item, CancellationToken ct)
    {
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var text = item.Text.Trim();
            if (text.Length == 0)
            {
                Interlocked.Increment(ref state.Done);
                return;
            }
            if (text.Length > state.MaxLen)
            {
                await LogSpeakBatchHandoffAsync(
                        state,
                        $"  S{item.Scene:D2}C{item.Clip:D2}: text {text.Length} chars exceeds model limit {state.MaxLen} — skip",
                        ct)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref state.Failed);
                Interlocked.Increment(ref state.Done);
                return;
            }

            var (audioBytes, ext, err) = await SynthesizeLineAsync(
                state.Ctx, state.ProjectId, state.CharKey, text, "speak_batch", ct).ConfigureAwait(false);

            if (audioBytes is not { Length: > 0 })
            {
                await LogSpeakBatchHandoffAsync(
                        state,
                        $"  S{item.Scene:D2}C{item.Clip:D2}: fail — {err ?? "no audio"}",
                        ct)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref state.Failed);
                Interlocked.Increment(ref state.Done);
                return;
            }

            await SaveSpeakBatchAudioAsync(state, item, audioBytes, ext, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await LogSpeakBatchHandoffAsync(
                    state,
                    $"  S{item.Scene:D2}C{item.Clip:D2}: exception — {ex.Message}",
                    CancellationToken.None)
                .ConfigureAwait(false);
            Interlocked.Increment(ref state.Failed);
            Interlocked.Increment(ref state.Done);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task LogSpeakBatchHandoffAsync(SpeakBatchRunState state, string message, CancellationToken ct)
    {
        await state.HandoffGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AppendLogAsync(message).ConfigureAwait(false);
        }
        finally { state.HandoffGate.Release(); }
    }

    private async Task SaveSpeakBatchAudioAsync(
        SpeakBatchRunState state,
        SpeakWorkItem item,
        byte[] audioBytes,
        string ext,
        CancellationToken ct)
    {
        var relPath = MediaRegistryService.RevoiceAudioRelativePath(item.Scene, item.Clip, ext);
        var projectDir = await _projects.GetProjectDirAsync(state.ProjectId, ct).ConfigureAwait(false);
        var absPath = Path.Combine(
            projectDir,
            relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absPath) ?? ".");
        await File.WriteAllBytesAsync(absPath, audioBytes, ct).ConfigureAwait(false);

        // Ticket form used by GET /api/projects/{id}/media/file
        var ticket = _mediaProxy.Issue($"{state.ProjectId}:{relPath}", TimeSpan.FromMinutes(45));
        var clientUrl =
            $"/api/projects/{Uri.EscapeDataString(state.ProjectId)}/media/file" +
            $"?path={Uri.EscapeDataString(relPath)}&ticket={ticket}";

        await state.HandoffGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var idx = Interlocked.Increment(ref state.Done);
            await UpdateAsync(s =>
            {
                s.Index = idx;
                s.Scene = item.Scene;
                s.Clip = item.Clip;
                s.ClientMediaUrl = clientUrl;
                s.ClientRelativePath = relPath;
                s.Message = $"Speak-batch: S{item.Scene:D2} C{item.Clip} ({idx}/{state.WorkCount})…";
            }).ConfigureAwait(false);
            await AppendLogAsync(
                    $"  S{item.Scene:D2}C{item.Clip:D2}: ready → {relPath} ({audioBytes.Length / 1024} KB)")
                .ConfigureAwait(false);
        }
        finally { state.HandoffGate.Release(); }
    }

    private async Task FinishSpeakBatchAsync(int failCount, int workCount)
    {
        if (failCount == 0)
            await FinishAsync(StatusDone, $"Speak-batch complete — {workCount} line(s)").ConfigureAwait(false);
        else if (failCount >= workCount)
            await FinishAsync(StatusError, $"Speak-batch failed — all {failCount} line(s) failed", "all failed")
                .ConfigureAwait(false);
        else
            await FinishAsync(
                    StatusPartial,
                    $"Speak-batch partial — {workCount - failCount} ok, {failCount} failed")
                .ConfigureAwait(false);
    }

    private sealed class SpeakWorkItem
    {
        public int Scene { get; init; }
        public int Clip { get; init; }
        public string Text { get; init; } = "";
    }

    private async Task<List<SpeakWorkItem>> BuildSpeakBatchWorkAsync(
        StartSpeakBatchRequest req,
        string projectId,
        string charKey,
        CancellationToken ct)
    {
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);

        // Explicit clips: text override or pull from blueprint
        if (req.Clips is { Count: > 0 })
            return await BuildSpeakWorkFromExplicitClipsAsync(req, projectId, projectDir, ct).ConfigureAwait(false);

        // Auto: all blueprint clips (optionally narrator-only)
        return await BuildSpeakWorkFromBlueprintAsync(req, projectId, projectDir, charKey, ct).ConfigureAwait(false);
    }

    private static bool ShouldSkipExistingRevoice(bool onlyMissing, string projectDir, int scene, int clip)
    {
        if (!onlyMissing)
            return false;
        var rel = MediaRegistryService.RevoiceAudioRelativePath(scene, clip);
        return File.Exists(Path.Combine(projectDir, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task<List<SpeakWorkItem>> BuildSpeakWorkFromExplicitClipsAsync(
        StartSpeakBatchRequest req,
        string projectId,
        string projectDir,
        CancellationToken ct)
    {
        var list = new List<SpeakWorkItem>();
        using var bp = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
        foreach (var c in req.Clips!.OrderBy(x => x.Scene).ThenBy(x => x.Clip))
        {
            if (c.Scene <= 0 || c.Clip <= 0) continue;
            var text = ResolveExplicitClipSpeakText(c, bp);
            if (text.Length == 0) continue;

            if (ShouldSkipExistingRevoice(req.OnlyMissing, projectDir, c.Scene, c.Clip))
                continue;

            list.Add(new SpeakWorkItem { Scene = c.Scene, Clip = c.Clip, Text = text });
        }
        return list;
    }

    private static string ResolveExplicitClipSpeakText(SpeakBatchClip c, JsonDocument? bp)
    {
        var text = (c.Text ?? "").Trim();
        if (text.Length == 0 && bp is not null)
            text = FindClipDialogue(bp.RootElement, c.Scene, c.Clip);
        return ClipVideoPromptBuilder.SanitizeSpokenDialogue(text);
    }

    private async Task<List<SpeakWorkItem>> BuildSpeakWorkFromBlueprintAsync(
        StartSpeakBatchRequest req,
        string projectId,
        string projectDir,
        string charKey,
        CancellationToken ct)
    {
        var list = new List<SpeakWorkItem>();
        using var blueprint = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
        if (blueprint is null)
            throw new InvalidOperationException(
                $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

        if (!blueprint.RootElement.TryGetProperty(ScenesKey, out var scenesEl) ||
            scenesEl.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var s in scenesEl.EnumerateArray())
        {
            var sn = ReadSceneNumber(s);
            if (sn <= 0) continue;
            if (!s.TryGetProperty(VeoClipsKey, out var clipsEl) || clipsEl.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var c in clipsEl.EnumerateArray())
                TryAddSpeakWorkFromBlueprintClip(list, c, sn, req, projectDir, charKey);
        }

        return list.OrderBy(x => x.Scene).ThenBy(x => x.Clip).ToList();
    }

    private static int ReadSceneNumber(JsonElement s) =>
        s.TryGetProperty(JsonKeys.SceneNumber, out var snEl) && snEl.TryGetInt32(out var n) ? n : 0;

    private static void TryAddSpeakWorkFromBlueprintClip(
        List<SpeakWorkItem> list,
        JsonElement c,
        int sn,
        StartSpeakBatchRequest req,
        string projectDir,
        string charKey)
    {
        var cn = ClipKeying.ClipNumber(c);
        if (cn <= 0) return;

        var (dialogue, speaker) = ReadClipDialogueAndSpeaker(c);
        dialogue = ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue);
        if (string.IsNullOrWhiteSpace(dialogue)) return;

        if (req.NarratorOnly && !IsNarratorSpeaker(speaker, charKey))
            return;

        if (ShouldSkipExistingRevoice(req.OnlyMissing, projectDir, sn, cn))
            return;

        list.Add(new SpeakWorkItem { Scene = sn, Clip = cn, Text = dialogue });
    }

    private static (string Dialogue, string? Speaker) ReadClipDialogueAndSpeaker(JsonElement c)
    {
        var (dialogue, speaker) = ReadAudioPayloadDialogueAndSpeaker(c);
        if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty(JsonKeys.Dialogue, out var rootD))
            dialogue = rootD.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(speaker) && c.TryGetProperty("speaker", out var rootSp))
            speaker = rootSp.GetString();
        return (dialogue, speaker);
    }

    private static (string Dialogue, string? Speaker) ReadAudioPayloadDialogueAndSpeaker(JsonElement c)
    {
        string? speaker = null;
        var dialogue = "";
        if (c.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object)
        {
            if (ap.TryGetProperty(JsonKeys.Dialogue, out var d))
                dialogue = d.GetString() ?? "";
            if (ap.TryGetProperty("speaker", out var sp))
                speaker = sp.GetString();
        }
        return (dialogue, speaker);
    }

    private static bool IsNarratorSpeaker(string? speaker, string narratorKey) =>
        CastKindClassifier.IsNarratorSpeaker(speaker, narratorKey);

    private static string FindClipDialogue(JsonElement root, int scene, int clip)
    {
        if (!root.TryGetProperty(ScenesKey, out var scenes) || scenes.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var s in scenes.EnumerateArray())
        {
            if (ReadSceneNumber(s) != scene)
                continue;
            return FindClipDialogueInScene(s, clip);
        }
        return "";
    }

    private static string FindClipDialogueInScene(JsonElement sceneEl, int clip)
    {
        if (!sceneEl.TryGetProperty(VeoClipsKey, out var clips) || clips.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var c in clips.EnumerateArray())
        {
            if (ClipKeying.ClipNumber(c) != clip)
                continue;
            var text = TryReadClipDialogueText(c);
            if (text is not null)
                return text;
        }
        return "";
    }

    private static string? TryReadClipDialogueText(JsonElement c)
    {
        if (c.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty(JsonKeys.Dialogue, out var d))
            return d.GetString() ?? "";
        if (c.TryGetProperty(JsonKeys.Dialogue, out var rootD))
            return rootD.GetString() ?? "";
        return null;
    }

    // ── Shared cloned-voice TTS helpers (used by speak-batch and movie-wide voice substitution) ──

    /// <summary>Resolved once-per-job voice/model context for cloned-voice TTS.</summary>
    private sealed class SpeakContext
    {
        public required string VoiceId { get; init; }
        public SupportedModelEntry? Entry { get; init; }
        public required string ProviderId { get; init; }
        public bool UseEleven { get; init; }
        public required string SpeakModelId { get; init; }
        public int MaxLen { get; init; }
    }

    /// <summary>
    /// Resolve the clone voice id + speak model + provider for a character, mirroring the catalog
    /// resolution the speak-batch job used inline. Returns an error message instead of a context when
    /// no clone exists or the needed provider key is missing.
    /// </summary>
    private async Task<(SpeakContext? Ctx, string? Error)> ResolveSpeakContextAsync(
        string projectId, string charKey, string? model, CancellationToken ct)
    {
        var voiceId = _projects.GetVoiceCloneProviderId(projectId, charKey);
        if (string.IsNullOrWhiteSpace(voiceId))
            return (null, "No cloned voice on this character — record and apply a sample first.");

        var seedProvider = _projects.GetVoiceProviderId(projectId, charKey) ?? "";
        model = await LoadConfiguredVoiceModelIfMissingAsync(projectId, model, ct).ConfigureAwait(false);
        var entry = ResolveVoiceCatalogEntry(ref model, seedProvider);
        return TryBuildSpeakContext(voiceId, entry, seedProvider, model);
    }

    private async Task<string?> LoadConfiguredVoiceModelIfMissingAsync(
        string projectId, string? model, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return model;
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        EnsureLabModelsAllowed(cfg);
        if (cfg.TryGetValue("voice_model_name", out var vm) && vm.ValueKind == JsonValueKind.String)
            return vm.GetString();
        return model;
    }

    private static SupportedModelEntry? ResolveVoiceCatalogEntry(ref string? model, string seedProvider)
    {
        SupportedModelEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(model))
            entry = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                    ?? SupportedModelCatalog.Find(model);
        if (entry is { IsVoiceCloneStep: true })
        {
            entry = FirstEnabledSpeakModelForProvider(entry.ProviderId);
            model = entry?.Id;
        }
        if (entry is null)
        {
            entry = FirstEnabledSpeakModelForSeed(seedProvider);
            model = entry?.Id ?? model;
        }
        return entry;
    }

    private static SupportedModelEntry? FirstEnabledSpeakModelForProvider(string? providerId) =>
        SupportedModelCatalog.FirstEnabledSpeakModel(providerId);

    private static SupportedModelEntry? FirstEnabledSpeakModelForSeed(string seedProvider) =>
        SupportedModelCatalog.FirstEnabledSpeakModel(
            string.IsNullOrWhiteSpace(seedProvider) ? null : seedProvider);

    private (SpeakContext? Ctx, string? Error) TryBuildSpeakContext(
        string voiceId, SupportedModelEntry? entry, string seedProvider, string? model)
    {
        var providerId = entry?.ProviderId
                         ?? (string.IsNullOrWhiteSpace(seedProvider) ? null : seedProvider)
                         ?? "unknown";
        var useEleven = providerId.Equals("elevenlabs", StringComparison.OrdinalIgnoreCase)
                        || entry?.Provider == ModelProviderFamily.ElevenLabs
                        || voiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase);

        var configError = SpeakProviderConfigError(useEleven, voiceId);
        if (configError is not null)
            return (null, configError);

        var speakModelId = SupportedModelCatalog.ResolveSpeakModelId(entry?.Id, providerId, model);
        if (entry?.MaxPromptLength is not int maxLen)
            return (null, $"Model '{entry?.Id ?? "(null)"}' has no maxPromptLength in models_catalog.json.");

        return (new SpeakContext
        {
            VoiceId = voiceId,
            Entry = entry,
            ProviderId = providerId,
            UseEleven = useEleven,
            SpeakModelId = speakModelId,
            MaxLen = maxLen,
        }, null);
    }

    private string? SpeakProviderConfigError(bool useEleven, string voiceId)
    {
        if (useEleven && !_voiceClient.IsConfigured && !voiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase))
            return "ElevenLabs key is not configured.";
        if (!useEleven && !_voiceClone.IsConfigured)
            return "Voice provider (Fal) is not configured.";
        return null;
    }

    /// <summary>
    /// Synthesize one line of cloned-voice speech (ElevenLabs bytes or Fal url→download) and log the
    /// TTS telemetry. Returns the audio bytes + file extension (or an error). Keys stay on the server.
    /// </summary>
    private async Task<(byte[]? Audio, string Ext, string? Error)> SynthesizeLineAsync(
        SpeakContext ctx, string projectId, string charKey, string text, string mode, CancellationToken ct)
    {
        var (audioBytes, ext, err) = ctx.UseEleven
            ? await SynthesizeViaElevenAsync(ctx, text, ct).ConfigureAwait(false)
            : await SynthesizeViaCloneDownloadAsync(ctx, text, ct).ConfigureAwait(false);

        await LogTtsTelemetryAsync(ctx, projectId, charKey, text, mode, audioBytes, err, ct).ConfigureAwait(false);
        return (audioBytes, ext, err);
    }

    private async Task<(byte[]? Audio, string Ext, string? Error)> SynthesizeViaElevenAsync(
        SpeakContext ctx, string text, CancellationToken ct)
    {
        var tts = await _voiceClient.TextToSpeechAsync(ctx.VoiceId, text, ctx.SpeakModelId, ct)
            .ConfigureAwait(false);
        if (!tts.Ok || tts.AudioBytes is not { Length: > 0 })
            return (null, ".mp3", tts.Error ?? "TTS failed");
        return (tts.AudioBytes, tts.FileExtension ?? ".mp3", null);
    }

    private async Task<(byte[]? Audio, string Ext, string? Error)> SynthesizeViaCloneDownloadAsync(
        SpeakContext ctx, string text, CancellationToken ct)
    {
        var audioUrl = await _voiceClone.SynthesizeSpeechAsync(text, ctx.VoiceId, ctx.SpeakModelId, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(audioUrl))
            return (null, ".mp3", "Speech synthesis failed");
        try
        {
            var http = _httpFactory.CreateClient();
            using var resp = await http.GetAsync(audioUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, ".mp3", $"Download TTS failed ({(int)resp.StatusCode})");
            var audioBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var ctHeader = resp.Content.Headers.ContentType?.MediaType ?? "";
            return (audioBytes, GuessTtsExtension(ctHeader), null);
        }
        catch (Exception ex)
        {
            return (null, ".mp3", ex.Message);
        }
    }

    private static string GuessTtsExtension(string contentType)
    {
        if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase))
            return ".wav";
        if (contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("m4a", StringComparison.OrdinalIgnoreCase))
            return ".m4a";
        return ".mp3";
    }

    private async Task LogTtsTelemetryAsync(
        SpeakContext ctx, string projectId, string charKey, string text, string mode,
        byte[]? audioBytes, string? err, CancellationToken ct)
    {
        if (_telemetry is null) return;
        var estimatedUsd = ctx.Entry?.CostPerThousandCharsUsd is { } rate
            ? Math.Round(rate * text.Length / 1000.0, 4)
            : (double?)null;
        await _telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            ProjectId = projectId,
            Kind = "tts",
            Mode = mode,
            Model = ctx.SpeakModelId,
            Provider = ctx.ProviderId,
            CharKey = charKey,
            PromptChars = text.Length,
            EstimatedUsd = estimatedUsd,
            Ok = audioBytes is { Length: > 0 },
            Error = err,
        }, ct).ConfigureAwait(false);
    }

    // ── Movie-wide "substitute my cloned voice" ─────────────────────────────────────────────────

    /// <summary>
    /// Start a tracked job that walks every clip in the movie, associates each dialogue line with its
    /// speaker (from the blueprint), synthesizes the line in the character's cloned voice, and updates
    /// the persisted per-clip speech alignment. The browser later detects real speech timestamps
    /// (free ffmpeg silence detection) and overlays the cloned voice at those windows. On a re-run,
    /// persisted timestamps are reused so detection is skipped.
    /// </summary>
    public Task<JobSnapshot> StartVoiceSubstitutionAsync(StartVoiceSubstitutionRequest req)
    {
        var (projectId, charKey) = ResolveProjectAndCharKey(req.ProjectId, req.CharKey);

        return StartBackgroundJobAsync(
            ct => RunVoiceSubstitutionAsync(req, projectId, charKey, ct),
            new JobEnqueueMeta
            {
                Kind = "voice-substitution",
                ProjectId = projectId,
                CharKey = charKey,
                Message = $"Queued voice substitution for {charKey}…",
            },
            lockResources: new[] { LockKeys.Character(projectId, charKey) },
            lockReason: $"voice-substitution {charKey}",
            failIfLocked: req.FailIfLocked);
    }

    private async Task RunVoiceSubstitutionAsync(
        StartVoiceSubstitutionRequest req,
        string projectId,
        string charKey,
        CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "voice-substitution",
            ProjectId = projectId,
            CharKey = charKey,
            Message = "Voice substitution: building work list…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync().ConfigureAwait(false);

        try
        {
            var work = await TryPrepareVoiceSubstitutionAsync(req, projectId, charKey, ct).ConfigureAwait(false);
            if (work is null) return;
            await RunVoiceSubstitutionWorkAsync(req, work, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, "Voice substitution cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voice substitution failed for {ProjectId}", projectId);
            await FinishAsync(StatusError, ex.Message, ex.Message).ConfigureAwait(false);
        }
    }

    private sealed class VoiceSubstitutionWork
    {
        public required SpeakContext Ctx { get; init; }
        public required string ProjectId { get; init; }
        public required string CharKey { get; init; }
        public required string ProjectDir { get; init; }
        public required List<IGrouping<int, VoiceAlignmentStore.ClipDialogueLines>> SceneGroups { get; init; }
        public required HashSet<int> ScenesWithOtherSpeakers { get; init; }
        public required int TotalLines { get; init; }
        public required int TotalScenes { get; init; }
        public required ProjectVoiceAlignment Alignment { get; init; }
    }

    private sealed class VoiceSubProgress
    {
        public int Done;
        public int Failed;
    }

    private async Task<VoiceSubstitutionWork?> TryPrepareVoiceSubstitutionAsync(
        StartVoiceSubstitutionRequest req, string projectId, string charKey, CancellationToken ct)
    {
        if (_voiceAlignment is null)
        {
            await FinishAsync(StatusError, "Voice alignment store unavailable.", "no alignment store")
                .ConfigureAwait(false);
            return null;
        }

        var (ctx, ctxErr) = await ResolveSpeakContextAsync(projectId, charKey, req.Model, ct).ConfigureAwait(false);
        if (ctx is null)
        {
            await FinishAsync(StatusError, ctxErr ?? VoiceNotConfigured, ctxErr ?? VoiceNotConfigured)
                .ConfigureAwait(false);
            return null;
        }

        using var blueprint = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
        if (blueprint is null)
        {
            await FinishAsync(StatusError, "No shot plan for this project yet.", "no blueprint")
                .ConfigureAwait(false);
            return null;
        }

        Func<string, bool>? filter = req.NarratorOnly
            ? spk => CastKindClassifier.SameCharacter(spk, charKey)
            : null;
        var clipLines = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, filter);
        if (clipLines.Count == 0)
        {
            await FinishAsync(StatusDone, "No matching dialogue lines to substitute.").ConfigureAwait(false);
            return null;
        }

        var scenesWithOtherSpeakers = req.NarratorOnly
            ? VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, null)
                .Where(cl => cl.Lines.Any(l => !CastKindClassifier.SameCharacter(l.CharacterKey, charKey)))
                .Select(cl => cl.Scene)
                .ToHashSet()
            : new HashSet<int>();

        var sceneGroups = clipLines
            .GroupBy(c => c.Scene)
            .OrderBy(g => g.Key)
            .ToList();

        var totalScenes = sceneGroups.Count;
        var totalLines = clipLines.Sum(c => c.Lines.Count);
        await UpdateAsync(s =>
        {
            s.Total = totalLines;
            s.Index = 0;
            s.Message = $"Voice substitution: {totalLines} line(s) across {totalScenes} scene(s) · {ctx.ProviderId}";
        }).ConfigureAwait(false);
        await AppendLogAsync(Snapshot.Message ?? "").ConfigureAwait(false);

        return new VoiceSubstitutionWork
        {
            Ctx = ctx,
            ProjectId = projectId,
            CharKey = charKey,
            ProjectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false),
            SceneGroups = sceneGroups,
            ScenesWithOtherSpeakers = scenesWithOtherSpeakers,
            TotalLines = totalLines,
            TotalScenes = totalScenes,
            Alignment = new ProjectVoiceAlignment
            {
                ProjectId = projectId,
                CharKey = charKey,
                SceneVoices = new List<SceneVoiceTrack>(sceneGroups.Count),
            },
        };
    }

    private async Task RunVoiceSubstitutionWorkAsync(
        StartVoiceSubstitutionRequest req, VoiceSubstitutionWork work, CancellationToken ct)
    {
        var progress = new VoiceSubProgress();
        foreach (var group in work.SceneGroups)
        {
            ct.ThrowIfCancellationRequested();
            var track = new SceneVoiceTrack
            {
                Scene = group.Key,
                HasOtherSpeakers = work.ScenesWithOtherSpeakers.Contains(group.Key),
            };
            await ProcessSceneVoiceLinesAsync(req, work, group, track, progress, ct).ConfigureAwait(false);
            work.Alignment.SceneVoices.Add(track);
        }

        await _voiceAlignment!.SaveAsync(work.ProjectId, work.Alignment, ct).ConfigureAwait(false);
        await AppendLogAsync($"Alignment saved → {VoiceAlignmentStore.RelativePath}").ConfigureAwait(false);
        await FinishVoiceSubstitutionAsync(progress.Failed, work.TotalLines, work.TotalScenes).ConfigureAwait(false);
    }

    private async Task ProcessSceneVoiceLinesAsync(
        StartVoiceSubstitutionRequest req,
        VoiceSubstitutionWork work,
        IGrouping<int, VoiceAlignmentStore.ClipDialogueLines> group,
        SceneVoiceTrack track,
        VoiceSubProgress progress,
        CancellationToken ct)
    {
        var sceneNo = group.Key;
        var sceneLines = group
            .OrderBy(c => c.Clip)
            .SelectMany(c => c.Lines)
            .Select(l => l.Text.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var lineNo = 0;
        foreach (var lineTextRaw in sceneLines)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessOneVoiceSubstitutionLineAsync(
                    req, work, sceneNo, lineNo, lineTextRaw, track, progress, ct)
                .ConfigureAwait(false);
            lineNo++;
        }
    }

    private async Task ProcessOneVoiceSubstitutionLineAsync(
        StartVoiceSubstitutionRequest req,
        VoiceSubstitutionWork work,
        int sceneNo,
        int lineNo,
        string lineTextRaw,
        SceneVoiceTrack track,
        VoiceSubProgress progress,
        CancellationToken ct)
    {
        var text = lineTextRaw;
        if (text.Length > work.Ctx.MaxLen)
        {
            await AppendLogAsync(
                    $"  S{sceneNo:D2} L{lineNo:D2}: {text.Length} chars exceeds model limit {work.Ctx.MaxLen} — truncating.")
                .ConfigureAwait(false);
            text = text[..work.Ctx.MaxLen];
        }

        var svl = new SceneVoiceLine { Index = lineNo, Text = text };
        var relPath = MediaRegistryService.RevoiceSceneLineAudioRelativePath(sceneNo, lineNo);
        var absPath = Path.Combine(work.ProjectDir, relPath.Replace('/', Path.DirectorySeparatorChar));

        if (req.OnlyMissing && File.Exists(absPath))
        {
            svl.VoiceAudioRelativePath = relPath;
            track.Lines.Add(svl);
            var idxSkip = Interlocked.Increment(ref progress.Done);
            await UpdateAsync(s => { s.Index = idxSkip; s.Scene = sceneNo; }).ConfigureAwait(false);
            await AppendLogAsync($"  S{sceneNo:D2} L{lineNo:D2}: reuse existing → {relPath}").ConfigureAwait(false);
            return;
        }

        var (audioBytes, ext, err) = await SynthesizeLineAsync(
            work.Ctx, work.ProjectId, work.CharKey, text, "voice_substitution", ct).ConfigureAwait(false);

        if (audioBytes is not { Length: > 0 })
        {
            await AppendLogAsync($"  S{sceneNo:D2} L{lineNo:D2}: fail — {err ?? "no audio"}").ConfigureAwait(false);
            Interlocked.Increment(ref progress.Failed);
            Interlocked.Increment(ref progress.Done);
            track.Lines.Add(svl);
            return;
        }

        relPath = MediaRegistryService.RevoiceSceneLineAudioRelativePath(sceneNo, lineNo, ext);
        absPath = Path.Combine(work.ProjectDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absPath) ?? ".");
        await File.WriteAllBytesAsync(absPath, audioBytes, ct).ConfigureAwait(false);
        svl.VoiceAudioRelativePath = relPath;
        track.Lines.Add(svl);

        var ticket = _mediaProxy.Issue($"{work.ProjectId}:{relPath}", TimeSpan.FromMinutes(45));
        var clientUrl =
            $"/api/projects/{Uri.EscapeDataString(work.ProjectId)}/media/file" +
            $"?path={Uri.EscapeDataString(relPath)}&ticket={ticket}";

        var idx = Interlocked.Increment(ref progress.Done);
        await UpdateAsync(s =>
        {
            s.Index = idx;
            s.Scene = sceneNo;
            s.ClientMediaUrl = clientUrl;
            s.ClientRelativePath = relPath;
            s.Message = $"Voice substitution: S{sceneNo:D2} L{lineNo:D2} ({idx}/{work.TotalLines})…";
        }).ConfigureAwait(false);
        await AppendLogAsync(
                $"  S{sceneNo:D2} L{lineNo:D2}: ready → {relPath} ({audioBytes.Length / 1024} KB)")
            .ConfigureAwait(false);
    }

    private async Task FinishVoiceSubstitutionAsync(int failed, int totalLines, int totalScenes)
    {
        if (failed == 0)
            await FinishAsync(StatusDone, $"Voice substitution ready — {totalLines} line(s) across {totalScenes} scene(s)").ConfigureAwait(false);
        else if (failed >= totalLines)
            await FinishAsync(StatusError, $"Voice substitution failed — all {failed} line(s) failed", "all failed")
                .ConfigureAwait(false);
        else
            await FinishAsync(
                    StatusPartial,
                    $"Voice substitution partial — {totalLines - failed} ok, {failed} failed")
                .ConfigureAwait(false);
    }

    /// <summary>
    /// H2 — stamp job-level take trigger for cost ledger events (fill_holes / stale_regen / user_regen / …).
    /// </summary>
    private static void ApplyVideoTakeContext(bool onlyMissing, string? takeTrigger, bool forceRegen)
    {
        var run = CurrentRun.Value;
        if (run is null) return;
        run.OnlyMissing = onlyMissing;
        var explicitKind = VideoTakeKinds.NormalizeOptional(takeTrigger);
        if (explicitKind is not null)
            run.TakeTrigger = explicitKind;
        else if (forceRegen || !onlyMissing)
            run.TakeTrigger = VideoTakeKinds.UserRegen;
        else
            // onlyMissing with no explicit trigger → per-clip Resolve → initial (first) or user_regen
            run.TakeTrigger = null;
    }

    private async Task RunBatchGenAsync(StartBatchGenRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);
        ApplyVideoTakeContext(req.OnlyMissing, req.TakeTrigger, forceRegen: req.Clips is { Count: > 0 });
        // G3/G4: draft production mode softens first-watch cast plate lock (plates optional).
        req.RequireLockedCharacters = EffectiveRequireLockedCharacters(req.RequireLockedCharacters, projectId);

        var hasClips = req.Clips is { Count: > 0 };
        var scenes = DistinctOrderedScenes(hasClips, req);
        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "batch",
            ProjectId = projectId,
            Message = BatchStartMessage(hasClips, req, scenes.Count),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await EnsureVideoProviderConfiguredAsync(projectId, ct).ConfigureAwait(false);

            using var bp = await _projects.LoadBlueprintAsync(projectId, ct)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            ApplyLockedCharacterGateForBatch(req, projectId, scenes, bp);

            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(projectDir, AssetsFolder, VideoFolder));

            var work = await CollectBatchWorkAsync(req, hasClips, scenes, bp, projectDir).ConfigureAwait(false);
            if (work.Count == 0)
            {
                await AppendLogAsync("Batch: nothing to generate (only_missing).");
                await FinishAsync(StatusDone, "No clips to generate");
                return;
            }

            // Fail before any API spend if the selected video model cannot do multi-clip / plates.
            await EnsureVideoModelCapabilitiesAsync(
                    projectId,
                    needReferenceImages: req.RequireLockedCharacters,
                    ct,
                    modelOverride: req.VideoModel)
                .ConfigureAwait(false);

            var resolution = await ResolveVideoResolutionAsync(projectId, req.Resolution, ct);
            await UpdateAsync(s =>
            {
                s.Total = work.Count;
                s.Index = 0;
                s.Message = $"Batch: {work.Count} clip(s) across {scenes.Count} scene(s) @ {resolution}";
            });
            await AppendLogAsync(Snapshot.Message ?? "");

            var (done, failed, firstClipError, cancelled) = await GenerateBatchClipsLoopAsync(
                work, bp, projectId, projectDir, resolution, req.VideoModel, ct).ConfigureAwait(false);
            if (cancelled)
                return;

            var (status, msg) = FormatBatchFinish(done, failed, firstClipError);
            await FinishAsync(status, msg, failed > 0 ? msg : null);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch gen failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private bool EffectiveRequireLockedCharacters(bool requireLocked, string projectId)
    {
        if (requireLocked && _projects.IsDraftProductionMode(projectId))
            return false;
        return requireLocked;
    }

    private static List<int> DistinctOrderedScenes(bool hasClips, StartBatchGenRequest req) =>
        (hasClips ? (req.Clips ?? new List<ClipTarget>()).Select(c => c.Scene) : req.Scenes)
            .Distinct().OrderBy(s => s).ToList();

    private static string BatchStartMessage(bool hasClips, StartBatchGenRequest req, int sceneCount) =>
        hasClips
            ? $"Batch: {(req.Clips ?? new List<ClipTarget>()).Count} clip(s)…"
            : $"Batch: {sceneCount} scene(s)…";

    private void ApplyLockedCharacterGateForBatch(
        StartBatchGenRequest req,
        string projectId,
        List<int> scenes,
        JsonDocument bp)
    {
        if (!req.RequireLockedCharacters)
            return;
        // The auto-inserted end-credits scene is a title card with no real cast, so it is
        // exempt from the locked-character gate. Detect it by the same blueprint signal
        // ProjectStore uses to derive SceneSummary.IsCredits — not a hardcoded scene number
        // or story-specific title string (AGENTS.md: generalize, no story-specific strings).
        var castScenes = scenes
            .Where(sn => !IsCreditsScene(FindScene(bp.RootElement, sn)))
            .ToList();

        // Project-wide first (all cast voice + locked images), then per-scene mentions.
        // Skip the cast-readiness gate entirely when the credits card is the ONLY scene being
        // generated (nothing on-screen to lock — no wasted spend to guard against).
        if (castScenes.Count > 0)
            EnsureCastReadyForVideo(projectId);
        foreach (var sn in castScenes)
            EnsureSceneCharactersLocked(projectId, sn);
    }

    private async Task<List<(int Scene, int Clip, JsonElement ClipEl)>> CollectBatchWorkAsync(
        StartBatchGenRequest req,
        bool hasClips,
        List<int> scenes,
        JsonDocument bp,
        string projectDir)
    {
        if (hasClips)
            return await CollectExplicitClipWorkAsync(req, bp).ConfigureAwait(false);
        return await CollectSceneClipWorkAsync(req, scenes, bp, projectDir).ConfigureAwait(false);
    }

    private async Task<List<(int Scene, int Clip, JsonElement ClipEl)>> CollectExplicitClipWorkAsync(
        StartBatchGenRequest req,
        JsonDocument bp)
    {
        // Explicit multi-select of specific clips — always force-regen (ignore OnlyMissing),
        // same as single-clip regen.
        var work = new List<(int Scene, int Clip, JsonElement ClipEl)>();
        foreach (var target in (req.Clips ?? new List<ClipTarget>()).OrderBy(c => c.Scene).ThenBy(c => c.Clip))
        {
            var sceneEl = FindScene(bp.RootElement, target.Scene);
            if (sceneEl is null)
            {
                await AppendLogAsync($"Scene {target.Scene}: not in blueprint — skip");
                continue;
            }
            // Credits render deterministically client-side, never through the video model —
            // stop it here too in case a caller other than the Scenes page reaches this endpoint.
            if (IsCreditsScene(sceneEl))
            {
                await AppendLogAsync($"Scene {target.Scene}: end-credits scene — skip (rendered client-side)");
                continue;
            }
            var clipEl = FindClipInScene(sceneEl.Value, target.Clip);
            if (clipEl is null)
            {
                await AppendLogAsync($"S{target.Scene:D2}C{target.Clip}: not in blueprint — skip");
                continue;
            }
            work.Add((Scene: target.Scene, Clip: target.Clip, ClipEl: clipEl.Value.Clone()));
        }
        return work;
    }

    private async Task<List<(int Scene, int Clip, JsonElement ClipEl)>> CollectSceneClipWorkAsync(
        StartBatchGenRequest req,
        List<int> scenes,
        JsonDocument bp,
        string projectDir)
    {
        var work = new List<(int Scene, int Clip, JsonElement ClipEl)>();
        foreach (var sn in scenes)
        {
            var sceneEl = FindScene(bp.RootElement, sn);
            if (sceneEl is null)
            {
                await AppendLogAsync($"Scene {sn}: not in blueprint — skip");
                continue;
            }
            // Credits render deterministically client-side, never through the video model —
            // stop it here too in case a caller other than the Scenes page reaches this endpoint.
            if (IsCreditsScene(sceneEl))
            {
                await AppendLogAsync($"Scene {sn}: end-credits scene — skip (rendered client-side)");
                continue;
            }
            if (!sceneEl.Value.TryGetProperty(VeoClipsKey, out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
            {
                await AppendLogAsync($"Scene {sn}: no veo_clips — skip");
                continue;
            }

            foreach (var c in clipsEl.EnumerateArray())
                TryAddSceneClipWork(work, c, sn, projectDir, req.OnlyMissing);
        }
        return work;
    }

    private static void TryAddSceneClipWork(
        List<(int Scene, int Clip, JsonElement ClipEl)> work,
        JsonElement c,
        int sn,
        string projectDir,
        bool onlyMissing)
    {
        var cn = ClipKeying.ClipNumber(c);
        if (cn <= 0) return;
        var path = Path.Combine(projectDir, AssetsFolder, VideoFolder, $"scene_{sn:D2}_clip_{cn:D2}.mp4");
        var missing = !ClipPresentOnServerOrClient(path);
        if (!onlyMissing || missing)
            work.Add((Scene: sn, Clip: cn, ClipEl: c.Clone()));
    }

    private async Task<(int Done, int Failed, string? FirstClipError, bool Cancelled)> GenerateBatchClipsLoopAsync(
        List<(int Scene, int Clip, JsonElement ClipEl)> work,
        JsonDocument bp,
        string projectId,
        string projectDir,
        string resolution,
        string? videoModel,
        CancellationToken ct)
    {
        var done = 0;
        var failed = 0;
        string? firstClipError = null;
        // Per-scene (LastGeneratedClip, CarryoverPaddingSec) — batch work can interleave scenes,
        // so the padding nudge from one scene's overrun must never leak into a different scene.
        var sceneCarryover = new Dictionary<int, (int LastClip, double PaddingSec)>();
        for (var i = 0; i < work.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (sn, cn, clip) = work[i];
            await UpdateAsync(s =>
            {
                s.Index = i + 1;
                s.Scene = sn;
                s.Clip = cn;
                s.Message = $"Generating S{sn:D2} C{cn} ({i + 1}/{work.Count})…";
            });
            await AppendLogAsync(Snapshot.Message ?? "");

            try
            {
                await GenerateOneBatchClipAsync(
                    projectId, projectDir, sn, cn, clip, resolution, ct,
                    bp, sceneCarryover, videoModel).ConfigureAwait(false);
                done++;
            }
            catch (OperationCanceledException)
            {
                await FinishAsync(StatusCancelled, CancelledByUser);
                return (done, failed, firstClipError, true);
            }
            catch (Exception ex)
            {
                failed++;
                firstClipError ??= ex.Message;
                _log.LogError(ex, "Clip S{Scene}C{Clip} failed", sn, cn);
                await AppendLogAsync($"Failed S{sn:D2} C{cn}: {ex.Message}");
            }
        }
        return (done, failed, firstClipError, false);
    }

    private async Task GenerateOneBatchClipAsync(
        string projectId,
        string projectDir,
        int sn,
        int cn,
        JsonElement clip,
        string resolution,
        CancellationToken ct,
        JsonDocument bp,
        Dictionary<int, (int LastClip, double PaddingSec)> sceneCarryover,
        string? videoModel)
    {
        // Previous clip element in same scene (for prompt context)
        var prevClipEl = FindPreviousClipElement(bp.RootElement, sn, cn);
        var prior = sceneCarryover.TryGetValue(sn, out var p) ? p : (LastClip: 0, PaddingSec: 0.0);
        var incomingPadding = ResolveIncomingDurationPadding(cn, prior.LastClip, prior.PaddingSec);
        var overrun = await GenerateOneClipAsync(
            projectId, projectDir, sn, cn, clip, resolution, ct,
            previousClipEl: prevClipEl,
            blueprintRoot: bp.RootElement,
            incomingDurationPaddingSec: incomingPadding,
            modelOverride: videoModel);
        sceneCarryover[sn] = (cn, overrun);
        // Fresh clips x/y + status pills while batch is still running.
        _projects.InvalidateSceneListCache(projectId);
        await AppendLogAsync($"Done S{sn:D2} C{cn}");
    }

    private static JsonElement? FindPreviousClipElement(JsonElement root, int sn, int cn)
    {
        if (cn <= 1)
            return null;
        var sceneEl = FindScene(root, sn);
        if (sceneEl is null)
            return null;
        return FindClipInScene(sceneEl.Value, cn - 1);
    }

    private static (string Status, string Message) FormatBatchFinish(int done, int failed, string? firstClipError)
    {
        if (failed > 0 && done == 0)
            return (StatusError, FormatBatchFailedMessage(failed, firstClipError));
        if (failed > 0)
            return (StatusPartial, FormatBatchPartialMessage(done, failed, firstClipError));
        return (StatusDone, $"Batch finished ({done} clip(s))");
    }

    private static string FormatBatchFailedMessage(int failed, string? firstClipError) =>
        !string.IsNullOrWhiteSpace(firstClipError)
            ? $"Batch failed: {firstClipError}"
            : $"Batch failed ({failed} clip(s) failed, none ok)";

    private static string FormatBatchPartialMessage(int done, int failed, string? firstClipError) =>
        !string.IsNullOrWhiteSpace(firstClipError)
            ? $"Batch partial ({done} ok, {failed} failed): {firstClipError}"
            : $"Batch partial ({done} ok, {failed} failed)";

    private async Task RunSceneGenAsync(StartSceneGenRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);
        ApplyVideoTakeContext(req.OnlyMissing, req.TakeTrigger, forceRegen: req.Clip is > 0 && !req.OnlyMissing);
        // G3/G4: draft production mode softens first-watch cast plate lock (plates optional).
        req.RequireLockedCharacters = EffectiveRequireLockedCharacters(req.RequireLockedCharacters, projectId);

        Snapshot = new JobSnapshot
        {
            Status = StatusRunning,
            Kind = "scene",
            ProjectId = projectId,
            Scene = req.Scene,
            Message = "Starting…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await EnsureVideoProviderConfiguredAsync(projectId, ct).ConfigureAwait(false);

            using var bp = await _projects.LoadBlueprintAsync(projectId, ct)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            var sceneEl = FindScene(bp.RootElement, req.Scene)
                ?? throw new InvalidOperationException($"Scene {req.Scene} not in blueprint.");

            ThrowIfCreditsScene(sceneEl, req.Scene);
            ApplyLockedCharactersForScene(req.RequireLockedCharacters, projectId, req.Scene);

            var clipsEl = RequireSceneClips(sceneEl, req.Scene);
            var clips = clipsEl.EnumerateArray().ToList();
            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var videoDir = Path.Combine(projectDir, AssetsFolder, VideoFolder);
            Directory.CreateDirectory(videoDir);

            var todo = BuildSceneGenTodo(clips, req, videoDir);
            if (todo.Count == 0)
            {
                await AppendLogAsync($"Scene {req.Scene}: nothing to generate (only_missing).");
                await FinishAsync(StatusDone, "No clips to generate");
                return;
            }

            // Fail before any API spend if the selected video model cannot do multi-clip / plates.
            await EnsureVideoModelCapabilitiesAsync(
                    projectId,
                    needReferenceImages: req.RequireLockedCharacters,
                    ct)
                .ConfigureAwait(false);

            var resolution = await ResolveVideoResolutionAsync(projectId, req.Resolution, ct);
            var startMsg = $"Scene {req.Scene}: {todo.Count} clip(s) @ {resolution}";
            await UpdateAsync(s =>
            {
                s.Total = todo.Count;
                s.Index = 0;
                s.Message = startMsg;
            });
            await AppendLogAsync(startMsg);

            var (qaRetryOnFail, qaMaxRetries) = await LoadQaRetryConfigAsync(projectId, ct).ConfigureAwait(false);
            var (adminQaRetry, dialogueQa) = ResolveAdminQaRetry(qaRetryOnFail);
            await LogQaRetryStatusAsync(qaRetryOnFail, adminQaRetry, qaMaxRetries).ConfigureAwait(false);

            var (done, failed, cancelled) = await GenerateSceneClipsWithQaAsync(
                req, projectId, projectDir, sceneEl, bp.RootElement, todo,
                resolution, adminQaRetry, qaMaxRetries, dialogueQa, ct).ConfigureAwait(false);
            if (cancelled)
                return;

            var (status, msg) = FormatSceneGenFinish(done, failed);
            await FinishAsync(status, msg, failed > 0 ? msg : null);

            // P0 learning: single-clip regen (typical after auto-review apply)
            await TryAppendRegenLearningEventAsync(req, projectId, status, msg, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(StatusCancelled, CancelledByUser);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scene gen failed");
            await FinishAsync(StatusError, ex.Message, ex.Message);
        }
    }

    private static void ThrowIfCreditsScene(JsonElement sceneEl, int scene)
    {
        // The end-credits card is rendered deterministically client-side (canvas -> ffmpeg.wasm),
        // never through the video model — a video model asked to render a text-heavy title card
        // hallucinates unrelated footage. The Scenes page already routes credits scenes elsewhere,
        // but any other caller of this endpoint must be stopped here too, before spending an API call.
        if (IsCreditsScene(sceneEl))
            throw new InvalidOperationException(
                $"Scene {scene} is the end-credits scene — it is rendered client-side, not through the video model.");
    }

    private void ApplyLockedCharactersForScene(bool requireLocked, string projectId, int scene)
    {
        if (!requireLocked)
            return;
        EnsureCastReadyForVideo(projectId);
        EnsureSceneCharactersLocked(projectId, scene);
    }

    private static JsonElement RequireSceneClips(JsonElement sceneEl, int scene)
    {
        if (!sceneEl.TryGetProperty(VeoClipsKey, out var clipsEl) ||
            clipsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Scene {scene} has no veo_clips.");
        return clipsEl;
    }

    private static List<(int ClipNum, JsonElement Clip)> BuildSceneGenTodo(
        List<JsonElement> clips,
        StartSceneGenRequest req,
        string videoDir)
    {
        var todo = new List<(int ClipNum, JsonElement Clip)>();
        foreach (var c in clips)
            TryAddSceneGenTodoItem(todo, c, req, videoDir);
        return todo;
    }

    private static void TryAddSceneGenTodoItem(
        List<(int ClipNum, JsonElement Clip)> todo,
        JsonElement c,
        StartSceneGenRequest req,
        string videoDir)
    {
        var cn = ClipKeying.ClipNumber(c);
        if (cn <= 0) return;
        if (req.Clip is int onlyClip && onlyClip > 0 && cn != onlyClip)
            return;
        var path = Path.Combine(videoDir, $"scene_{req.Scene:D2}_clip_{cn:D2}.mp4");
        var missing = !ClipPresentOnServerOrClient(path);
        if (!req.OnlyMissing || missing)
            todo.Add((cn, c.Clone()));
    }

    private async Task<(bool RetryOnFail, int MaxRetries)> LoadQaRetryConfigAsync(string projectId, CancellationToken ct)
    {
        var qaRetryOnFail = false;
        var qaMaxRetries = 1;
        try
        {
            var cfgMap = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            qaRetryOnFail = ReadQaRetryOnFail(cfgMap);
            if (cfgMap.TryGetValue("qa_max_retries", out var qm) && qm.TryGetInt32(out var qmi))
                qaMaxRetries = Math.Clamp(qmi, 0, 5);
        }
        catch { /* keep defaults */ }
        return (qaRetryOnFail, qaMaxRetries);
    }

    private static bool ReadQaRetryOnFail(Dictionary<string, JsonElement> cfgMap)
    {
        if (!cfgMap.TryGetValue("qa_retry_on_fail", out var qe))
            return true; // match Configuration default
        if (qe.ValueKind is JsonValueKind.True) return true;
        if (qe.ValueKind is JsonValueKind.False) return false;
        if (qe.ValueKind == JsonValueKind.String && bool.TryParse(qe.GetString(), out var qb))
            return qb;
        return false;
    }

    private (bool AdminQaRetry, ClipDialogueVerificationService? DialogueQa) ResolveAdminQaRetry(bool qaRetryOnFail)
    {
        var adminQaRetry = qaRetryOnFail && _user.IsAdmin &&
                           _dialogueVerification is not null &&
                           _dialogueVerification.IsConfigured;
        var dialogueQa = adminQaRetry ? _dialogueVerification : null;
        return (adminQaRetry, dialogueQa);
    }

    private async Task LogQaRetryStatusAsync(bool qaRetryOnFail, bool adminQaRetry, int qaMaxRetries)
    {
        if (qaRetryOnFail && !_user.IsAdmin)
            await AppendLogAsync("Quality gate retry is on, but auto-regen runs in admin mode only.");
        else if (adminQaRetry)
            await AppendLogAsync(
                $"Admin quality gate retry ON (max {qaMaxRetries} re-gen(s) per clip on dialogue fail).");
    }

    private async Task<(int Done, int Failed, bool Cancelled)> GenerateSceneClipsWithQaAsync(
        StartSceneGenRequest req,
        string projectId,
        string projectDir,
        JsonElement sceneEl,
        JsonElement blueprintRoot,
        List<(int ClipNum, JsonElement Clip)> todo,
        string resolution,
        bool adminQaRetry,
        int qaMaxRetries,
        ClipDialogueVerificationService? dialogueQa,
        CancellationToken ct)
    {
        var done = 0;
        var failed = 0;
        var lastGeneratedClipNum = 0;
        var carryoverPaddingSec = 0.0;
        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (cn, clip) = todo[i];
            await UpdateAsync(s =>
            {
                s.Index = i + 1;
                s.Clip = cn;
                s.Message = $"Generating S{req.Scene:D2} C{cn} ({i + 1}/{todo.Count})…";
            });
            await AppendLogAsync(Snapshot.Message ?? "");

            try
            {
                carryoverPaddingSec = await GenerateOneSceneClipWithQaAsync(
                    req, projectId, projectDir, sceneEl, blueprintRoot, todo,
                    cn, clip, resolution, lastGeneratedClipNum, carryoverPaddingSec,
                    adminQaRetry, qaMaxRetries, dialogueQa, ct).ConfigureAwait(false);
                lastGeneratedClipNum = cn;
                done++;
                // Fresh clips x/y + status pills while scene gen is still running.
                _projects.InvalidateSceneListCache(projectId);
                await AppendLogAsync($"Done S{req.Scene:D2} C{cn}");
            }
            catch (OperationCanceledException)
            {
                await FinishAsync(StatusCancelled, CancelledByUser);
                return (done, failed, true);
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogError(ex, "Clip S{Scene}C{Clip} failed", req.Scene, cn);
                await AppendLogAsync($"Failed S{req.Scene:D2} C{cn}: {ex.Message}");
                // Full-scene sequential gen: later clips need previous on disk — stop after first fail.
                // Single-clip regen (req.Clip set) keeps trying only that one clip (already filtered).
                if (ShouldStopSceneGenAfterClipFailure(req.Clip, i, todo.Count))
                {
                    await AppendLogAsync(
                        "Stopping scene gen after first clip failure " +
                        $"(remaining {todo.Count - i - 1} clip(s) need previous video).");
                    break;
                }
            }
        }
        return (done, failed, false);
    }

    private static bool ShouldStopSceneGenAfterClipFailure(int? requestedClip, int i, int todoCount) =>
        requestedClip is null or <= 0 && i + 1 < todoCount;

    private async Task<double> GenerateOneSceneClipWithQaAsync(
        StartSceneGenRequest req,
        string projectId,
        string projectDir,
        JsonElement sceneEl,
        JsonElement blueprintRoot,
        List<(int ClipNum, JsonElement Clip)> todo,
        int cn,
        JsonElement clip,
        string resolution,
        int lastGeneratedClipNum,
        double carryoverPaddingSec,
        bool adminQaRetry,
        int qaMaxRetries,
        ClipDialogueVerificationService? dialogueQa,
        CancellationToken ct)
    {
        var prevClipEl = FindPreviousClipForSceneGen(cn, todo, sceneEl);
        var incomingPadding = ResolveIncomingDurationPadding(cn, lastGeneratedClipNum, carryoverPaddingSec);
        carryoverPaddingSec = await GenerateOneClipAsync(
            projectId, projectDir, req.Scene, cn, clip, resolution, ct,
            previousClipEl: prevClipEl,
            blueprintRoot: blueprintRoot,
            incomingDurationPaddingSec: incomingPadding);

        if (adminQaRetry && ClipHasSpokenAudio(clip))
        {
            carryoverPaddingSec = await RunDialogueQaRetryLoopAsync(
                req, projectId, projectDir, cn, clip, resolution, ct,
                prevClipEl, blueprintRoot, incomingPadding, qaMaxRetries, dialogueQa,
                carryoverPaddingSec).ConfigureAwait(false);
        }
        return carryoverPaddingSec;
    }

    private static JsonElement? FindPreviousClipForSceneGen(
        int cn,
        List<(int ClipNum, JsonElement Clip)> todo,
        JsonElement sceneEl)
    {
        if (cn <= 1)
            return null;
        foreach (var (pcn, pclip) in todo)
        {
            if (pcn == cn - 1) return pclip;
        }
        // Also scan full scene clips for prev not in the work list
        return FindClipInScene(sceneEl, cn - 1);
    }

    private enum QaVerifyOutcome
    {
        Stop,
        NeedsRegen,
    }

    private async Task<double> RunDialogueQaRetryLoopAsync(
        StartSceneGenRequest req,
        string projectId,
        string projectDir,
        int cn,
        JsonElement clip,
        string resolution,
        CancellationToken ct,
        JsonElement? prevClipEl,
        JsonElement blueprintRoot,
        double incomingPadding,
        int qaMaxRetries,
        ClipDialogueVerificationService? dialogueQa,
        double carryoverPaddingSec)
    {
        for (var qaAttempt = 1; qaAttempt <= qaMaxRetries; qaAttempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (outcome, ver) = await TryQaVerifyAsync(dialogueQa, projectId, req.Scene, cn, ct)
                .ConfigureAwait(false);
            if (outcome != QaVerifyOutcome.NeedsRegen)
                break;

            // Targeted retry: change what the verifier said was wrong, not just re-roll the dice.
            var correction = ClipCorrectionPlanner.Plan(ver!);
            var issueText = ver!.Issues.Count > 0
                ? " issues=" + string.Join(",", ver.Issues.Select(i => i.Kind + (string.IsNullOrWhiteSpace(i.Word) ? "" : ":" + i.Word)))
                : "";
            await AppendLogAsync(
                $"  [QA] S{req.Scene:D2}C{cn} {ver.Status}{issueText} — auto-regen {qaAttempt}/{qaMaxRetries}" +
                (correction.IsEmpty ? " (plain re-roll)" : $" with correction: {string.Join("; ", correction.Reasons)}") + "…");
            await TryAppendQaRetryLearningEventAsync(projectId, req.Scene, cn, ver, qaAttempt, ct, correction)
                .ConfigureAwait(false);
            carryoverPaddingSec = await GenerateOneClipAsync(
                projectId, projectDir, req.Scene, cn, clip, resolution, ct,
                previousClipEl: prevClipEl,
                blueprintRoot: blueprintRoot,
                incomingDurationPaddingSec: incomingPadding,
                takeKindOverride: VideoTakeKinds.QaAuto,
                correction: correction.IsEmpty ? null : correction);
            if (qaAttempt == qaMaxRetries)
            {
                // Last retry spent: verify the final take once more so the outcome is known and the
                // user is told what is still wrong (not left to discover it in the review).
                var (finalOutcome, finalVer) = await TryQaVerifyAsync(dialogueQa, projectId, req.Scene, cn, ct)
                    .ConfigureAwait(false);
                if (finalOutcome == QaVerifyOutcome.NeedsRegen && finalVer is not null)
                    await EscalateQaFailureAsync(projectId, req.Scene, cn, finalVer, qaMaxRetries, ct).ConfigureAwait(false);
            }
        }
        return carryoverPaddingSec;
    }

    /// <summary>Retries exhausted and the clip still fails: say exactly what is wrong (by tier) in the
    /// job log and record a learning event so the (issue → corrections → outcome) triple is complete.</summary>
    private async Task EscalateQaFailureAsync(
        string projectId, int scene, int cn, ClipDialogueVerificationResult ver, int retries, CancellationToken ct)
    {
        var blocking = ver.Issues.Where(i => DialogueIssueKinds.IsBlocking(i.Kind)).Select(DescribeIssue).Distinct().ToList();
        var degraded = ver.Issues.Where(i => DialogueIssueKinds.IsDegraded(i.Kind)).Select(DescribeIssue).Distinct().ToList();
        var what = blocking.Count > 0 ? "blocking: " + string.Join(", ", blocking)
            : degraded.Count > 0 ? "degraded: " + string.Join(", ", degraded)
            : ver.Status;
        await AppendLogAsync(
            $"  [QA] ⚠ S{scene:D2}C{cn} still failing after {retries} auto-retr{(retries == 1 ? "y" : "ies")} ({what}) — needs your review: " +
            "regenerate with a different take, edit the line, or revoice it.");
        try
        {
            await _learning.AppendAsync(new ReviewLearningEvent
            {
                ProjectId = projectId,
                Type = "qa_escalated",
                Scene = scene,
                Clip = cn,
                Note = ver.Status + (ver.Issues.Count > 0 ? " issues=" + string.Join(",", ver.Issues.Select(i => i.Kind + (string.IsNullOrWhiteSpace(i.Word) ? "" : ":" + i.Word))) : ""),
                Outcome = $"after_{retries}_retries",
                JobId = Snapshot.JobId,
                ActionTaken = "needs_user_review",
            }, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal */ }
    }

    private static string DescribeIssue(DialogueVerificationIssue i) =>
        i.Kind + (string.IsNullOrWhiteSpace(i.Word) ? "" : $" '{i.Word}'");

    private async Task<(QaVerifyOutcome Outcome, ClipDialogueVerificationResult? Ver)> TryQaVerifyAsync(
        ClipDialogueVerificationService? dialogueQa,
        string projectId,
        int scene,
        int cn,
        CancellationToken ct)
    {
        if (dialogueQa is null)
            return (QaVerifyOutcome.Stop, null);
        ClipDialogueVerificationResult? ver;
        try
        {
            ver = await dialogueQa
                .VerifyClipDialogueAsync(projectId, scene, cn, force: true, ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AppendLogAsync(
                $"  [QA] dialogue check failed to run S{scene:D2}C{cn}: {ex.Message}");
            return (QaVerifyOutcome.Stop, null);
        }

        if (ver is null || !DialogueQaNeedsRegen(ver))
        {
            if (ver is not null)
                await AppendLogAsync($"  [QA] S{scene:D2}C{cn} ok ({ver.Status})");
            return (QaVerifyOutcome.Stop, ver);
        }
        return (QaVerifyOutcome.NeedsRegen, ver);
    }

    private async Task TryAppendQaRetryLearningEventAsync(
        string projectId,
        int scene,
        int cn,
        ClipDialogueVerificationResult ver,
        int qaAttempt,
        CancellationToken ct,
        ClipCorrection? correction = null)
    {
        try
        {
            await _learning.AppendAsync(new ReviewLearningEvent
            {
                ProjectId = projectId,
                Type = "qa_auto_retry",
                Scene = scene,
                Clip = cn,
                // What the retry will change — the (issue → correction → outcome) triple is the
                // learning signal; a plain "qa_auto_retry" without it cannot be evaluated later.
                Note = ver.Status
                       + (ver.Issues.Count > 0 ? " issues=" + string.Join(",", ver.Issues.Select(i => i.Kind + (string.IsNullOrWhiteSpace(i.Word) ? "" : ":" + i.Word))) : "")
                       + (correction is { IsEmpty: false } ? " " + correction.Tag() : " plain_reroll"),
                Outcome = $"attempt_{qaAttempt}",
                JobId = Snapshot.JobId,
                ActionTaken = "admin_dialogue_qa_regen",
            }, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal */ }
    }

    private static (string Status, string Message) FormatSceneGenFinish(int done, int failed)
    {
        // partial = some clips ok, some failed (not StatusDone — remux/continue need a clear signal)
        if (failed > 0 && done == 0)
            return (StatusError, $"Scene gen failed ({failed} clip(s) failed, none ok)");
        if (failed > 0)
            return (StatusPartial, $"Scene gen partial ({done} ok, {failed} failed)");
        return (StatusDone, $"Generation finished ({done} clip(s))");
    }

    private async Task TryAppendRegenLearningEventAsync(
        StartSceneGenRequest req,
        string projectId,
        string status,
        string msg,
        CancellationToken ct)
    {
        if (req.Clip is not int regenClip || regenClip <= 0)
            return;
        try
        {
            await _learning.AppendAsync(new ReviewLearningEvent
            {
                ProjectId = projectId,
                Type = "regen_after_review",
                Scene = req.Scene,
                Clip = regenClip,
                Note = msg,
                Outcome = status,
                JobId = Snapshot.JobId,
                ActionTaken = $"gen clip force only_missing={req.OnlyMissing}",
            }, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Before a regen overwrites a previously-rendered clip, copy it (plus its duration sidecar)
    /// into assets/video/_backup/ so a bad regen can be restored by hand. Keeps only the
    /// immediately-previous version — not unbounded history.
    /// </summary>
    private static void BackupExistingClipFile(string outPath, int scene, int clip)
    {
        if (!File.Exists(outPath)) return;
        try
        {
            var videoDir = Path.GetDirectoryName(outPath) ?? ".";
            var backupDir = Path.Combine(videoDir, "_backup");
            Directory.CreateDirectory(backupDir);
            var backupPath = Path.Combine(backupDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");
            File.Copy(outPath, backupPath, overwrite: true);

            var sidecar = outPath + ".duration.json";
            if (File.Exists(sidecar))
                File.Copy(sidecar, backupPath + ".duration.json", overwrite: true);
        }
        catch
        {
            // Best-effort safety net — never block a regen because the backup copy failed.
        }
    }

    /// <summary>
    /// Ceiling on how much extra duration a measured overrun on the previous clip in the same
    /// continuation chain can add to the next clip's request. Bounds a single anomalous measurement
    /// from ballooning every subsequent clip's requested duration.
    /// </summary>
    private const double MaxCarryoverDurationPaddingSec = 2.0;

    /// <summary>
    /// How much of the previous clip's measured duration overrun should carry forward as padding for
    /// <paramref name="clipNum"/>. Only non-zero when <paramref name="lastGeneratedClipNum"/> is truly
    /// the immediately preceding clip number (no gap) — a gap (e.g. only-missing regen skipped one)
    /// means there's no real adjacency to reconcile against, so start fresh at zero.
    /// </summary>
    public static double ResolveIncomingDurationPadding(
        int clipNum, int lastGeneratedClipNum, double lastOverrunSec) =>
        clipNum == lastGeneratedClipNum + 1 ? lastOverrunSec : 0.0;

    /// <summary>
    /// Seconds a just-finished clip's real measured duration overran what was requested, clamped to
    /// <see cref="MaxCarryoverDurationPaddingSec"/>. Zero for non-continuation models (Constraint 3 —
    /// only continuation chains get the free same-scene reconciliation) or when the clip ran at/under
    /// its requested duration (never carry forward a negative "padding").
    /// </summary>
    public static double ComputeCarryoverOverrunSec(
        bool supportsContinue, double probedDurationSec, int requestedDurationSec) =>
        supportsContinue
            ? Math.Clamp(probedDurationSec - requestedDurationSec, 0.0, MaxCarryoverDurationPaddingSec)
            : 0.0;

    /// <summary>
    /// Applies carried-forward padding to a clip's requested duration, never exceeding the resolved
    /// model's absolute ceiling (<paramref name="absMaxSeconds"/>).
    /// </summary>
    public static int ApplyIncomingDurationPadding(
        int durationSeconds, double incomingDurationPaddingSec, int absMaxSeconds) =>
        incomingDurationPaddingSec > 0
            ? Math.Min(absMaxSeconds, durationSeconds + (int)Math.Ceiling(incomingDurationPaddingSec))
            : durationSeconds;

    /// <summary>
    /// Generates one clip. Returns the seconds this clip's actual measured duration overran its
    /// requested duration (0 when not applicable) — for continuation-chain models, the caller feeds
    /// this back in as <paramref name="incomingDurationPaddingSec"/> for the next clip in the same
    /// scene, since clip N+1 already can't start before clip N is on disk (free reconciliation,
    /// no added wall-clock cost). Never used to retroactively correct this clip itself — duration is
    /// billed/quantized per provider, so padding the next request is cheaper than any fix-up here.
    /// </summary>
    private async Task<double> GenerateOneClipAsync(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        JsonElement clipEl,
        string resolution,
        CancellationToken ct,
        JsonElement? previousClipEl = null,
        JsonElement? blueprintRoot = null,
        double incomingDurationPaddingSec = 0.0,
        string? modelOverride = null,
        string? takeKindOverride = null,
        ClipCorrection? correction = null)
    {
        var ctx = await CreateClipGenContextAsync(
            projectId, projectDir, scene, clip, clipEl, resolution, ct,
            previousClipEl, blueprintRoot, incomingDurationPaddingSec,
            modelOverride, takeKindOverride, correction).ConfigureAwait(false);
        try
        {
            return await ExecuteClipGenerationAsync(ctx).ConfigureAwait(false);
        }
        finally
        {
            // Single-use: consumed extend-source is deleted so a later plain regenerate (no fresh
            // upload) falls back to fresh gen instead of silently reusing stale continuity data.
            TryDeleteExtendInputTemp(ctx.CreatedTempTrimPath);
        }
    }

    private sealed class ClipGenContext
    {
        public required string ProjectId { get; init; }
        public required string ProjectDir { get; init; }
        public int Scene { get; init; }
        public int Clip { get; init; }
        public JsonElement ClipEl { get; init; }
        public string Resolution { get; set; } = "";
        public CancellationToken Ct { get; init; }
        public JsonElement? PreviousClipEl { get; init; }
        public JsonElement? BlueprintRoot { get; init; }
        /// <summary>Targeted change for a QA retry (speaker lock / respellings); null on a normal take.</summary>
        public ClipCorrection? Correction { get; init; }

        public double IncomingDurationPaddingSec { get; init; }
        public string? TakeKindOverride { get; init; }
        public required Dictionary<string, ClipVideoPromptBuilder.CharacterProfile> Profiles { get; init; }
        public required string VideoDir { get; init; }
        public bool HadVideoBefore { get; init; }
        public required string Model { get; init; }
        public required SupportedModelEntry ModelEntry { get; init; }
        public string? PrevVisual { get; set; }
        public string? PrevVideoPath { get; set; }
        public string? ExtendSourceFileId { get; set; }
        public bool ReseedFresh { get; set; }
        public string? ExtendSourcePath { get; init; }
        public string? CreatedTempTrimPath { get; init; }
        public double? ExtendInputDurationSec { get; set; }
    }

    private async Task<ClipGenContext> CreateClipGenContextAsync(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        JsonElement clipEl,
        string resolution,
        CancellationToken ct,
        JsonElement? previousClipEl,
        JsonElement? blueprintRoot,
        double incomingDurationPaddingSec,
        string? modelOverride,
        string? takeKindOverride,
        ClipCorrection? correction = null)
    {
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);
        var videoDir = Path.Combine(projectDir, AssetsFolder, VideoFolder);

        // H1/H2: whether this scene+clip already has media (regen vs first take).
        var existingClipPath = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        var hadVideoBefore = ClipPresentOnServerOrClient(existingClipPath);

        // Previous clip in this scene — Imagine /videos/extensions continues from that video.
        // Cast-set changes reseed fresh+refs (PR2).
        var wantContinue = WantsVideoContinue(clipEl, clip);

        var model = await ResolveVideoModelAsync(projectId, ct, modelOverride).ConfigureAwait(false);
        var modelEntry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video);

        var (extendSourcePath, extendSourceFileId, tempTrimPath, extendInputDur) = wantContinue
            ? await ResolveExtendInputAsync(projectDir, scene, clip, modelEntry, ct).ConfigureAwait(false)
            : (null, null, null, null);

        var prevVisual = ResolvePreviousClipVisual(previousClipEl, wantContinue, blueprintRoot, scene, clip);

        return new ClipGenContext
        {
            ProjectId = projectId,
            ProjectDir = projectDir,
            Scene = scene,
            Clip = clip,
            ClipEl = clipEl,
            Resolution = resolution,
            Ct = ct,
            PreviousClipEl = previousClipEl,
            BlueprintRoot = blueprintRoot,
            Correction = correction,

            IncomingDurationPaddingSec = incomingDurationPaddingSec,
            TakeKindOverride = takeKindOverride,
            Profiles = profiles,
            VideoDir = videoDir,
            HadVideoBefore = hadVideoBefore,
            Model = model,
            ModelEntry = modelEntry,
            PrevVisual = prevVisual,
            PrevVideoPath = extendSourcePath,
            ExtendSourceFileId = extendSourceFileId,
            ExtendSourcePath = extendSourcePath,
            CreatedTempTrimPath = tempTrimPath,
            ExtendInputDurationSec = extendInputDur,
        };
    }

    private static bool WantsVideoContinue(JsonElement clipEl, int clip)
    {
        var cont = clipEl.TryGetProperty("veo_continuation_source", out var ce)
            ? (ce.GetString() ?? "none")
            : "none";
        return string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase) ||
               clip > 1;
    }

    private readonly record struct PredecessorClipDetails(
        string? LocalMp4Path,
        string? SourceFileId,
        string? SourceUrl,
        double? DurationSeconds,
        bool ProviderCopyIsCombined = false);

    private static PredecessorClipDetails? TryReadPredecessorClipDetails(string projectDir, int scene, int clip)
    {
        try
        {
            var videoDir = Path.Combine(projectDir, AssetsFolder, VideoFolder);
            if (!Directory.Exists(videoDir)) return null;

            var pattern = $"scene_{scene:D2}_clip_{clip:D2}*.clip.json";
            var sidecarFile = new DirectoryInfo(videoDir)
                .EnumerateFiles(pattern)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            string? sourceFileId = null;
            string? sourceUrl = null;
            double? durationSeconds = null;
            var providerCombined = false;

            if (sidecarFile is not null && File.Exists(sidecarFile.FullName))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sidecarFile.FullName));
                var root = doc.RootElement;
                if (root.TryGetProperty("source_file_id", out var sfid))
                    sourceFileId = sfid.GetString();
                if (root.TryGetProperty("source_url", out var surl))
                    sourceUrl = surl.GetString();
                if (root.TryGetProperty("duration_seconds", out var ds) && ds.TryGetDouble(out var dur))
                    durationSeconds = dur;
                if (root.TryGetProperty(ClipProviderSource.LeadInProperty, out var li) && li.TryGetDouble(out var lead) && lead > 0.1)
                    providerCombined = true;
            }

            var mp4Pattern = $"scene_{scene:D2}_clip_{clip:D2}*.mp4";
            var mp4File = new DirectoryInfo(videoDir)
                .EnumerateFiles(mp4Pattern)
                .Where(fi => fi.Length >= 1024 && !fi.Name.StartsWith('_'))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            return new PredecessorClipDetails(
                LocalMp4Path: mp4File?.FullName,
                SourceFileId: sourceFileId,
                SourceUrl: sourceUrl,
                DurationSeconds: durationSeconds,
                ProviderCopyIsCombined: providerCombined);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse an extend-source marker: (file_id, duration_seconds); (null, null) when malformed.</summary>
    internal static (string? FileId, double? Seconds) TryReadExtendSourceMarker(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fid = root.TryGetProperty("file_id", out var f) ? f.GetString() : null;
            double? sec = root.TryGetProperty("duration_seconds", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
            return (string.IsNullOrWhiteSpace(fid) ? null : fid, sec);
        }
        catch { return (null, null); }
    }

    /// <summary>Marker written by the clip upload endpoint when a browser-trimmed extend source was relayed to xAI Files.</summary>
    public static string ExtendSourceMarkerName(int scene, int clip) => $"_extend_src_s{scene:D2}c{clip:D2}.json";

    private async Task<(string? ExtendSourcePath, string? ExtendSourceFileId, string? CreatedTempTrimPath, double? PredecessorDurationSec)>
        ResolveExtendInputAsync(
            string projectDir,
            int scene,
            int clip,
            SupportedModelEntry modelEntry,
            CancellationToken ct)
    {
        if (clip <= 1 || !modelEntry.SupportsVideoContinue)
            return (null, null, null, null);

        // 0. Browser-trimmed delta relayed to xAI Files: marker holds the file_id (+ its duration,
        //    which is exactly the lead-in the extension result carries and the client trims).
        var markerPath = Path.Combine(projectDir, AssetsFolder, VideoFolder, ExtendSourceMarkerName(scene, clip));
        if (File.Exists(markerPath))
        {
            var (fid, sec) = TryReadExtendSourceMarker(await File.ReadAllTextAsync(markerPath, ct).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(fid))
                return (null, fid, null, sec);
            _log.LogWarning("Unreadable extend-source marker {Path}; falling back", markerPath);
        }

        // 1. Check if client already uploaded an explicit _extend_src_ file (fakes / legacy path)
        var explicitSrc = Path.Combine(
            projectDir, AssetsFolder, VideoFolder, $"_extend_src_s{scene:D2}c{clip:D2}.mp4");
        if (File.Exists(explicitSrc) && new FileInfo(explicitSrc).Length >= 1024)
        {
            var explicitDur = await Mp4DurationReader.TryReadSecondsAsync(explicitSrc, ct).ConfigureAwait(false);
            return (explicitSrc, null, null, explicitDur);
        }

        // 2. Inspect predecessor clip take/sidecar
        var maxInputSeconds = modelEntry.MaxEditInputDurationSeconds ?? 8.7;
        var prevClipInfo = TryReadPredecessorClipDetails(projectDir, scene, clip - 1);
        if (prevClipInfo is null)
            return (null, null, null, null);

        var prevDur = prevClipInfo.Value.DurationSeconds ?? 5.0;

        // Case 1: Predecessor local standalone MP4 exists and duration <= maxInputSeconds.
        // We prefer the local standalone file because predecessor's remote source_file_id may contain
        // un-trimmed accumulated predecessor footage from earlier extends (e.g. [C01 + C02]).
        if (!string.IsNullOrWhiteSpace(prevClipInfo.Value.LocalMp4Path) && File.Exists(prevClipInfo.Value.LocalMp4Path) && prevDur <= maxInputSeconds + 0.1)
        {
            return (prevClipInfo.Value.LocalMp4Path, null, null, prevDur);
        }

        // Case 2: Predecessor has valid source_file_id and duration <= maxInputSeconds (e.g. Clip 1 fresh anchor).
        // Not when the predecessor was itself an extend: its provider copy is the COMBINED video
        // (lead-in recorded in the sidecar) — extending from it would carry the clip before it along.
        if (!string.IsNullOrWhiteSpace(prevClipInfo.Value.SourceFileId) && prevDur <= maxInputSeconds + 0.1 && !prevClipInfo.Value.ProviderCopyIsCombined)
        {
            return (null, prevClipInfo.Value.SourceFileId, null, prevDur);
        }

        // Case 3 / 4: Duration > maxInputSeconds -> tail trimming required
        return await TrimPredecessorForExtendAsync(projectDir, scene, clip, prevClipInfo.Value, maxInputSeconds, ct).ConfigureAwait(false);
    }

    private async Task<(string? ExtendSourcePath, string? ExtendSourceFileId, string? CreatedTempTrimPath, double? PredecessorDurationSec)>
        TrimPredecessorForExtendAsync(
            string projectDir,
            int scene,
            int clip,
            PredecessorClipDetails prevClipInfo,
            double maxInputSeconds,
            CancellationToken ct)
    {
        var sourceVideoToTrim = prevClipInfo.LocalMp4Path;
        string? downloadedTempFile = null;
        if ((string.IsNullOrWhiteSpace(sourceVideoToTrim) || !File.Exists(sourceVideoToTrim)) &&
            !string.IsNullOrWhiteSpace(prevClipInfo.SourceUrl))
        {
            downloadedTempFile = Path.Combine(Path.GetTempPath(), $"ptm_extend_pred_{Guid.NewGuid():N}.mp4");
            try
            {
                await _grok.DownloadToFileAsync(prevClipInfo.SourceUrl, downloadedTempFile, ct).ConfigureAwait(false);
                sourceVideoToTrim = downloadedTempFile;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to download predecessor video from source_url for tail trimming: {Url}", prevClipInfo.SourceUrl);
                TryDeleteExtendInputTemp(downloadedTempFile);
                return (null, null, null, null);
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceVideoToTrim) && File.Exists(sourceVideoToTrim))
        {
            var trimmedDest = Path.Combine(projectDir, AssetsFolder, VideoFolder, $"_extend_src_s{scene:D2}c{clip:D2}.mp4");
            var keepSeconds = Math.Max(1.0, maxInputSeconds - 0.2);
            if (NativeFfmpeg.TryTrimTail(sourceVideoToTrim, trimmedDest, keepSeconds))
            {
                TryDeleteExtendInputTemp(downloadedTempFile);
                return (trimmedDest, null, trimmedDest, keepSeconds);
            }
            TryDeleteExtendInputTemp(downloadedTempFile);
        }

        return (null, null, null, null);
    }

    private static string? ResolvePreviousClipVisual(
        JsonElement? previousClipEl,
        bool wantContinue,
        JsonElement? blueprintRoot,
        int scene,
        int clip)
    {
        if (previousClipEl is { } prevEl &&
            prevEl.TryGetProperty("visual_prompt", out var pvp))
            return pvp.GetString();
        if (wantContinue && blueprintRoot is { } root)
            return FindClipVisualInBlueprint(root, scene, clip - 1);
        return null;
    }

    private static void TryDeleteExtendInputTemp(string? path)
    {
        if (path is null)
            return;
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private async Task<double> ExecuteClipGenerationAsync(ClipGenContext ctx)
    {
        // PR2: reseed with locked refs when on-screen cast set changes (API drops refs on extend).
        await ApplyIdentityReseedIfNeededAsync(ctx).ConfigureAwait(false);

        // Silent → first spoken/VO: video-extend often clips the opening word (mouth stays closed
        // from the prior silent clip). Require prev on disk for order, but gen fresh + plates.
        await ApplyFirstSpokenAfterSilenceReseedAsync(ctx).ConfigureAwait(false);
        await LogContinuityOrReseedAsync(ctx).ConfigureAwait(false);

        var styleHead = await TryGetStyleLockHeadAsync(ctx.ProjectId, ctx.Ct).ConfigureAwait(false);
        var sceneLocationKey = ResolveSceneLocationKey(ctx.BlueprintRoot, ctx.Scene);

        var built = ClipVideoPromptBuilder.Build(
            ctx.ClipEl,
            ctx.ProjectDir,
            characters: ctx.Profiles,
            previousClipVisualPrompt: ctx.PrevVisual,
            previousClipVideoPath: ctx.PrevVideoPath,
            startFrameImagePath: null,
            maxRefs: ctx.ModelEntry.MaxReferenceImages
                ?? throw new InvalidOperationException(
                    $"Video model '{ctx.ModelEntry.Id}' has no maxReferenceImages in models_catalog.json."),
            styleHead: styleHead,
            videoModel: ctx.Model,
            fallbackLocationKey: sceneLocationKey,
            previousClipExtendFileId: ctx.ExtendSourceFileId,
            correction: ctx.Correction);

        if (string.IsNullOrWhiteSpace(built.Prompt))
            throw new InvalidOperationException("clip missing visual_prompt");

        EnsureClipRefsForMode(ctx, built);
        built = await ApplyProjectRulesToPromptAsync(ctx.ProjectId, built, ctx.Ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(ctx.Resolution))
            ctx.Resolution = await ResolveVideoResolutionAsync(ctx.ProjectId, null, ctx.Ct);

        built = await ApplyPromptBudgetAsync(built, ctx.ModelEntry).ConfigureAwait(false);
        await WriteAndLogPromptAsync(ctx.ProjectId, ctx.ProjectDir, ctx.Scene, ctx.Clip, built, ctx.Ct)
            .ConfigureAwait(false);
        await LogPromptRefsAsync(built, ctx.PrevVideoPath, ctx.ExtendSourceFileId).ConfigureAwait(false);

        var supportsContinue = SupportedModelCatalog.ResolveOrDefault(ctx.Model, ModelCapability.Video).SupportsVideoContinue;
        var duration = await ResolveClipDurationAsync(ctx, built, supportsContinue).ConfigureAwait(false);

        var modeLabel = (ctx.PrevVideoPath is not null || ctx.ExtendSourceFileId is not null) ? "video-extend" : built.Mode;
        await AppendLogAsync(
            $"  [Grok] Submit S{ctx.Scene:D2}C{ctx.Clip} duration={duration}s res={ctx.Resolution} " +
            $"model={ctx.Model} mode={modeLabel} {built.PromptLogSummary}");

        var targetAspectRatio = await ResolveTargetAspectRatioAsync(ctx.ProjectId, ctx.BlueprintRoot, ctx.Ct).ConfigureAwait(false);

        var requestId = await _grok.SubmitGenerationAsync(
            built.Prompt,
            duration,
            ctx.Resolution,
            ctx.Model,
            ctx.Ct,
            referenceImagePaths: ClipReferenceImagesForSubmit(ctx.PrevVideoPath, ctx.ExtendSourceFileId, built),
            startFrameImagePath: null,
            continueFromVideoPath: ctx.PrevVideoPath,
            aspectRatio: targetAspectRatio,
            extendSourceFileId: ctx.ExtendSourceFileId);
        await AppendLogAsync($"  [Grok] request_id={requestId}");

        var url = await _grok.PollForVideoUrlAsync(
            requestId,
            msg => { _ = AppendLogAsync($"  [Grok] {msg}"); },
            ctx.Ct);

        var mp4Path = Path.Combine(ctx.VideoDir, $"scene_{ctx.Scene:D2}_clip_{ctx.Clip:D2}.mp4");
        var (overrunSec, serverTrimmed) = await DownloadClipAndRecordTelemetryAsync(
            ctx, built, url, mp4Path, duration, supportsContinue).ConfigureAwait(false);

        // Video-extend: the provider URL is the combined video. Hand the browser the server-trimmed
        // standalone copy when we have it (no client-side slice needed); otherwise the provider URL
        // plus the lead-in length so the browser slices it. The sidecar records the lead-in either way.
        var leadIn = ctx.ExtendInputDurationSec is { } pd && pd > 0.1 && (ctx.PrevVideoPath is not null || ctx.ExtendSourceFileId is not null) ? pd : (double?)null;
        await PublishClipClientMediaAsync(ctx, url, serverTrimmed ? mp4Path : null).ConfigureAwait(false);
        await WriteClipSidecarIfConfiguredAsync(ctx, built, url, requestId, duration, leadIn).ConfigureAwait(false);
        await RecordClipCostAsync(ctx, built, requestId, duration).ConfigureAwait(false);
        return overrunSec;
    }

    internal static IReadOnlyList<string>? ClipReferenceImagesForSubmit(
        string? prevVideoPath, string? extendSourceFileId, ClipVideoPromptBuilder.PromptBuildResult built) =>
        prevVideoPath is null && extendSourceFileId is null && built.ReferenceImagePaths.Count > 0
            ? built.ReferenceImagePaths
            : null;

    private static bool IsVisualOnScreenKey(
        string k, Dictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles) =>
        !(profiles.TryGetValue(k, out var cp) && cp.VoiceOnly);

    private async Task ApplyIdentityReseedIfNeededAsync(ClipGenContext ctx)
    {
        if ((ctx.PrevVideoPath is null && ctx.ExtendSourceFileId is null) || !_opts.IdentityReseedOnCastChange)
            return;
        var curKeys = ClipVideoPromptBuilder.ResolveOnScreenCharacterKeys(ctx.ClipEl)
            .Where(k => IsVisualOnScreenKey(k, ctx.Profiles))
            .Select(k => k)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var prevKeys = ctx.PreviousClipEl is { } pe
            ? ClipVideoPromptBuilder.ResolveOnScreenCharacterKeys(pe)
                .Where(k => IsVisualOnScreenKey(k, ctx.Profiles))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
        if (prevKeys.Count == 0 || OnScreenSetsEqual(curKeys, prevKeys))
            return;
        ctx.ReseedFresh = true;
        await AppendLogAsync(
            $"  [Identity] Cast set changed " +
            $"[{string.Join(", ", prevKeys)}] → [{string.Join(", ", curKeys)}] — " +
            "fresh gen with locked refs (not video-extend)");
        ctx.PrevVideoPath = null; // API: attach refs
        ctx.ExtendSourceFileId = null;
        ctx.ExtendInputDurationSec = null;
        // Keep prevVisual for continuity prose only
    }

    private async Task ApplyFirstSpokenAfterSilenceReseedAsync(ClipGenContext ctx)
    {
        if (ctx.PrevVideoPath is null && ctx.ExtendSourceFileId is null)
            return;
        JsonElement? prevMeta = ctx.PreviousClipEl;
        if (prevMeta is null && ctx.BlueprintRoot is { } br)
            prevMeta = FindClipElementInBlueprint(br, ctx.Scene, ctx.Clip - 1);
        if (prevMeta is not { } pm || !ClipHasSpokenAudio(ctx.ClipEl) || ClipHasSpokenAudio(pm))
            return;
        ctx.ReseedFresh = true;
        ctx.PrevVideoPath = null;
        ctx.ExtendSourceFileId = null;
        ctx.ExtendInputDurationSec = null;
        await AppendLogAsync(
            $"  [Speech] S{ctx.Scene:D2}C{ctx.Clip:D2} is first spoken after silence — " +
            "fresh gen with locked refs (not video-extend) so the opening word is not clipped");
    }

    private async Task LogContinuityOrReseedAsync(ClipGenContext ctx)
    {
        if (ctx.PrevVideoPath is not null || ctx.ExtendSourceFileId is not null)
        {
            var src = ctx.PrevVideoPath is not null ? Path.GetFileName(ctx.PrevVideoPath) : ctx.ExtendSourceFileId;
            await AppendLogAsync(
                $"  [Continuity] Imagine video-extend from S{ctx.Scene:D2}C{ctx.Clip - 1:D2} " +
                $"({src})");
            return;
        }
        if (ctx.ReseedFresh && ctx.ExtendSourcePath is not null)
        {
            await AppendLogAsync(
                $"  [Identity] Reseed S{ctx.Scene:D2}C{ctx.Clip:D2} after S{ctx.Scene:D2}C{ctx.Clip - 1:D2} " +
                "(locked character refs attached)");
        }
    }

    private async Task<string?> TryGetStyleLockHeadAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var rules = await _projectRules.GetActiveRulesBlockAsync(projectId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rules))
                return null;
            var m = CommonRegex.Match(
                rules, @"STYLE LOCK:\s*([^\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
                return "STYLE LOCK: " + m.Groups[1].Value.Trim().TrimEnd('.', ' ');
        }
        catch { /* non-fatal */ }
        return null;
    }

    private static string? ResolveSceneLocationKey(JsonElement? blueprintRoot, int scene)
    {
        if (blueprintRoot is not { } sceneRoot)
            return null;
        var sceneEl = FindScene(sceneRoot, scene);
        if (sceneEl is not { } se)
            return null;
        if (se.TryGetProperty("primary_location_id", out var pl) &&
            pl.ValueKind == JsonValueKind.String &&
            pl.GetString() is { Length: > 0 } pls)
            return pls;
        return FirstLocationId(se);
    }

    private static string? FirstLocationId(JsonElement se)
    {
        if (!se.TryGetProperty("location_ids", out var lids) ||
            lids.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var x in lids.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String &&
                x.GetString() is { Length: > 0 } first)
                return first;
        }
        return null;
    }

    private void EnsureClipRefsForMode(ClipGenContext ctx, ClipVideoPromptBuilder.PromptBuildResult built)
    {
        // Fresh / reseed: every on-screen cast key must have a locked ref attached
        if (ctx.PrevVideoPath is null && ctx.ExtendSourceFileId is null)
            EnsureFreshGenHasLockedRefs(ctx.ProjectId, ctx.ProjectDir, built, ctx.Profiles, ctx.ClipEl, ctx.BlueprintRoot);
        else
        {
            // Extend still requires locks on disk even when API cannot attach them
            EnsureOnScreenLocksExist(ctx.ProjectId, ctx.ProjectDir, built, ctx.Profiles, ctx.ClipEl, ctx.BlueprintRoot);
        }
    }

    private async Task<ClipVideoPromptBuilder.PromptBuildResult> ApplyProjectRulesToPromptAsync(
        string projectId,
        ClipVideoPromptBuilder.PromptBuildResult built,
        CancellationToken ct)
    {
        // Approved project-scoped house rules (learning). Global clip gen rules live in
        // embedded prompts/clip_gen_rules.txt and are composed inside ClipVideoPromptBuilder.
        try
        {
            var rules = await _projectRules.GetActiveRulesBlockAsync(projectId, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(rules))
            {
                built = built.WithPrompt(
                    built.Prompt.TrimEnd() + "\n\n" + rules.Trim(),
                    " · project-rules");
            }
        }
        catch { /* non-fatal */ }
        return built;
    }

    private async Task<ClipVideoPromptBuilder.PromptBuildResult> ApplyPromptBudgetAsync(
        ClipVideoPromptBuilder.PromptBuildResult built,
        SupportedModelEntry modelEntry)
    {
        var modelMaxPromptLen = modelEntry.MaxPromptLength
            ?? throw new InvalidOperationException(
                $"Video model '{modelEntry.Id}' has no maxPromptLength in models_catalog.json.");

        // Pre-budget to model-specific prompt limit (e.g. 1000 for Fal.ai, 4096 for Grok).
        // Avoids a guaranteed first-attempt 400 on every clip.
        var preLen = built.Prompt.Length;
        var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(built.Prompt, modelMaxPromptLen);
        if (fitted.Length < preLen)
        {
            built = built.WithPrompt(fitted, $" · pre-budget {preLen}→{fitted.Length}");
            await AppendLogAsync(
                $"  [Prompt] pre-budget {preLen}→{fitted.Length} chars (model {modelEntry.Id} hard cap {modelMaxPromptLen})");
        }
        return built;
    }

    private static string FormatRefLocationSuffix(ClipVideoPromptBuilder.PromptBuildResult built)
    {
        if (built.LocationRefAttached)
            return $" · set={built.LocationKey} {built.LocationImageTag}";
        if (built.LocationKey is { Length: > 0 })
            return $" · set={built.LocationKey} (no locked plate)";
        return "";
    }

    private async Task LogPromptRefsAsync(
        ClipVideoPromptBuilder.PromptBuildResult built, string? prevVideoPath, string? extendSourceFileId)
    {
        if (built.Prompt.Contains("<VoiceLock>", StringComparison.OrdinalIgnoreCase))
            await AppendLogAsync("  [Voice] VOICE LOCK from character profile");
        if (built.ReferenceImagePaths.Count > 0)
        {
            await AppendLogAsync(
                $"  [Refs] attached={built.RefsAttachedToApi} count={built.ReferenceImagePaths.Count}: " +
                string.Join(", ", built.ReferenceImagePaths.Select(Path.GetFileName)) +
                FormatRefLocationSuffix(built));
            return;
        }
        if (prevVideoPath is not null || extendSourceFileId is not null)
            await AppendLogAsync("  [Refs] video-extend — locked plates not attached to API (IDENTITY text only)");
        else if (built.LocationKey is { Length: > 0 })
            await AppendLogAsync($"  [Refs] no plates attached · set={built.LocationKey} (no locked plate or no slots)");
    }

    private async Task<int> ResolveClipDurationAsync(
        ClipGenContext ctx,
        ClipVideoPromptBuilder.PromptBuildResult built,
        bool supportsContinue)
    {
        // Only continuation-chain models get carried-forward padding: clip N+1 already can't
        // start before clip N is on disk for these, so reconciling against N's real measurement
        // costs nothing extra. Non-continuation models don't have that same-scene coupling.
        // Dialogue-aware duration (tight for short lines — billed per second), clamped to the
        // actually-selected model's own duration caps (SupportedModelCatalog) instead of a
        // hardcoded provider assumption.
        var (durMin, durMax, durAbsMax) = ClipDurationEstimator.ResolveBoundsForModel(ctx.Model);
        var duration = ClipDurationEstimator.EstimateForClip(ctx.ClipEl, durMin, durMax, durAbsMax);
        if (supportsContinue && ctx.IncomingDurationPaddingSec > 0)
        {
            var padded = ApplyIncomingDurationPadding(duration, ctx.IncomingDurationPaddingSec, durAbsMax);
            await AppendLogAsync(
                $"  [Duration] +{ctx.IncomingDurationPaddingSec:F1}s carried from previous clip's overrun -> {duration}s to {padded}s");
            duration = padded;
        }
        await AppendLogAsync($"  [Duration] estimated {duration}s (dialogue-aware, max {durMax}s, model={ctx.Model})");
        if (ctx.Correction is { ExtraDurationSec: > 0 } corr)
        {
            // QA retry after a cut-off line: the previous take was too short for the words.
            var longer = Math.Min(durAbsMax, duration + corr.ExtraDurationSec);
            await AppendLogAsync($"  [Duration] +{corr.ExtraDurationSec}s (QA: line was cut off) -> {duration}s to {longer}s");
            duration = longer;
        }
        // Reference-conditioned / continuation generation is bounded by the model's own
        // tighter extension cap (catalog MaxExtensionSeconds), not a bare hardcoded 10 — keeps
        // this correct if a future model's real ref-conditioned max differs from Grok's ~10s.
        if (ctx.PrevVideoPath is not null || ctx.ExtendSourceFileId is not null || built.ReferenceImagePaths.Count > 0)
            duration = ClipDurationEstimator.ResolveActualDurationForModel(ctx.Model, duration, isExtensionMode: true);
        return duration;
    }

    private async Task PublishClipClientMediaAsync(ClipGenContext ctx, string url, string? serverTrimmedPath)
    {
        var relPath = MediaRegistryService.ClipRelativePath(ctx.Scene, ctx.Clip);
        // Trimmed extend clip: serve the standalone server file (local: ticket) and tell the browser
        // there is nothing left to slice. Otherwise: provider URL + lead-in for the browser slice.
        var ticket = _mediaProxy.Issue(serverTrimmedPath is not null ? "local:" + serverTrimmedPath : url, TimeSpan.FromMinutes(45));
        var clientUrl = $"/api/media/proxy/{ticket}";
        await UpdateAsync(s =>
        {
            s.ClientMediaUrl = clientUrl;
            s.ClientRelativePath = relPath;
            s.Scene = ctx.Scene;
            s.Clip = ctx.Clip;
            s.PredecessorDurationSec = serverTrimmedPath is not null ? null : ctx.ExtendInputDurationSec;
        });
        await AppendLogAsync(
            $"  [Grok] video ready for client save → {relPath} (server copy is transient; provider-hosted)");
    }

    /// <returns>Overrun seconds and whether the server copy was lead-in-trimmed. A trimmed copy is the
    /// only standalone version of a video-extend clip (the provider holds the combined video), so it
    /// stays until the browser has saved it (register deletes it) — that is the "while necessary".</returns>
    private async Task<(double OverrunSec, bool ServerTrimmed)> DownloadClipAndRecordTelemetryAsync(
        ClipGenContext ctx,
        ClipVideoPromptBuilder.PromptBuildResult built,
        string url,
        string mp4Path,
        int duration,
        bool supportsContinue)
    {
        var overrunSec = 0.0;
        var serverTrimmed = false;
        // Save MP4 file to server project directory so client media sync delivers MP4 files to client folder.
        // Via IVideoClient.DownloadToFileAsync, not a raw HttpClient GET. Fake providers may return a
        // non-http URL (a local fixture scheme) that DownloadToFileAsync resolves on disk. A bare
        // GetByteArrayAsync would skip the save on that scheme.
        try
        {
            await _grok.DownloadToFileAsync(url, mp4Path, ctx.Ct).ConfigureAwait(false);
            var bytesLength = File.Exists(mp4Path) ? new FileInfo(mp4Path).Length : 0;
            if (bytesLength > 0)
            {
                await AppendLogAsync($"  [Media] Saved {bytesLength} bytes to {Path.GetFileName(mp4Path)}");

                if (ctx.ExtendInputDurationSec is { } predDur && predDur > 0.1 && (ctx.PrevVideoPath is not null || ctx.ExtendSourceFileId is not null))
                {
                    serverTrimmed = await TryTrimPredecessorFromDownloadedClipAsync(ctx, mp4Path, predDur).ConfigureAwait(false);
                }

                overrunSec = await RecordDownloadedClipTelemetryAsync(
                    ctx, built, mp4Path, duration, supportsContinue).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save MP4 bytes to server project directory for S{Scene:D2}C{Clip:D2}", ctx.Scene, ctx.Clip);
        }
        finally
        {
            // Media does not live on the server: the copy above existed only for the duration probe,
            // telemetry, lead-in trim and dialogue verification. The user's folder (client save via the
            // proxy URL) and the provider (source_url / file_id in the sidecar) are the durable homes.
            // Kept only under fakes, whose provider "URLs" are local fixture paths with no durable host,
            // and for a lead-in-trimmed extend clip: the provider copy is the combined video, so this
            // trimmed file is the only correct copy until the browser saves it (register deletes it).
            if (serverTrimmed)
                await AppendLogAsync($"  [Media] keeping trimmed {Path.GetFileName(mp4Path)} on the server until your browser saves it (provider copy is the combined extend video)");
            else
                await DeleteTransientServerClipAsync(ctx, mp4Path).ConfigureAwait(false);
        }
        return (overrunSec, serverTrimmed);
    }

    private async Task DeleteTransientServerClipAsync(ClipGenContext ctx, string mp4Path)
    {
        try
        {
            if (_opts.UseFakes) return;
            if (!File.Exists(mp4Path)) return;
            File.Delete(mp4Path);
            await AppendLogAsync($"  [Media] server copy of {Path.GetFileName(mp4Path)} released (provider-hosted; saved to your folder via the browser)");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not release transient server clip {Path}", mp4Path);
        }
    }

    /// <summary>True when the lead-in was cut off the server copy (it is then the only standalone copy of this clip).</summary>
    private async Task<bool> TryTrimPredecessorFromDownloadedClipAsync(ClipGenContext ctx, string mp4Path, double predDur)
    {
        try
        {
            var totalDur = await Mp4DurationReader.TryReadSecondsAsync(mp4Path, ctx.Ct).ConfigureAwait(false);
            if (totalDur is not { } td || td <= predDur + 0.2)
                return false;

            var dir = Path.GetDirectoryName(mp4Path) ?? "";
            var tempOut = Path.Combine(dir, $"_trim_{Path.GetFileName(mp4Path)}");
            if (NativeFfmpeg.TryTrimHead(mp4Path, tempOut, predDur))
            {
                File.Move(tempOut, mp4Path, overwrite: true);
                var newBytes = new FileInfo(mp4Path).Length;
                await AppendLogAsync($"  [Extend] Trimmed {predDur:F2}s predecessor lead-in → {td - predDur:F2}s standalone delta ({newBytes} bytes)");
                return true;
            }
            else
            {
                if (File.Exists(tempOut))
                {
                    try { File.Delete(tempOut); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not trim predecessor lead-in from extended clip S{Scene:D2}C{Clip:D2}", ctx.Scene, ctx.Clip);
        }
        return false;
    }

    private async Task<double> RecordDownloadedClipTelemetryAsync(
        ClipGenContext ctx,
        ClipVideoPromptBuilder.PromptBuildResult built,
        string mp4Path,
        int duration,
        bool supportsContinue)
    {
        // Trigger 100% automated background clip dialogue & speaker verification.
        // Telemetry recording below awaits this (if started) so DialogueTruncated
        // reflects the real Expected-vs-Heard result instead of staying hardcoded false.
        var dialogueVerificationTask = StartDialogueVerificationIfConfigured(ctx);
        // Probe the real rendered duration once — used both to carry a same-scene
        // continuation-chain padding nudge into the next clip (below) and, if timing
        // calibration is configured, for telemetry.
        var probedSec = await Mp4DurationReader.TryReadSecondsAsync(mp4Path, ctx.Ct).ConfigureAwait(false) ?? (double)duration;
        var overrunSec = ComputeCarryoverOverrunSec(supportsContinue, probedSec, duration);
        await RecordTimingTelemetryIfConfiguredAsync(
            ctx, built, duration, probedSec, dialogueVerificationTask).ConfigureAwait(false);
        return overrunSec;
    }

    private Task<ClipDialogueVerificationResult?> StartDialogueVerificationIfConfigured(ClipGenContext ctx)
    {
        var verifier = _dialogueVerification;
        if (verifier is null || !verifier.IsConfigured)
            return Task.FromResult<ClipDialogueVerificationResult?>(null);
        var projId = Snapshot.ProjectId ?? ctx.ProjectId ?? _projects.ActiveProjectId;
        var scene = ctx.Scene;
        var clip = ctx.Clip;
        return Task.Run(async () =>
        {
            try
            {
                return await verifier.VerifyClipDialogueAsync(projId, scene, clip, force: true, ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background dialogue verification failed for S{Scene:D2}C{Clip:D2}", scene, clip);
                return null;
            }
        }, ctx.Ct);
    }

    private async Task RecordTimingTelemetryIfConfiguredAsync(
        ClipGenContext ctx,
        ClipVideoPromptBuilder.PromptBuildResult built,
        int duration,
        double probedSec,
        Task<ClipDialogueVerificationResult?> dialogueVerificationTask)
    {
        // Record dynamic cut timing telemetry into SQLite database for continuous server learning
        if (_timingCalibration is null)
            return;
        var projId = Snapshot.ProjectId ?? ctx.ProjectId ?? _projects.ActiveProjectId;
        var evaluatorModelId = await TryLoadEvaluatorModelIdAsync(projId, ctx.Ct).ConfigureAwait(false);
        var wordCount = ExtractClipDialogueWordCount(ctx.ClipEl);
        var camCat = ExtractCameraCategory(ctx.ClipEl);
        var actCat = await ClassifyActionCategoryAsync(built.Prompt ?? "", ctx.Ct).ConfigureAwait(false);
        double camOverhead = _timingLedger is null ? 1.6 : ActionCameraOverheadLedger.GetOverheadSec(camCat, 1.6);
        double netSpeechSec = wordCount > 0 ? (wordCount / ClipDurationEstimator.DialogueWordsPerSecond) : 0.0;
        double measuredActOverhead = Math.Max(0.5, Math.Round(probedSec - camOverhead - netSpeechSec, 2));
        StartTimingTelemetryRecord(
            ctx, projId, evaluatorModelId, camCat, actCat, wordCount, duration, probedSec,
            camOverhead, measuredActOverhead, dialogueVerificationTask);
    }

    private async Task<string> TryLoadEvaluatorModelIdAsync(string projId, CancellationToken ct)
    {
        try
        {
            var evalCfg = await _projects.GetConfigAsync(projId, ct).ConfigureAwait(false);
            // Attribute evaluator to project Video review / planning model (Settings), never invent.
            return ProjectModelSelection.TryGet(
                evalCfg,
                ProjectModelSelection.QualityConfigKey,
                ProjectModelSelection.PlanningConfigKey,
                ProjectModelSelection.ChatConfigKey) ?? "";
        }
        catch { /* telemetry only */ }
        return "";
    }

    private static int ExtractClipDialogueWordCount(JsonElement clipEl)
    {
        // 1. Extract dialogue text & word count from clip blueprint
        string dialogueText = "";
        if (clipEl.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty(JsonKeys.Dialogue, out var dEl))
        {
            dialogueText = dEl.GetString() ?? "";
        }
        return string.IsNullOrWhiteSpace(dialogueText)
            ? 0
            : dialogueText.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string ExtractCameraCategory(JsonElement clipEl)
    {
        // 2. Extract camera movement category from blueprint or visual prompt
        if (clipEl.TryGetProperty("camera", out var camEl) && camEl.ValueKind == JsonValueKind.String)
        {
            var cam = camEl.GetString();
            if (!string.IsNullOrWhiteSpace(cam))
                return cam;
        }
        else if (clipEl.TryGetProperty("camera_category", out var ccEl) && ccEl.ValueKind == JsonValueKind.String)
        {
            var cc = ccEl.GetString();
            if (!string.IsNullOrWhiteSpace(cc))
                return cc;
        }
        return "cam_push_in";
    }

    private async Task<string> ClassifyActionCategoryAsync(string promptToAnalyze, CancellationToken ct)
    {
        // 3. Dynamically classify scene action category via AiActionOverheadClassifier
        string actCat = "act_generic_action";
        if (_timingClassifier is null)
            return actCat;
        var estimation = await _timingClassifier.ClassifyNovelActionAsync(promptToAnalyze, null, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(estimation.MatchCategoryId))
            actCat = estimation.MatchCategoryId;
        return actCat;
    }

    private void StartTimingTelemetryRecord(
        ClipGenContext ctx,
        string projId,
        string evaluatorModelId,
        string camCat,
        string actCat,
        int wordCount,
        int duration,
        double probedSec,
        double camOverhead,
        double measuredActOverhead,
        Task<ClipDialogueVerificationResult?> dialogueVerificationTask)
    {
        var calibration = _timingCalibration;
        if (calibration is null)
            return;
        var scene = ctx.Scene;
        var clip = ctx.Clip;
        var model = ctx.Model;
        _ = Task.Run(async () =>
        {
            try
            {
                var dialogueTruncated = await ResolveDialogueTruncatedAsync(dialogueVerificationTask)
                    .ConfigureAwait(false);
                await calibration.RecordCutTelemetryAsync(
                    projectId: projId,
                    sceneNumber: scene,
                    videoModelId: model,
                    videoModelVersion: "v1",
                    evaluatorModelId: evaluatorModelId,
                    evaluatorModelVersion: "v1",
                    cameraCategory: camCat,
                    actionCategory: actCat,
                    wordCount: wordCount,
                    estimatedDurationSec: (double)duration,
                    clipDurationSec: probedSec,
                    measuredCamOverheadSec: camOverhead,
                    measuredActionOverheadSec: measuredActOverhead,
                    dialogueTruncated: dialogueTruncated).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background timing telemetry logging failed for S{Scene:D2}C{Clip:D2}", scene, clip);
            }
        }, ctx.Ct);
    }

    private static async Task<bool> ResolveDialogueTruncatedAsync(
        Task<ClipDialogueVerificationResult?> dialogueVerificationTask)
    {
        var verification = await dialogueVerificationTask.ConfigureAwait(false);
        if (verification is null)
            return false;
        return ClipDialogueVerificationService.LooksTruncated(verification);
    }

    private async Task WriteClipSidecarIfConfiguredAsync(
        ClipGenContext ctx,
        ClipVideoPromptBuilder.PromptBuildResult built,
        string url,
        string requestId,
        int duration,
        double? providerLeadInSeconds = null)
    {
        if (_sidecars is null)
            return;
        try
        {
            var projDir = await _projects.GetProjectDirAsync(
                Snapshot.ProjectId ?? ctx.ProjectId ?? _projects.ActiveProjectId, ctx.Ct).ConfigureAwait(false);
            // xAI Files API reference for this exact clip, when generation requested
            // storage and it succeeded (see GrokVideoClient's storage_options) — lets a
            // later "AI Edit" reuse the file instead of re-uploading. Absent for
            // non-Grok providers or when storage wasn't granted; never required.
            var (sourceFileId, sourceFileExpiresAt) = _grok.TryGetStoredFileReference(requestId);
            await _sidecars.WriteSidecarAsync(
                projDir,
                ctx.Scene,
                ctx.Clip,
                prompt: built.Prompt ?? "",
                scriptText: "",
                model: ctx.Model,
                resolution: ctx.Resolution,
                durationSeconds: (double)duration,
                sha256: "",
                sizeBytes: 0,
                // Persist the provider-hosted video URL so an exported project can be re-hydrated
                // by another user on import (xAI/Grok URLs are long-lived). Provider is resolved
                // from the model via the catalog (SSoT) rather than hardcoded.
                sourceUrl: url,
                sourceProvider: SupportedModelCatalog.ResolveOrDefault(ctx.Model, ModelCapability.Video).ProviderId,
                sourceFileId: sourceFileId,
                sourceFileExpiresAtUnixSeconds: sourceFileExpiresAt,
                providerLeadInSeconds: providerLeadInSeconds,
                ct: ctx.Ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write clip sidecar for S{Scene:D2}C{Clip:D2}", ctx.Scene, ctx.Clip);
        }
    }

    private static string? TryGetStableBeatId(JsonElement clipEl)
    {
        if (clipEl.TryGetProperty("stable_beat_id", out var sbe) &&
            sbe.ValueKind == JsonValueKind.String &&
            sbe.GetString() is { Length: > 0 } sbid)
            return sbid;
        if (clipEl.TryGetProperty("beat_id", out var be) &&
            be.ValueKind == JsonValueKind.String &&
            be.GetString() is { Length: > 0 } bid)
            return bid;
        return null;
    }

    private async Task RecordClipCostAsync(
        ClipGenContext ctx,
        ClipVideoPromptBuilder.PromptBuildResult built,
        string requestId,
        int duration)
    {
        // Cost uses requested duration (no server file to probe until client registers).
        var costDurationSec = (double)duration;
        try
        {
            var costProjectId = Snapshot.ProjectId ?? ctx.ProjectId ?? _projects.ActiveProjectId;
            var stableBeatId = TryGetStableBeatId(ctx.ClipEl);

            // Character refs: any attached path beyond an optional location plate.
            var hadCharRefs = built.RefsAttachedToApi &&
                (built.ReferenceImagePaths.Count > (built.LocationRefAttached ? 1 : 0));

            var takeKind = ctx.TakeKindOverride
                ?? VideoTakeKinds.Resolve(
                    CurrentRun.Value?.TakeTrigger,
                    clipHadVideoBefore: ctx.HadVideoBefore,
                    isQaRetry: false);

            await _costs.RecordVideoGenerationAsync(
                costProjectId,
                ctx.Scene,
                ctx.Clip,
                costDurationSec,
                ctx.Resolution,
                ctx.Model,
                hasRefImage: built.ReferenceImagePaths.Count > 0 || ctx.PrevVideoPath is not null || ctx.ExtendSourceFileId is not null,
                isExtend: ctx.PrevVideoPath is not null || ctx.ExtendSourceFileId is not null,
                requestId: requestId,
                requestedDurationSec: duration,
                userId: Snapshot.UserId ?? _user.UserId,
                keyMode: CurrentRun.Value?.KeyMode,
                takeKind: takeKind,
                stableBeatId: stableBeatId,
                hadCharRefs: hadCharRefs,
                hadLocRef: built.LocationRefAttached,
                ct: ctx.Ct);
            await AppendLogAsync(
                $"  [Cost] tracked list-rate for S{ctx.Scene:D2}C{ctx.Clip} ({costDurationSec:F2}s, take={takeKind})");
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Cost] ledger write skipped: {ex.Message}");
        }
    }

    private async Task WriteAndLogPromptAsync(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        ClipVideoPromptBuilder.PromptBuildResult built,
        CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(projectDir, AssetsFolder, VideoFolder, "prompts");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"S{scene:D2}C{clip:D2}.txt");
            var header =
                $"# S{scene:D2}C{clip:D2}  {built.PromptLogSummary}\n" +
                $"# projectId: {projectId}\n" +
                $"# mode: {built.Mode}\n" +
                $"# castCount: {built.CastCount}\n" +
                $"# onScreen: {string.Join(", ", built.OnScreenKeys)}\n" +
                $"# refs: {string.Join(", ", built.ReferenceImagePaths.Select(Path.GetFileName))}\n" +
                $"# refsAttachedToApi: {built.RefsAttachedToApi}\n" +
                $"# startFrame: {built.StartFrameImagePath ?? "(none)"}\n" +
                $"# characters: {string.Join(", ", built.CharacterKeys)}\n\n";
            await File.WriteAllTextAsync(path, header + built.Prompt, ct).ConfigureAwait(false);

            var metaPath = Path.Combine(dir, $"S{scene:D2}C{clip:D2}.meta.json");
            ArchiveClipPromptHistory(projectDir, scene, clip, metaPath);
            var meta = new Dictionary<string, object?>
            {
                ["projectId"] = projectId,
                ["scene"] = scene,
                ["clip"] = clip,
                ["mode"] = built.Mode,
                ["castCount"] = built.CastCount,
                ["onScreenKeys"] = built.OnScreenKeys.ToList(),
                ["characterKeys"] = built.CharacterKeys.ToList(),
                ["refs"] = built.ReferenceImagePaths.Select(Path.GetFileName).ToList(),
                ["refsAttachedToApi"] = built.RefsAttachedToApi,
                ["styleHead"] = built.StyleHead,
                ["castCountLine"] = built.CastCountLine,
                ["actionText"] = built.ActionText,
                // Full prompt body on disk for manual / external AI review (PR5 project-local data)
                ["prompt"] = built.Prompt,
                ["promptLen"] = built.Prompt.Length,
                ["promptLogSummary"] = built.PromptLogSummary,
                ["startFrame"] = built.StartFrameImagePath,
                ["builtAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
            };
            var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }) + "\n";
            await File.WriteAllTextAsync(metaPath, metaJson, ct).ConfigureAwait(false);

            await AppendLogAsync(
                $"  [Prompt] saved {Path.GetRelativePath(projectDir, path)} + meta " +
                $"({built.Prompt.Length} chars, castCount={built.CastCount})");
            await AppendLogAsync("--- PROMPT BEGIN ---");
            const int chunk = 3500;
            for (var i = 0; i < built.Prompt.Length; i += chunk)
            {
                var len = Math.Min(chunk, built.Prompt.Length - i);
                await AppendLogAsync(built.Prompt.Substring(i, len));
            }
            await AppendLogAsync("--- PROMPT END ---");
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Prompt] log failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Before a clip's prompt meta is overwritten by a fresh generation, copy the previous
    /// version into assets/video/history/ so ClipPromptCompareViewer has a prior prompt to show
    /// alongside whatever prior video pagetomovie-media.js archived client-side. Best-effort.
    /// </summary>
    private static void ArchiveClipPromptHistory(string projectDir, int scene, int clip, string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath)) return;
            var historyDir = Path.Combine(projectDir, AssetsFolder, VideoFolder, "history");
            Directory.CreateDirectory(historyDir);
            var dest = Path.Combine(
                historyDir,
                $"scene_{scene:D2}_clip_{clip:D2}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.meta.json");
            File.Copy(metaPath, dest, overwrite: false);
        }
        catch
        {
            // never block a regeneration on history archiving
        }
    }

    /// <summary>Archived prompt versions for one clip (newest first), for ClipPromptCompareViewer.</summary>
    public static async Task<List<ClipPromptHistoryEntry>> ListClipPromptHistoryAsync(
        string projectDir, int scene, int clip, CancellationToken ct = default)
    {
        var result = new List<ClipPromptHistoryEntry>();
        var historyDir = Path.Combine(projectDir, AssetsFolder, VideoFolder, "history");
        if (!Directory.Exists(historyDir)) return result;

        var prefix = $"scene_{scene:D2}_clip_{clip:D2}_";
        foreach (var file in Directory.GetFiles(historyDir, $"{prefix}*.meta.json"))
        {
            try
            {
                var name = Path.GetFileName(file);
                var stamp = name[prefix.Length..^".meta.json".Length];
                if (!long.TryParse(stamp, out var ms)) continue;

                using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                var root = doc.RootElement;
                string? prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() : null;
                result.Add(new ClipPromptHistoryEntry
                {
                    TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms),
                    Prompt = prompt ?? "",
                    VideoRelativePath = $"assets/video/history/scene_{scene:D2}_clip_{clip:D2}_{ms}.mp4",
                });
            }
            catch
            {
                // skip unreadable/corrupt history entry
            }
        }

        result.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        return result;
    }

    public sealed class ClipPromptHistoryEntry
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public string Prompt { get; set; } = "";
        /// <summary>Relative path under the project dir — client checks its own media folder for this.</summary>
        public string VideoRelativePath { get; set; } = "";
    }

    /// <summary>
    /// Probe final clip length (MP4 box parse) and write duration sidecar for cost ledger.
    /// </summary>
    private async Task<double?> EnsureClipDurationSidecarAsync(
        string videoPath,
        int scene,
        int clip,
        CancellationToken ct)
    {
        if (!File.Exists(videoPath))
            return null;
        try
        {
            var sec = await Mp4DurationReader.TryReadSecondsAsync(videoPath, ct).ConfigureAwait(false);
            if (sec is > 0)
            {
                await MediaDurationProbe.WriteDurationSidecarAsync(videoPath, sec.Value, ct)
                    .ConfigureAwait(false);
                await AppendLogAsync(
                    $"  [Duration] S{scene:D2}C{clip:D2} sidecar {sec.Value:F2}s");
                return sec.Value;
            }
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Duration] sidecar skip: {ex.Message}");
        }

        return null;
    }

    private static bool OnScreenSetsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        // Inputs are expected already sorted ignore-case; still compare as sets.
        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(b);
    }

    /// <summary>
    /// Video-extend cannot attach plates to the API, but locked refs must still exist on disk
    /// so CHARACTER VARIABLES / future reseeds stay authoritative.
    /// </summary>
    private void EnsureOnScreenLocksExist(
        string projectId,
        string projectDir,
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        JsonElement clipEl,
        JsonElement? blueprintRoot)
    {
        var (missingCast, mustCast, _) = ClassifyMissingRefs(projectId, projectDir, built, profiles, clipEl, blueprintRoot);
        if (mustCast.Count > 0) throw UncastRoleException(mustCast);
        if (missingCast.Count == 0) return;

        throw new InvalidOperationException(
            "Locked character reference images required on disk before video-extend " +
            "(identity continuity even though the API cannot attach plates). " +
            $"Missing ref for: {string.Join(", ", missingCast)}. " +
            "Open Characters → generate + lock a portrait for each on-screen role.");
    }

    /// <summary>
    /// On fresh (non-extend) gens, every non-voice-only character in the clip prompt must have
    /// a locked ref image actually attached — prevents identity drift across clips.
    /// </summary>
    private void EnsureFreshGenHasLockedRefs(
        string projectId,
        string projectDir,
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        JsonElement clipEl,
        JsonElement? blueprintRoot)
    {
        var (missingCast, mustCast, textOnly) = ClassifyMissingRefs(projectId, projectDir, built, profiles, clipEl, blueprintRoot);
        if (mustCast.Count > 0) throw UncastRoleException(mustCast);
        if (missingCast.Count > 0)
        {
            throw new InvalidOperationException(
                "Locked character reference images required for fresh video gen (avoids face drift). " +
                $"Missing ref for: {string.Join(", ", missingCast)}. " +
                "Open Characters → generate + lock a portrait for each on-screen role.");
        }

        // Extras rendered from description do not need (and cannot have) a plate; only cast
        // members on screen require an attached reference.
        var onScreenCast = OnScreenVisualKeys(built, profiles).Except(textOnly, StringComparer.OrdinalIgnoreCase).ToList();
        if (onScreenCast.Count > 0 && built.ReferenceImagePaths.Count == 0)
        {
            throw new InvalidOperationException(
                "Fresh video gen built a prompt with on-screen cast but attached 0 reference images. " +
                $"On screen: {string.Join(", ", onScreenCast)}. Lock portraits under Characters and retry.");
        }
    }

    /// <summary>
    /// Split on-screen keys without an attached/locked plate into: cast members missing a lock
    /// (fail-fast — the user can lock one), un-cast roles that must be cast (speak or recur), and
    /// un-cast extras allowed to render from description (see <see cref="UncastOnScreenPolicy"/>).
    /// </summary>
    private (List<string> MissingCast, List<UncastOnScreenPolicy.Decision> MustCast, List<string> TextOnly) ClassifyMissingRefs(
        string projectId,
        string projectDir,
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        JsonElement clipEl,
        JsonElement? blueprintRoot)
    {
        var missingCast = new List<string>();
        var mustCast = new List<UncastOnScreenPolicy.Decision>();
        var textOnly = new List<string>();
        foreach (var key in MissingOnScreenLockKeys(projectId, projectDir, built, profiles))
        {
            if (profiles.ContainsKey(key)) { missingCast.Add(key); continue; }
            var d = UncastOnScreenPolicy.Decide(key, clipEl, blueprintRoot);
            if (d.TextOnly) textOnly.Add(key); else mustCast.Add(d);
        }
        if (textOnly.Count > 0)
            _ = AppendLogAsync($"  [Cast] {textOnly.Count} background role(s) rendered from description (not in cast, non-speaking, single clip): {string.Join(", ", textOnly)}");
        return (missingCast, mustCast, textOnly);
    }

    private static InvalidOperationException UncastRoleException(List<UncastOnScreenPolicy.Decision> mustCast)
    {
        var parts = mustCast.Select(d => d.SpeaksInClip
            ? $"{d.Key} (speaks)"
            : $"{d.Key} (appears in {d.ClipAppearances} clips)");
        return new InvalidOperationException(
            "On-screen role(s) not in the cast need a locked portrait because they speak or recur: " +
            $"{string.Join(", ", parts)}. Add them under Characters and lock a look, or rewrite the shot plan so they appear once, silently.");
    }

    /// <summary>
    /// On-screen character keys that require a locked reference image for identity consistency.
    /// Excludes voice-only roles (never on screen) and group/ensemble cast (Children, Crowd, …):
    /// a group has no single portrait identity to lock or drift, so the video model renders its
    /// members freely — requiring a ref for a group is impossible and blocks generation. Group
    /// detection uses the same <see cref="CastKindClassifier.IsGroup"/> signal as the cast gates.
    /// </summary>
    internal static List<string> OnScreenVisualKeys(
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        return (built.OnScreenKeys.Count > 0 ? built.OnScreenKeys : built.CharacterKeys)
            .Where(k => !(profiles.TryGetValue(k, out var p) && p.VoiceOnly))
            .Where(k =>
            {
                // Full-signal group detection (cast_kind + display + description), matching the
                // cast gates in ProjectStore — a chorus/ensemble seed with a non-token key (e.g.
                // Character_The_Choir + cast_kind:"chorus") must be exempt here too, or generation
                // hard-fails demanding a locked portrait a group can never have.
                profiles.TryGetValue(k, out var p);
                return !CastKindClassifier.IsGroup(k, p?.DisplayName, p?.CastKind, p?.Description);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> MissingOnScreenLockKeys(
        string projectId,
        string projectDir,
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        var onScreen = OnScreenVisualKeys(built, profiles);
        var missing = new List<string>();
        foreach (var key in onScreen)
        {
            var path = ClipVideoPromptBuilder.ResolveCharacterRefPathPublic(projectDir, key)
                       ?? _projects.ResolveCharacterRefPath(projectId, key);
            if (path is null || !File.Exists(path) || new FileInfo(path).Length < 64)
                missing.Add(key);
        }
        return missing;
    }

    private static string? FindClipVisualInBlueprint(JsonElement root, int scene, int clipNum)
    {
        try
        {
            var c = FindClipElementInBlueprint(root, scene, clipNum);
            if (c is { } clip && clip.TryGetProperty("visual_prompt", out var vp))
                return vp.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static JsonElement? FindClipElementInBlueprint(JsonElement root, int scene, int clipNum)
    {
        try
        {
            if (!root.TryGetProperty(ScenesKey, out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var s in scenes.EnumerateArray())
            {
                if (!s.TryGetProperty(JsonKeys.SceneNumber, out var sn) || !sn.TryGetInt32(out var n) || n != scene)
                    continue;
                return FindClipInScene(s, clipNum);
            }
        }
        catch { /* ignore */ }
        return null;
    }


    /// <summary>Admin quality-gate: regenerate when dialogue QA says mismatch / swap / truncated.</summary>
    private static bool DialogueQaNeedsRegen(ClipDialogueVerificationResult ver)
    {
        var status = (ver.Status ?? "").Trim().ToLowerInvariant();
        if (status is "mismatch" or "speaker_swap")
            return true;
        if (ClipDialogueVerificationService.LooksTruncated(ver))
            return true;
        if (!string.IsNullOrWhiteSpace(ver.ExpectedDialogue) &&
            ver.DialogueAccuracyScore < 0.5 &&
            status is not "no_speech" and not "verified")
            return true;
        return false;
    }

    /// <summary>
    /// True when the clip has spoken dialogue or VO text (not silent establish).
    /// </summary>
    internal static bool ClipHasSpokenAudio(JsonElement clipEl)
    {
        if (!clipEl.TryGetProperty(JsonKeys.AudioPayload, out var ap) ||
            ap.ValueKind != JsonValueKind.Object)
            return false;
        var dialogue = ap.TryGetProperty(JsonKeys.Dialogue, out var d) ? d.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(dialogue))
            return false;
        var delivery = (ap.TryGetProperty("delivery", out var del) ? del.GetString() ?? "none" : "none")
            .Trim().ToLowerInvariant();
        if (delivery is "none" or "")
            return false;
        return Stage2PlannerService.IsOnCameraDelivery(delivery) ||
               delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought" or
                   "voiceover" or "voice_over" or "off_camera" or "offcamera";
    }

    internal static JsonElement? FindClipInScene(JsonElement sceneEl, int clipNum)
    {
        if (!sceneEl.TryGetProperty(VeoClipsKey, out var clips) ||
            clips.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var c in clips.EnumerateArray())
        {
            if (ClipKeying.ClipNumber(c) == clipNum)
                return c;
        }
        return null;
    }

    private static JsonElement? FindScene(JsonElement root, int sceneNum)
    {
        if (!root.TryGetProperty(ScenesKey, out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in scenes.EnumerateArray())
        {
            if (s.TryGetProperty(JsonKeys.SceneNumber, out var n) && n.TryGetInt32(out var sn) && sn == sceneNum)
                return s;
        }
        return null;
    }

    /// <summary>
    /// The auto-inserted end-credits scene is a title card with no real cast, so it is exempt from the
    /// locked-character video-gen gate. Delegates to <see cref="ProjectStore.IsCreditsScene"/> — the
    /// single source of truth that also derives <c>SceneSummary.IsCredits</c> — so the gate and the
    /// Scenes list can never disagree. Never keys off a hardcoded scene number or story-specific title.
    /// </summary>
    internal static bool IsCreditsScene(JsonElement? sceneEl) => ProjectStore.IsCreditsScene(sceneEl);

    /// <summary>
    /// Prefer explicit request resolution, else project Configuration, else app default —
    /// then guard against mixing resolutions within one project (see
    /// <see cref="GetLockedResolutionAsync"/>).
    /// </summary>
    private async Task<string> ResolveVideoResolutionAsync(
        string projectId,
        string? requested,
        CancellationToken ct)
    {
        var resolution = string.IsNullOrWhiteSpace(requested)
            ? await ReadConfiguredOrDefaultResolutionAsync(projectId, ct).ConfigureAwait(false)
            : NormalizeResolution(requested);

        var locked = await GetLockedResolutionAsync(projectId, ct).ConfigureAwait(false);
        if (locked is not null && !string.Equals(locked, resolution, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"This project's existing clips are {locked} — generating at {resolution} would mix " +
                $"resolutions in one movie. Delete the existing clips first, or generate at {locked}.");
        }

        return resolution ?? "480p";
    }

    private async Task<string?> ResolveTargetAspectRatioAsync(string projectId, JsonElement? blueprint, CancellationToken ct)
    {
        try
        {
            if (blueprint is { ValueKind: JsonValueKind.Object } bp &&
                bp.TryGetProperty("global_production_variables", out var gpv) &&
                gpv.TryGetProperty("target_aspect_ratio", out var ar) &&
                ar.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(ar.GetString()))
            {
                return ar.GetString();
            }

            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var vision = ProjectVisionMeta.TryRead(projectDir);
            if (vision is not null && !string.IsNullOrWhiteSpace(vision.VisualMedium))
                return ProjectVisionMeta.DefaultAspectRatio(vision.VisualMedium);
        }
        catch
        {
            // fallback
        }
        return null;
    }

    private async Task<string> ReadConfiguredOrDefaultResolutionAsync(string projectId, CancellationToken ct)
    {
        string? resolution = null;
        try
        {
            var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            EnsureLabModelsAllowed(cfg);
            resolution = TryReadResolutionFromConfig(cfg);
        }
        catch
        {
            // fall through to app default
        }

        return NormalizeResolution(
            resolution ?? (string.IsNullOrWhiteSpace(_opts.DefaultResolution) ? "480p" : _opts.DefaultResolution));
    }

    private static string? TryReadResolutionFromConfig(IReadOnlyDictionary<string, JsonElement> cfg)
    {
        if (!cfg.TryGetValue("resolution", out var el))
            return null;
        var fromCfg = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(fromCfg) ? null : NormalizeResolution(fromCfg);
    }

    /// <summary>
    /// The resolution already used by this project's on-disk clips, if consistent — guards
    /// against accidentally mixing resolutions within one project. Null when there are no
    /// on-disk clips yet, or existing data doesn't settle on one value (fail-open: never
    /// block generation on ambiguous or missing cost-ledger history).
    /// </summary>
    public async Task<string?> GetLockedResolutionAsync(string projectId, CancellationToken ct = default)
    {
        try
        {
            var onDisk = _reviewIndex.ListOnDiskClipCoords(projectId);
            var ledger = await _costs.GetCostLedgerAsync(projectId, ct).ConfigureAwait(false);
            return DetermineLockedResolution(onDisk, ledger);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pure decision: given which (scene, clip) pairs are on disk and the project's cost
    /// ledger, what resolution (if any) is this project locked to? Null when there are no
    /// on-disk clips, or the ledger doesn't settle on one consistent value for them
    /// (fail-open — ambiguous/missing history never blocks generation).
    /// </summary>
    public static string? DetermineLockedResolution(
        IEnumerable<(int Scene, int Clip)> onDiskClips,
        IEnumerable<CostEvent> costLedger)
    {
        var onDisk = onDiskClips as ICollection<(int Scene, int Clip)> ?? onDiskClips.ToList();
        if (onDisk.Count == 0)
            return null;

        var onDiskSet = onDisk.ToHashSet();
        var resolutions = costLedger
            .Where(e => e.Scene is int s && e.Clip is int c &&
                        onDiskSet.Contains((s, c)) &&
                        !string.IsNullOrWhiteSpace(e.Resolution))
            .Select(e => NormalizeResolution(e.Resolution))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return resolutions.Count == 1 ? resolutions[0] : null;
    }

    /// <summary>
    /// Fail closed before video spend when the selected model cannot attach locked character plates.
    /// </summary>
    private async Task EnsureVideoModelCapabilitiesAsync(
        string projectId,
        bool needReferenceImages,
        CancellationToken ct,
        string? modelOverride = null)
    {
        if (!needReferenceImages)
            return;

        var modelId = await ResolveVideoModelAsync(projectId, ct, modelOverride).ConfigureAwait(false);
        var entry = SupportedModelCatalog.ResolveOrDefault(modelId, ModelCapability.Video);

        if (!entry.SupportsReferenceImages)
        {
            throw new InvalidOperationException(
                $"Video model '{entry.Id}' cannot attach locked character reference plates. " +
                "Switch project video model to grok-imagine-video, or disable the cast lock gate " +
                "only if you accept identity drift. " +
                (string.IsNullOrWhiteSpace(entry.Notes) ? "" : entry.Notes));
        }
    }

    /// <summary>
    /// Project <c>model_name</c> only — Settings selection required (no host DefaultModel invent).
    /// <paramref name="requestedModel"/> is a one-off override (admin model comparison); when set it
    /// must resolve to a video-capable catalog model, and the project config is left unchanged.
    /// </summary>
    private async Task<string> ResolveVideoModelAsync(string projectId, CancellationToken ct, string? requestedModel = null)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            EnsureLabModelsAllowed(cfg);
        if (!string.IsNullOrWhiteSpace(requestedModel))
            return ResolveExplicitVideoModel(requestedModel);
        return ProjectModelSelection.RequireVideo(cfg, "Video generation");
    }

    /// <summary>
    /// Resolve an admin one-off video model override (from <see cref="StartBatchGenRequest.VideoModel"/>)
    /// to a catalog id, strictly enforcing video capability. <see cref="ProjectModelSelection.RequireExplicit"/>
    /// alone falls back to an any-capability catalog match, so an override id that names a chat/image/audio
    /// model would slip through and route batch spend to a non-video model — re-check Video here.
    /// </summary>
    internal static string ResolveExplicitVideoModel(string requestedModel)
    {
        var id = ProjectModelSelection.RequireExplicit(requestedModel, ModelCapability.Video, "Video generation");
        if (SupportedModelCatalog.Find(id, ModelCapability.Video) is null)
            throw new InvalidOperationException(
                $"Video generation: model '{requestedModel}' is not a video model. Pick a video model.");
        return id;
    }

    private async Task<string> ResolvePlanningModelAsync(string projectId, string? requestedModel, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel))
            return ProjectModelSelection.RequireExplicit(requestedModel, ModelCapability.Chat, "Script & planning");
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        return ProjectModelSelection.RequirePlanning(cfg, "Script & planning");
    }

    private async Task<string> ResolveVisionModelAsync(string projectId, string? requestedModel, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel))
            return ProjectModelSelection.RequireExplicit(requestedModel, ModelCapability.Vision, "Image vision");
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        return ProjectModelSelection.RequireVision(cfg, "Image vision");
    }

    private static string NormalizeResolution(string? value)
    {
        var v = (value ?? "720p").Trim().ToLowerInvariant();
        return v switch
        {
            "480" or "480p" => "480p",
            "720" or "720p" => "720p",
            "1080" or "1080p" => "1080p",
            _ => v.EndsWith('p') ? v : $"{v}p",
        };
    }

    /// <summary>
    /// Require env keys for the project's selected video model (not a hardcoded XAI_API_KEY message).
    /// MultiProvider IsConfigured is true if either provider has a key — that misdirects Gemini-only setups.
    /// </summary>
    private async Task EnsureVideoProviderConfiguredAsync(string projectId, CancellationToken ct)
    {
        // Fakes register FakeGrokVideoClient (IsConfigured=true). Do not demand real provider
        // env keys — soaks use PageToMovie__UseFakes without XAI_API_KEY. Optional XAI_API_KEY=fake
        // also works (any non-empty ambient key), but is not required when UseFakes is on.
        if (_opts.UseFakes)
            return;

        var modelId = await ResolveVideoModelAsync(projectId, ct).ConfigureAwait(false);
        var entry = SupportedModelCatalog.ResolveOrDefault(modelId, ModelCapability.Video);

        // Ambient per-user keys count as configured (personal BYOK or server env via scope).
        var ambient = entry.Provider switch
        {
            ModelProviderFamily.Xai => ApiKeyScope.Current,
            ModelProviderFamily.Google => ApiKeyScope.CurrentGemini,
            ModelProviderFamily.Anthropic => ApiKeyScope.CurrentAnthropic,
            ModelProviderFamily.Fal => ApiKeyScope.CurrentFal,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(ambient))
            return;

        var missing = SupportedModelCatalog.MissingEnvKeys(entry);
        if (missing.Count == 0)
            return;

        var keys = string.Join(" / ", missing);
        throw new InvalidOperationException(
            $"{keys} is not set (required for video model '{entry.Id}' / {entry.ProviderId}). " +
            "Add a personal key in Configuration, or set the server environment variable.");
    }

    /// <summary>
    /// Project-wide spend gate: every cast seed must have an approved voice profile and
    /// (for on-screen roles) a locked ref image before any video generation.
    /// </summary>
    private void EnsureCastReadyForVideo(string projectId)
    {
        var missing = _projects.GetCastNotReadyForVideo(projectId);
        if (missing.Count == 0)
            return;

        var detail = string.Join("; ", missing);
        throw new InvalidOperationException(
            "Cast not ready for video gen — approve voice and locked image for every character first " +
            $"(avoids wasting API spend). Missing: {detail}. " +
            "Open Characters → set voice, then generate + lock a portrait. " +
            "Voice-only roles (e.g. Narrator) need a voice profile only.");
    }

    /// <summary>
    /// Scene-level safety net for on-screen keys mentioned in the blueprint that may not
    /// appear in cast seeds (still require a locked ref if they are not voice-only).
    /// </summary>
    private void EnsureSceneCharactersLocked(string projectId, int sceneNumber)
    {
        var unlocked = _projects.GetUnlockedOnScreenCharacters(projectId, sceneNumber);
        if (unlocked.Count == 0)
            return;

        var names = string.Join(", ", unlocked);
        throw new InvalidOperationException(
            $"Scene {sceneNumber}: locked character refs required before video gen. " +
            $"Missing lock(s): {names}. " +
            "Open Characters → lock a book plate or generate + lock a portrait. " +
            "(Only true voice-only roles skip images.)");
    }

    private void ReportStage2ProgressLine(string line)
    {
        _ = AppendLogAsync(line);
        _ = UpdateAsync(s => ApplyStage2Progress(s, line));
    }

    private static void ApplyStage2Progress(JobSnapshot s, string line)
    {
        s.Message = line;
        if (TryApplyStage2PlanningSceneCount(s, line))
            return;
        if (TryApplyStage2PlanningComplete(s, line))
            return;
        if (TryApplyStage2SceneOf(s, line))
            return;
        ApplyStage2MergedOrComplete(s, line);
    }

    private static bool TryApplyStage2PlanningSceneCount(JobSnapshot s, string line)
    {
        // "Planning N scene(s) @ …"
        var mPlan = CommonRegex.Match(
            line, @"Planning\s+(\d+)\s+scene", RegexOptions.IgnoreCase);
        if (!mPlan.Success || !int.TryParse(mPlan.Groups[1].Value, out var nScenes) || nScenes <= 0)
            return false;
        s.Total = Math.Max(s.Total, nScenes);
        s.Index = Math.Max(s.Index, 0);
        return true;
    }

    private static bool TryApplyStage2PlanningComplete(JobSnapshot s, string line)
    {
        // "Planning scenes: 3/29 complete"
        var mDone = CommonRegex.Match(
            line, @"Planning scenes:\s*(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
        if (!mDone.Success
            || !int.TryParse(mDone.Groups[1].Value, out var doneN)
            || !int.TryParse(mDone.Groups[2].Value, out var totN)
            || totN <= 0)
            return false;
        s.Total = Math.Max(s.Total, totN);
        s.Index = Math.Max(s.Index, Math.Min(doneN, totN));
        return true;
    }

    private static bool TryApplyStage2SceneOf(JobSnapshot s, string line)
    {
        // "Scene 12 of 29…"
        var mOf = CommonRegex.Match(
            line, @"Scene\s+(\d+)\s+of\s+(\d+)", RegexOptions.IgnoreCase);
        if (!mOf.Success
            || !int.TryParse(mOf.Groups[1].Value, out var snOf)
            || !int.TryParse(mOf.Groups[2].Value, out var totOf)
            || totOf <= 0)
            return false;
        s.Total = Math.Max(s.Total, totOf);
        // Don't jump Index past completed count — keep "of" as context in Message.
        if (s.Index < snOf - 1)
            s.Index = Math.Max(s.Index, Math.Min(snOf - 1, totOf));
        return true;
    }

    private static void ApplyStage2MergedOrComplete(JobSnapshot s, string line)
    {
        if (!IsStage2MergedOrCompleteLine(line))
            return;
        if (s.Total > 0)
            s.Index = s.Total;
        else
            s.Index = Math.Max(s.Index, 9);
    }

    private static bool IsStage2MergedOrCompleteLine(string line) =>
        line.Contains("Merged", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Backed up", StringComparison.OrdinalIgnoreCase) ||
        (line.Contains("complete", StringComparison.OrdinalIgnoreCase) &&
         !line.Contains("Planning scenes", StringComparison.OrdinalIgnoreCase));

    private async Task ReportStage1ProgressAsync(string line)
    {
        // Single UpdateAsync so Index/Total + log stay atomic (no race losing counters).
        // Keep Total on a 10-step phase scale so single-pass adapt still moves the bar
        // (legacy chunk-only counters left Total=0 → UI stuck at 35%).
        await UpdateAsync(s => ApplyStage1Progress(s, line));
        if (_sink is not null)
            await _sink.OnJobLogAsync(line);
    }

    private static void ApplyStage1Progress(JobSnapshot s, string line)
    {
        AppendJobLogLine(s, line);
        s.Message = line;
        if (s.Total <= 0)
            s.Total = 10;
        if (TryApplyChunkAdaptProgress(s, line))
            return;
        if (TryApplyVisionPrepareProgress(s, line))
            return;
        ApplyStage1KeywordProgress(s, line);
    }

    private static void AppendJobLogLine(JobSnapshot s, string line)
    {
        if (IsStage1HeartbeatLine(line) && s.Log.Count > 0 && IsStage1HeartbeatLine(s.Log[^1]))
        {
            s.Log[^1] = line;
            return;
        }
        if (s.Log.Count == 0 || s.Log[^1] != line)
        {
            s.Log.Add(line);
            if (s.Log.Count > 120)
                s.Log = s.Log.TakeLast(120).ToList();
        }
    }

    private static bool IsStage1HeartbeatLine(string? line) =>
        !string.IsNullOrWhiteSpace(line)
        && (line.StartsWith("Still working — ", StringComparison.Ordinal)
            || line.StartsWith("Still writing", StringComparison.Ordinal)
            || line.StartsWith("Still generating", StringComparison.Ordinal));

    private static bool TryApplyChunkAdaptProgress(JobSnapshot s, string line)
    {
        // Multi-chunk adapt: map chunk i/N into phases 4–8
        var m = CommonRegex.Match(
            line, @"chunk\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success ||
            !int.TryParse(m.Groups[1].Value, out var idx) ||
            !int.TryParse(m.Groups[2].Value, out var tot) ||
            tot <= 0)
            return false;
        var chunkDone = line.Contains(StatusDone, StringComparison.OrdinalIgnoreCase);
        var shown = chunkDone ? idx : Math.Max(1, idx);
        s.Total = tot;
        s.Index = Math.Clamp(shown, 1, tot);
        return true;
    }

    private static bool TryApplyVisionPrepareProgress(JobSnapshot s, string line)
    {
        // Vision prepare: page i/N → phases 1–3
        var mVis = CommonRegex.Match(
            line, @"(?:Grok vision|Reading page|page)\s+(\d+)\s*/\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!mVis.Success ||
            !int.TryParse(mVis.Groups[1].Value, out var vi) ||
            !int.TryParse(mVis.Groups[2].Value, out var vt) ||
            vt <= 0)
            return false;
        var frac = Math.Clamp((vi - 1.0) / vt, 0, 1);
        s.Index = Math.Max(s.Index, 1 + (int)Math.Round(2.0 * frac));
        return true;
    }

    private static void ApplyStage1KeywordProgress(JobSnapshot s, string line)
    {
        if (line.Contains("Screenplay ready", StringComparison.OrdinalIgnoreCase))
            s.Index = Stage1CompleteOrFixed(s, 10);
        else if (ContainsAnyIgnoreCase(line, "approving", "Fountain draft saved", "plate", "Attaching"))
            s.Index = Stage1CompleteOrFloor(s, 9);
        else if (ContainsAnyIgnoreCase(line, "name-spelling", "Name normalization", "Names checked",
                     "Location names", "Location normalization", "narration", "split V.O."))
            s.Index = Stage1CompleteOrFloor(s, 9);
        else if (ContainsAnyIgnoreCase(line, "Merge", "Stitch"))
            s.Index = Stage1CompleteOrFloor(s, 8);
        else if (ContainsAnyIgnoreCase(line, "repair", "retry", "Refin"))
            s.Index = Stage1NearCompleteOrFloor(s, 7);
        else if (ContainsAnyIgnoreCase(line, "single pass", "Adapting book", "Book split", "multi-chunk"))
            s.Index = Stage1EarlyAdaptIndex(s);
        else if (ContainsAnyIgnoreCase(line, "Target runtime", "building Fountain", "Writing screenplay"))
            s.Index = Math.Max(s.Index, 1);
        else if (ContainsAnyIgnoreCase(line, "prepare", "Extract", "Vision", "book text", "Checking book"))
            s.Index = Math.Max(s.Index, 1);
        else if (!IsStage1HeartbeatLine(line))
            s.Index = Math.Max(s.Index, 1);
    }

    private static int Stage1CompleteOrFixed(JobSnapshot s, int whenTotalUnknown) =>
        s.Total > 0 ? s.Total : whenTotalUnknown;

    private static int Stage1CompleteOrFloor(JobSnapshot s, int floor) =>
        s.Total > 0 ? s.Total : Math.Max(s.Index, floor);

    private static int Stage1NearCompleteOrFloor(JobSnapshot s, int floor) =>
        s.Total > 0 ? Math.Max(s.Index, s.Total - 1) : Math.Max(s.Index, floor);

    private static int Stage1EarlyAdaptIndex(JobSnapshot s) =>
        Math.Max(s.Index, s.Total >= 10 ? 4 : 1);

    private async Task AppendLogAsync(string message)
    {
        _logLines.Enqueue(message);
        await UpdateAsync(s =>
        {
            // Avoid duplicate consecutive lines (AppendLog after Update that already set Message)
            if (s.Log.Count == 0 || s.Log[^1] != message)
            {
                s.Log.Add(message);
                if (s.Log.Count > 120)
                    s.Log = s.Log.TakeLast(120).ToList();
            }
            s.Message = message;
        });
        if (_sink is not null)
            await _sink.OnJobLogAsync(message);
    }

    private async Task UpdateAsync(Action<JobSnapshot> mutate)
    {
        var run = CurrentRun.Value;
        if (run is null) return;
        await run.SnapLock.WaitAsync();
        try
        {
            mutate(run.Snapshot);
            if (!string.IsNullOrEmpty(run.ActiveJobId))
            {
                _jobs.Update(run.ActiveJobId, rec =>
                {
                    rec.Status = run.Snapshot.Status;
                    rec.Kind = run.Snapshot.Kind;
                    rec.Message = run.Snapshot.Message;
                    rec.ProjectId = run.Snapshot.ProjectId;
                    rec.UserId = run.Snapshot.UserId;
                    rec.CharKey = run.Snapshot.CharKey;
                    rec.Scene = run.Snapshot.Scene;
                    rec.Clip = run.Snapshot.Clip;
                    rec.Index = run.Snapshot.Index;
                    rec.Total = run.Snapshot.Total;
                    rec.Log = run.Snapshot.Log.ToList();
                    rec.Error = run.Snapshot.Error;
                    rec.StartedAt = run.Snapshot.StartedAt;
                    rec.FinishedAt = run.Snapshot.FinishedAt;
                    rec.ClientMediaUrl = run.Snapshot.ClientMediaUrl;
                    rec.ClientRelativePath = run.Snapshot.ClientRelativePath;
                    if (run.Snapshot.JobId is null)
                        run.Snapshot.JobId = rec.JobId;
                });
            }
            await PublishAsync();
        }
        finally
        {
            run.SnapLock.Release();
        }
    }

    private async Task FinishAsync(string status, string message, string? error = null)
    {
        string? projectId = null;
        string? kind = null;
        await UpdateAsync(s =>
        {
            s.Status = status;
            s.Message = message;
            s.Error = error;
            s.FinishedAt = DateTimeOffset.UtcNow;
            if (s.Total > 0 && status == StatusDone)
                s.Index = s.Total;
            projectId = s.ProjectId;
            kind = s.Kind;
        });
        await AppendLogAsync(message);

        // Scene list cache: clip/composite counts change on gen/remux/stage done
        if (status is StatusDone or StatusError or StatusCancelled)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                projectId = CurrentRun.Value?.Snapshot.ProjectId;
            _projects.InvalidateSceneListCache(projectId);
        }

        // PR4.5b: keep ARTIFACTS.md / artifact_index.json current after pipeline work
        if (status == StatusDone &&
            !string.IsNullOrWhiteSpace(projectId) &&
            ShouldRefreshArtifactIndex(kind))
        {
            await TryRefreshArtifactIndexAsync(projectId).ConfigureAwait(false);
        }

        // Stage-end package history: one debounced commit for finished film/music work
        // (text artifacts only — MP4/MP3 stay gitignored). Intermediate clip writes do not commit.
        if ((status == StatusDone || status == StatusPartial) &&
            !string.IsNullOrWhiteSpace(projectId) &&
            StageEndAutoGitMessage(kind) is { } gitMsg)
        {
            _projects.TriggerAutoGitCommit(projectId, gitMsg);
        }
    }

    /// <summary>
    /// Job kinds that represent a complete pipeline stage for project package history.
    /// Book / screenplay / cast / stage2 also commit from their services; film+music finish here.
    /// </summary>
    private static string? StageEndAutoGitMessage(string? kind) =>
        ProjectStageCommits.FromJobKind(kind);

    /// <summary>
    /// Server MP4 bytes or client-folder marker (.client.json). Called per-clip inside bulk
    /// "what's missing" loops, so this stays a cheap sync file check rather than routing through
    /// MediaSyncLocator (SQL-backed, async) — that would turn a directory scan into an N+1 query
    /// per clip for no correctness gain, since a marker file and its registry row are written
    /// together atomically (see /api/projects/{id}/media/register) and this already checks it.
    /// MediaSyncLocator is for new, single-clip call sites that also need sha256/size (Takes
    /// list, playback staleness) — see its doc comment for the fuller "why".
    /// </summary>
    /// <summary>
    /// A clip "exists" when its bytes are on the server, registered in the user's local folder
    /// (.client.json), or hosted by the provider (sidecar carries source_url / file_id). The last
    /// case is the normal state now that the server keeps no MP4 after generation — without it a
    /// generated-but-not-yet-saved clip would be regenerated (and paid for) as "missing".
    /// </summary>
    internal static bool ClipPresentOnServerOrClient(string mp4Path) =>
        (File.Exists(mp4Path) && new FileInfo(mp4Path).Length >= 1024) ||
        File.Exists(mp4Path + ".client.json") ||
        SidecarHasProviderSource(mp4Path);

    internal static bool SidecarHasProviderSource(string mp4Path) =>
        ClipProviderSource.ReadForMp4(mp4Path)?.HasProviderCopy == true;

    private static bool ShouldRefreshArtifactIndex(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        return kind is
            "remux" or
            "gen-scene" or
            "gen-batch" or
            "clip-auto-review" or
            "clip-auto-review-batch" or
            KindStage2 or
            "character-variants";
    }

    private async Task TryRefreshArtifactIndexAsync(string projectId)
    {
        try
        {
            var doc = await _artifactIndex.RebuildAsync(projectId).ConfigureAwait(false);
            await AppendLogAsync(
                $"  [Artifacts] map updated — readyForManualFinalReview={doc.ReadyForManualFinalReview} " +
                $"(ARTIFACTS.md, artifact_index.json, telemetry snapshots)");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Artifact index rebuild skipped for {ProjectId}", projectId);
            await AppendLogAsync($"  [Artifacts] map refresh skipped: {ex.Message}");
        }
    }

    private async Task PublishAsync()
    {
        if (_sink is null) return;
        var run = CurrentRun.Value;
        if (run is null) return;
        await _sink.OnJobUpdatedAsync(Clone(run.Snapshot));
    }

    private static JobSnapshot Clone(JobSnapshot s) => new()
    {
        JobId = s.JobId,
        Status = s.Status,
        Kind = s.Kind,
        Message = s.Message,
        ProjectId = s.ProjectId,
        UserId = s.UserId,
        CharKey = s.CharKey,
        Scene = s.Scene,
        Clip = s.Clip,
        Index = s.Index,
        Total = s.Total,
        Log = s.Log.ToList(),
        Error = s.Error,
        QueuedAt = s.QueuedAt,
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
    };
}

internal sealed class JobRunState
{
    public JobSnapshot Snapshot { get; set; } = new() { Status = "idle" };
    public string? ActiveJobId { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
    public string UserId { get; set; } = "local";
    public string? ApiKey { get; set; }
    public string? KeyMode { get; set; }
    public string? KeyUserId { get; set; }
    /// <summary>H2 — optional take trigger for video cost events on this run.</summary>
    public string? TakeTrigger { get; set; }
    public bool OnlyMissing { get; set; } = true;
    public string? GeminiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }
    public string? FalApiKey { get; set; }
    public string? SunoApiKey { get; set; }
    public string? AiMusicApiKey { get; set; }
    public string? ElevenLabsApiKey { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public List<string> HeldLocks { get; set; } = new();
    public List<string> PendingLockResources { get; set; } = new();
    public string? LockReason { get; set; }
    public SemaphoreSlim SnapLock { get; } = new(1, 1);
}
