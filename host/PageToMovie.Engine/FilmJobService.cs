using System.Collections.Concurrent;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelBacked;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly EditLogService _editLogs;
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
        VoiceAlignmentStore? voiceAlignment = null,
        IVideoEditClient? videoEdit = null,
        CastFromScreenplayService? castExtract = null)
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
        _editLogs = editLogs;
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
        {
            var n = CancelOneJob(jobId!) ? 1 : 0;
            return Task.FromResult(n);
        }

        // Refuse unscoped bulk cancel — callers must pass userId or cancelAllUsers.
        if (!cancelAllUsers && string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(0);

        var cancelled = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        // CTS entries that might lack a store row (edge case)
        foreach (var kv in _jobCts.ToArray())
        {
            if (!seen.Add(kv.Key))
                continue;
            var rec = _jobs.Get(kv.Key);
            if (!IsInBulkCancelScope(rec?.UserId, userId, cancelAllUsers))
                continue;
            if (CancelOneJob(kv.Key))
                cancelled++;
        }

        return Task.FromResult(cancelled);
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
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase);

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
        // Soft gate: running at global max still allows queue until per-user max;
        // worker pool will wait for a slot. Reject only when user queue is full.
        if (!string.IsNullOrWhiteSpace(userId) &&
            _jobs.CountQueuedForUser(userId!) >= Math.Max(1, cap.MaxQueuePerUser))
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

    private JobSnapshot Snapshot
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
        snapshot.Status = "running";
        snapshot.Index = 0;
        snapshot.Total = 100;
        snapshot.StartedAt = DateTimeOffset.UtcNow;
        snapshot.Log = new List<string>();
        Snapshot = snapshot;
        RegisterActiveJob();
        await PublishAsync();
    }

    private string? ActiveJobId
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
        Snapshot.Status = "running";
        run.StartedAt = Snapshot.StartedAt;

        if (!string.IsNullOrWhiteSpace(run.ActiveJobId))
        {
            // Promote existing queued → running
            Snapshot.JobId = run.ActiveJobId;
            _jobs.Update(run.ActiveJobId, rec =>
            {
                rec.Status = "running";
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
    private Task<JobSnapshot> StartBackgroundJobAsync(
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

        // Hard reject only when client asks FailIfLocked and lock is held by someone else
        if (failIfLocked)
        {
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

        var apiKey = !string.IsNullOrWhiteSpace(_user.RequestApiKey)
            ? _user.RequestApiKey
            : _keys.GetKey(userId, "grok");
        var geminiKey = _keys.GetKey(userId, "gemini");
        var anthropicKey = _keys.GetKey(userId, "anthropic");
        var falKey = _keys.GetKey(userId, "fal");
        var sunoKey = _keys.GetKey(userId, "suno");
        var aiMusicApiKey = _keys.GetKey(userId, "aimusicapi");
        var elevenLabsKey = _keys.GetKey(userId, "elevenlabs");

        var queuedAt = DateTimeOffset.UtcNow;
        var cts = new CancellationTokenSource();
        var kind = meta.Kind ?? "job";
        var rec = _jobs.Create(new JobRecord
        {
            Status = "queued",
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
            ApiKey = apiKey,
            GeminiApiKey = geminiKey,
            AnthropicApiKey = anthropicKey,
            FalApiKey = falKey,
            SunoApiKey = sunoKey,
            AiMusicApiKey = aiMusicApiKey,
            ElevenLabsApiKey = elevenLabsKey,
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

        _ = Task.Run(async () =>
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
                    await WaitForLocksAsync(run, cts.Token);

                    await UpdateQueuedMessageAsync(run, "Waiting for worker slot…");

                    async Task RunWorkAsync(CancellationToken ct)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token, ct);
                        // Bind api_calls telemetry to this job's project for the async flow
                        using var tel = !string.IsNullOrWhiteSpace(meta.ProjectId)
                            ? _telemetry.UseProject(meta.ProjectId!)
                            : null;
                        await work(linked.Token);
                    }

                    await _apiPool.RunAsync(userId, RunWorkAsync, run.Cts.Token);

                    var status = CurrentRun.Value?.Snapshot.Status;
                    success = string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (CurrentRun.Value?.Snapshot is { } s &&
                            !string.Equals(s.Status, "cancelled", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(s.Status, "done", StringComparison.OrdinalIgnoreCase))
                        {
                            await FinishAsync("cancelled", "Cancelled by user");
                        }
                    }
                    catch { /* ignore */ }
                }
                catch (LockConflictException ex)
                {
                    _metrics.NoteLockConflict();
                    try
                    {
                        await FinishAsync("error", ex.Message, ex.Message);
                    }
                    catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Background job failed");
                    try
                    {
                        if (CurrentRun.Value?.Snapshot is { } s &&
                            (string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(s.Status, "queued", StringComparison.OrdinalIgnoreCase)))
                        {
                            await FinishAsync("error", ex.Message, ex.Message);
                        }
                    }
                    catch { /* ignore */ }
                }
                finally
                {
                    var kindDone = CurrentRun.Value?.Snapshot.Kind ?? kind;
                    var q = run.QueuedAt;
                    var st = run.StartedAt ?? startedAt;
                    var snapStatus = CurrentRun.Value?.Snapshot.Status;
                    if (string.Equals(snapStatus, "done", StringComparison.OrdinalIgnoreCase))
                        success = true;
                    else if (string.Equals(snapStatus, "error", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(snapStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
                        success = false;

                    _metrics.NoteJobFinished(kindDone, userId, success, q, st);

                    foreach (var res in run.HeldLocks)
                        _locks.Release(res, userId);

                    if (!string.IsNullOrWhiteSpace(run.ActiveJobId))
                    {
                        _jobCts.TryRemove(run.ActiveJobId, out _);
                        _locks.ReleaseAllForJob(run.ActiveJobId);
                    }

                    CurrentRun.Value = null;
                }
            }
        }, CancellationToken.None);

        return Task.FromResult(rec.ToSnapshot());
    }

    private async Task WaitForLocksAsync(JobRunState run, CancellationToken ct)
    {
        var resources = run.PendingLockResources;
        if (resources.Count == 0)
            return;

        await UpdateQueuedMessageAsync(run, "Waiting for resource lock…");

        while (!ct.IsCancellationRequested)
        {
            // Cancelled while queued?
            var job = !string.IsNullOrEmpty(run.ActiveJobId) ? _jobs.Get(run.ActiveJobId) : null;
            if (job is not null &&
                string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException("Job cancelled");
            }

            var acquired = new List<string>();
            string? blockedResource = null;
            string? blockedOwner = null;
            foreach (var res in resources)
            {
                if (_locks.TryAcquire(res, run.UserId, DefaultLockTtl, run.LockReason, run.ActiveJobId))
                {
                    acquired.Add(res);
                    continue;
                }

                var holder = _locks.Get(res);
                if (holder is not null &&
                    string.Equals(holder.UserId, run.UserId, StringComparison.OrdinalIgnoreCase))
                {
                    // Already ours
                    acquired.Add(res);
                    continue;
                }

                blockedResource = res;
                blockedOwner = holder?.UserId;
                break;
            }

            if (blockedResource is null)
            {
                run.HeldLocks = acquired;
                await UpdateQueuedMessageAsync(run, "Lock acquired — waiting for worker…");
                return;
            }

            foreach (var a in acquired)
                _locks.Release(a, run.UserId);

            var msg = string.IsNullOrEmpty(blockedOwner)
                ? $"Waiting for lock {blockedResource}…"
                : $"Waiting for lock (held by {blockedOwner})…";
            await UpdateQueuedMessageAsync(run, msg);
            await Task.Delay(300, ct);
        }

        throw new OperationCanceledException("Cancelled while waiting for lock");
    }

    private async Task UpdateQueuedMessageAsync(JobRunState run, string message)
    {
        if (string.IsNullOrEmpty(run.ActiveJobId)) return;
        run.Snapshot.Message = message;
        run.Snapshot.Status = "queued";
        if (run.Snapshot.Log.Count == 0 || run.Snapshot.Log[^1] != message)
        {
            run.Snapshot.Log.Add(message);
            if (run.Snapshot.Log.Count > 120)
                run.Snapshot.Log = run.Snapshot.Log.TakeLast(120).ToList();
        }
        _jobs.Update(run.ActiveJobId, rec =>
        {
            if (string.Equals(rec.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                return;
            rec.Status = "queued";
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
            ? req.Clips!.Select(c => c.Scene)
            : req.Scenes ?? new List<int>();
        var locks = sceneNumbers
            .Where(s => s > 0)
            .Select(s => LockKeys.Scene(projectId, s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var queuedMsg = hasClips
            ? $"Queued batch gen ({req.Clips!.Count} clip(s))…"
            : $"Queued batch gen ({req.Scenes!.Count} scenes)…";
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
    /// "projectId required" when neither is set — and default a blank character key to the
    /// narrator pseudo-character. Shared prologue for voice/speak job starts.
    /// </summary>
    private (string projectId, string charKey) ResolveProjectAndCharKey(string? reqProjectId, string? reqCharKey)
    {
        if (string.IsNullOrWhiteSpace(reqProjectId) && string.IsNullOrWhiteSpace(_projects.ActiveProjectId))
            throw new InvalidOperationException("projectId required");
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
                Kind = "stage2",
                ProjectId = projectId,
                Message = "Queued Stage 2…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "stage2");
    }

    /// <summary>C# PDF extract + optional Grok vision OCR → book_full.txt (prepare only).</summary>
    public Task<JobSnapshot> StartBookPrepareAsync(StartBookPrepareRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException("projectId required");
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
            throw new InvalidOperationException("projectId required");
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

    private sealed class JobRunState
    {
        public JobSnapshot Snapshot { get; set; } = new() { Status = "idle" };
        public string? ActiveJobId { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();
        public string UserId { get; set; } = "local";
        public string? ApiKey { get; set; }
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

    private async Task RunBookPrepareAsync(StartBookPrepareRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        await _projects.RequireProjectAsync(projectId, ct);
        Snapshot = new JobSnapshot
        {
            Status = "running",
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
            await FinishAsync(result.Ok ? "done" : "error", msg, result.Ok ? null : msg);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Book prepare failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunBookImportAsync(StartBookImportRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);

        // Progress: 0–4 prepare, 5–10 adapt (chunk messages bump index)
        Snapshot = new JobSnapshot
        {
            Status = "running",
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
            var keyHint = !string.IsNullOrWhiteSpace(ApiKeyScope.Current)
                ? "personal/scope"
                : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"))
                    ? "server XAI_API_KEY env"
                    : "none";
            await AppendLogAsync($"AI key source for import: {keyHint}").ConfigureAwait(false);

            if (!_chat.IsConfigured)
            {
                await FinishAsync("error",
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
            if (needPrepare && File.Exists(bookPath) && !req.ForceExtract && !req.ForceVision)
            {
                // Light skip if text already good and not forcing — still run prepare for PDF path consistency
                // Import always sets ForceExtract=true for PDF; SkipPrepare for re-draft only.
            }

            if (needPrepare)
            {
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
                        _ = UpdateAsync(s =>
                        {
                            s.Message = line;
                            if (line.Contains("Extract", StringComparison.OrdinalIgnoreCase))
                                s.Index = Math.Max(s.Index, 2);
                            else if (line.Contains("Vision", StringComparison.OrdinalIgnoreCase) ||
                                     line.Contains("page", StringComparison.OrdinalIgnoreCase))
                                s.Index = Math.Max(s.Index, 3);
                            else
                                s.Index = Math.Max(s.Index, 2);
                        });
                    },
                    ct: ct).ConfigureAwait(false);

                if (!prep.Ok)
                {
                    await FinishAsync("error", prep.StrategyReason ?? "Book prepare failed",
                        prep.StrategyReason ?? "Book prepare failed").ConfigureAwait(false);
                    return;
                }

                await AppendLogAsync(
                    $"Book text ready · {prep.TextWords} words · {prep.TextEngine}").ConfigureAwait(false);
            }
            else
            {
                await AppendLogAsync("Skipping prepare — using existing book text").ConfigureAwait(false);
            }

            if (!File.Exists(bookPath))
            {
                await FinishAsync("error", "No book text after prepare",
                    "No book text after prepare").ConfigureAwait(false);
                return;
            }

            await UpdateAsync(s =>
            {
                s.Index = 5;
                s.Message = "Writing screenplay draft…";
            }).ConfigureAwait(false);
            await AppendLogAsync("Phase 2: book → Fountain screenplay").ConfigureAwait(false);

            if (!_chat.IsConfigured)
            {
                await FinishAsync("error", "Chat service not configured",
                    "Chat service not configured").ConfigureAwait(false);
                return;
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
                    _ = UpdateAsync(s =>
                    {
                        s.Message = line;
                        // Map adapt progress into 5–9
                        if (line.Contains("chunk", StringComparison.OrdinalIgnoreCase))
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(
                                line, @"(\d+)\s*/\s*(\d+)");
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
                        else if (line.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("Stitch", StringComparison.OrdinalIgnoreCase))
                            s.Index = Math.Max(s.Index, 9);
                        else if (line.Contains("repair", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("retry", StringComparison.OrdinalIgnoreCase))
                            s.Index = Math.Max(s.Index, 8);
                        else
                            s.Index = Math.Max(s.Index, 6);
                    });
                },
                ct: ct,
                errorLogger: _errorLogger,
                jobId: Snapshot.JobId,
                bookRegistry: _bookRegistry,
                cacheUserId: _user.UserId,
                bookFileSessionFactory: _bookFileSessionFactory).ConfigureAwait(false);

            if (!save.Ok)
            {
                await FinishAsync("error", save.Error ?? "Screenplay draft failed",
                    save.Error ?? "Screenplay draft failed").ConfigureAwait(false);
                return;
            }

            await UpdateAsync(s => s.Index = 10).ConfigureAwait(false);
            await FinishAsync(
                "done",
                save.Message ?? "Screenplay draft ready — review and approve").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Book import failed");
            await FinishAsync("error", ex.Message, ex.Message).ConfigureAwait(false);
        }
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

    /// <summary>Background music (or singing) for one scene via audio API (client saves the
    /// segment(s)). <paramref name="model"/> overrides the project's configured audio_model_name for
    /// this run only; <paramref name="isVocal"/> requests sung vocals (Suno-family models only).</summary>
    public Task<JobSnapshot> StartSceneMusicGenAsync(string projectId, int scene, string? model = null, bool isVocal = false)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            projectId = _projects.ActiveProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("projectId required");
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
            if (!_audio.IsConfigured)
            {
                await FinishAsync("error", "Audio synthesis API key missing.", "Audio synthesis API key missing.");
                return;
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
                await FinishAsync("done", "Background music disabled in settings.");
                return;
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
            var segmentCount = (int)Math.Ceiling(totalDuration / (double)segLen);
            var savedSegments = 0;
            var segmentFileNames = new List<string>();
            string? lastProviderNote = null;

            for (var seg = 1; seg <= segmentCount; seg++)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = totalDuration - (seg - 1) * segLen;
                var segDuration = Math.Clamp(remaining, 1, segLen);

                await AppendLogAsync($"  [{entry.DisplayName}] generating segment {seg}/{segmentCount} ({segDuration}s)…");
                var url = await _audio.GenerateMusicTrackAsync(
                    prompt, segDuration, entry.Id, ct,
                    onProgress: msg => { lastProviderNote = msg; _ = AppendLogAsync("  " + msg); },
                    isVocal: effectiveIsVocal, lyrics: lyrics).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(url))
                {
                    await AppendLogAsync($"  [{entry.DisplayName}] segment {seg} failed — stopping.");
                    break;
                }

                var relPath = MediaRegistryService.MusicSegmentRelativePath(scene, seg);
                var ticket = _mediaProxy.Issue(url, TimeSpan.FromMinutes(45));
                var clientUrl = $"/api/media/proxy/{ticket}";
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

            if (savedSegments == 0)
            {
                // Name the resolved provider — the top-level _audio.IsConfigured gate only checks
                // that *some* audio provider has a key, not that the one audio_model_name actually
                // routes to (MultiProviderAudioClient) does. A key set for a different provider than
                // the configured audio_model_name fails every scene this way, silently otherwise.
                // Surface the provider's real reason instead of a generic "synthesis failed": the
                // audio client sends its HTTP error via onProgress before returning null.
                var providerDetail = !string.IsNullOrWhiteSpace(lastProviderNote)
                    && (lastProviderNote.Contains("fail", StringComparison.OrdinalIgnoreCase)
                        || lastProviderNote.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || lastProviderNote.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
                        || lastProviderNote.Contains("key", StringComparison.OrdinalIgnoreCase))
                    ? $" {entry.DisplayName} said: “{lastProviderNote.Trim()}”."
                    : "";
                await FinishAsync("error",
                    $"No music came back from {entry.DisplayName}.{providerDetail} " +
                    "Most likely its API key isn’t configured — the audio gate only checks that some " +
                    "provider has a key, not the one this model uses. Add its key in Configuration, or pick a different audio model.",
                    $"Music synthesis failed for all segments via {entry.DisplayName} ({entry.Id}).{providerDetail}");
                return;
            }

            if (_musicSidecars is not null)
            {
                try
                {
                    await _musicSidecars.WriteActiveSidecarAsync(
                        pDir, scene, takeId, entry.Id, effectiveIsVocal, prompt, lyrics, segmentFileNames, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed writing music take sidecar for scene {Scene}", scene);
                }
            }

            await UpdateAsync(s => s.Index = 100);
            await FinishAsync("done", $"{(effectiveIsVocal ? "Singing" : "Background music")} ready ({savedSegments} segment(s)) — save to media folder");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scene music gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
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
            throw new InvalidOperationException("projectId required");

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
                "done",
                $"Review ready S{req.Scene:D2}C{req.Clip:D2} — {draft.Suggestion} ({draft.Suggestions.Count} suggestions)");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Clip review cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clip auto-review failed S{Scene}C{Clip}", req.Scene, req.Clip);
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunClipAutoReviewBatchAsync(StartClipAutoReviewBatchRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        var coords = _reviewIndex.ListOnDiskClipCoords(projectId, req.Scene)
            .Where(c => !req.OnlyMissing || !_reviewIndex.HasDraft(projectId, c.Scene, c.Clip))
            .ToList();

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "clip-auto-review-batch",
            ProjectId = projectId,
            Scene = req.Scene is int s0 && s0 > 0 ? s0 : null,
            Message = coords.Count == 0
                ? "No clips to auto-review"
                : $"Batch reviewing {coords.Count} clip(s)…",
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
                try { await _reviewIndex.RebuildAsync(projectId, req.Scene, ct); } catch { /* non-fatal */ }
                await FinishAsync("done", "Batch auto-review: nothing to do (no missing drafts)");
                return;
            }

            await AppendLogAsync(
                $"Batch auto-review: {coords.Count} clip(s)" +
                (req.OnlyMissing ? " (only missing drafts)" : " (all)") +
                (req.Scene is int sn && sn > 0 ? $" scene S{sn:D2}" : ""));

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
                    ok++;
                    await AppendLogAsync(
                        $"  → {draft.Suggestion}/{draft.Category} · {draft.Suggestions.Count} suggestion(s)");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogWarning(ex, "Batch auto-review failed S{Scene}C{Clip}", scene, clip);
                    await AppendLogAsync($"  → ERROR: {ex.Message}");
                }
            }

            try
            {
                var index = await _reviewIndex.RebuildAsync(projectId, req.Scene, ct: ct);
                await AppendLogAsync(
                    $"Review index rebuilt: {index.Clips.Count} row(s) → assets/review/index.json");
            }
            catch (Exception ex)
            {
                await AppendLogAsync($"Review index rebuild skipped: {ex.Message}");
            }

            await FinishAsync(
                "done",
                $"Batch auto-review done: {ok} ok, {failed} failed of {coords.Count}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Batch auto-review cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch auto-review failed for {ProjectId}", projectId);
            await FinishAsync("error", ex.Message, ex.Message);
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
            await FinishAsync("done", $"Voice sample ready for {req.CharKey}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Voice sample cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voice preview failed for {Char}", req.CharKey);
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    /// <summary>
    /// Grok vision: classify book images → which characters appear, write plates to scenes.json.
    /// Cancellable. Falls back to heuristics if no API key.
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
            Status = "running",
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
                await FinishAsync("error", result.Error ?? "Cast extract failed", result.Error).ConfigureAwait(false);
                return;
            }

            var n = result.CharacterCount;
            var msg = $"Cast ready · {n} character(s)"
                      + (result.CharacterKeys is { Count: > 0 }
                          ? " — " + string.Join(", ", result.CharacterKeys.Take(12))
                          : "");
            await FinishAsync("done", msg).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync("cancelled", "Cast extract cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cast extract failed for {Project}", projectId);
            await FinishAsync("error", ex.Message, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task RunSortCharacterPlatesAsync(AttachCharacterPlatesRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "character-plates",
            ProjectId = projectId,
            Message = req.UseGrok
                ? "Sorting book images onto characters with Grok vision…"
                : "Sorting book images onto characters (heuristic)…",
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
                req.UseGrok
                    ? "Character plate sort (Grok vision classifies who appears on each page)"
                    : "Character plate sort (heuristic only)");

            var result = await _plates.AttachAsync(
                projectId,
                force: true, // job is always an explicit re-sort from UI
                copyIntoAssets: req.CopyIntoAssets,
                onlyCharKey: req.CharKey,
                useGrok: req.UseGrok,
                visionModel: await ResolveVisionModelAsync(projectId, req.VisionModel, ct).ConfigureAwait(false),
                maxImages: req.MaxImages > 0 ? req.MaxImages : 32,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    // "Grok vision 3/20: …"
                    var m = System.Text.RegularExpressions.Regex.Match(
                        line, @"Grok vision\s+(\d+)/(\d+)",
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
                await FinishAsync("done", $"Already sorted ({result.SortedAt})");
                return;
            }

            if (!result.Ok && !string.IsNullOrEmpty(result.Reason))
            {
                await FinishAsync("error", result.Reason, result.Reason);
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
                "done",
                $"Plates sorted ({result.Method}): {result.CharactersUpdated} character(s) updated");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character plate sort failed");
            await FinishAsync("error", ex.Message, ex.Message);
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
                    await ResolveLockImagePathAsync(projectId, imagePath!, ct).ConfigureAwait(false),
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
            Status = "running",
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
            var videoDir = Path.Combine(projectDir, "assets", "video");
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

            await FinishAsync("done", "Edited clip ready — saved as a new take.");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Video edit failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunLocationVariantsAsync(StartLocationVariantsRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
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
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"saved variant (\d+)/(\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var idx))
                        _ = UpdateAsync(s => { s.Index = idx; s.Message = line; });
                    else if (line.Contains("generating", StringComparison.OrdinalIgnoreCase))
                    {
                        var g = System.Text.RegularExpressions.Regex.Match(line, @"generating\s+(\d+)");
                        if (g.Success && int.TryParse(g.Groups[1].Value, out var total) && total > 0)
                            _ = UpdateAsync(s => { s.Total = total; s.Message = line; });
                        else
                            _ = UpdateAsync(s => s.Message = line);
                    }
                    else
                        _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            await UpdateAsync(s =>
            {
                s.Index = result.Paths.Count;
                s.Total = Math.Max(s.Total, result.Paths.Count);
            });
            await AppendLogAsync($"mode={result.Mode} · {result.Paths.Count} file(s)");
            await FinishAsync(
                "done",
                $"Set plates ready for {req.LocKey} ({result.Mode}, {result.Paths.Count} image(s))");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Location variants failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunCharacterVariantsAsync(StartCharacterVariantsRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "character",
            ProjectId = projectId,
            CharKey = req.CharKey,
            Message = $"Generating portraits for {req.CharKey}…",
            Index = 0,
            Total = 3,
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
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    var idx = TryParseVariantProgress(line);
                    if (idx > 0)
                        _ = UpdateAsync(s => { s.Index = idx; s.Message = line; });
                    else if (line.Contains("generating", StringComparison.OrdinalIgnoreCase))
                    {
                        // "generating 1 variant(s)" / "generating 3 variants"
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"generating\s+(\d+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var total) && total > 0)
                            _ = UpdateAsync(s => { s.Total = total; s.Message = line; });
                        else
                            _ = UpdateAsync(s => s.Message = line);
                    }
                    else if (line.Contains("edit variant", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Grok", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("book ref", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("ref image", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s =>
                        {
                            s.Index = Math.Max(s.Index, 1);
                            s.Message = line;
                        });
                },
                ct: ct);

            await UpdateAsync(s =>
            {
                s.Index = result.Paths.Count;
                s.Total = Math.Max(s.Total, result.Paths.Count);
            });
            await AppendLogAsync(
                $"mode={result.Mode} · {result.Paths.Count} file(s)" +
                (result.BookRefs.Count > 0
                    ? $" · book refs: {string.Join(", ", result.BookRefs)}"
                    : ""));
            await FinishAsync(
                "done",
                $"Variants ready for {req.CharKey} ({result.Mode}, {result.Paths.Count} image(s))");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character variants failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private static int TryParseVariantProgress(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            line, @"variant[_\s-]*0*([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return n;
        m = System.Text.RegularExpressions.Regex.Match(line, @"\b([1-3])\s*/\s*3\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        m = System.Text.RegularExpressions.Regex.Match(
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
            Status = "running",
            Kind = "stage1",
            ProjectId = projectId,
            Message = "Building screenplay…",
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
            await FinishAsync(result.Ok || result.SceneCount > 0 ? "done" : "error", msg,
                result.Ok ? null : string.Join("; ", result.HardErrors.Take(3)));
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 1 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunStage2Async(StartStage2Request req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "stage2",
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
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s =>
                    {
                        s.Message = line;
                        s.Total = Math.Max(s.Total, 10);
                        // "Planning N scene(s)" / "Scene N…" — map into 1–9
                        var mPlan = System.Text.RegularExpressions.Regex.Match(
                            line, @"Planning\s+(\d+)\s+scene", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (mPlan.Success && int.TryParse(mPlan.Groups[1].Value, out var nScenes) && nScenes > 0)
                        {
                            s.Index = Math.Max(s.Index, 1);
                            return;
                        }
                        var mSc = System.Text.RegularExpressions.Regex.Match(
                            line, @"Scene\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (mSc.Success && int.TryParse(mSc.Groups[1].Value, out var sn) && sn > 0)
                        {
                            // Approximate: scene numbers climb; keep under 9 until merge/done
                            s.Index = Math.Max(s.Index, Math.Min(8, 1 + sn));
                            return;
                        }
                        if (line.Contains("Merged", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Backed up", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("complete", StringComparison.OrdinalIgnoreCase))
                            s.Index = Math.Max(s.Index, 9);
                        else
                            s.Index = Math.Max(s.Index, 1);
                    });
                },
                ct: ct);

            await FinishAsync(
                "done",
                $"Stage 2 complete: {result.SceneCount} scenes · {result.ClipCount} clips · ~{result.DurationSeconds}s");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 2 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }


    public Task<JobSnapshot> StartYouTubeUploadAsync(StartYouTubeUploadRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException("projectId required");
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
            var path = _projects.ResolveWipMoviePath(projectId);
            if (path is null || !File.Exists(path))
            {
                var pDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
                var altWip = Path.Combine(pDir, "assets", "video", "wip_movie.mp4");
                if (File.Exists(altWip)) path = altWip;
            }
            if (path is null || !File.Exists(path))
                throw new InvalidOperationException("No WIP movie file found on server — publish Demo from a browser stitch first.");

            // E: hash-gate exact upload bytes vs film_build.studio.sha256 (Clipchamp detection).
            byte[] uploadBytes;
            try
            {
                uploadBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not read WIP movie for hash gate: " + ex.Message, ex);
            }
            var publishGate = FilmBuildService.ApplyUploadHashGate(_projects, projectId, uploadBytes);
            await AppendLogAsync(
                $"Film publish path: {publishGate.Path} (upload sha {publishGate.UploadSha256[..Math.Min(12, publishGate.UploadSha256.Length)]}…)");

            var youtube = await _youTube.GetServiceAsync(ct)
                ?? throw new InvalidOperationException("YouTube is not connected — connect it from Review first.");

            var title = string.IsNullOrWhiteSpace(req.Title) ? $"{projectId} — WIP" : req.Title.Trim();
            var privacy = req.PrivacyStatus is "private" or "unlisted" or "public"
                ? req.PrivacyStatus
                : "unlisted";

            var video = new Video
            {
                Snippet = new VideoSnippet
                {
                    Title = title,
                    Description = req.Description ?? "",
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
            upload.ProgressChanged += p =>
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
            };

            var result = await upload.UploadAsync(ct);
            if (result.Status != UploadStatus.Completed || videoId is null)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                var errDetail = result.Exception is Google.GoogleApiException gerr
                    ? $"Google API {gerr.HttpStatusCode}: {gerr.Message} — {gerr.Error?.Message}"
                    : result.Exception?.Message ?? $"YouTube upload status: {result.Status}";
                await AppendLogAsync($"❌ YouTube upload failed: {errDetail}");
                throw result.Exception ?? new InvalidOperationException($"YouTube upload failed: {errDetail}");
            }

            var url = $"https://youtu.be/{videoId}";
            await _projects.SaveYouTubeUploadInfoAsync(projectId, new YouTubeUploadInfo
            {
                VideoId = videoId,
                Url = url,
                Title = title,
                PrivacyStatus = privacy,
                UploadedAt = DateTimeOffset.UtcNow,
            }, ct);

            // Re-record publish block with YouTube ids; create learning package when intact.
            try
            {
                var finalPublish = FilmBuildService.ApplyUploadHashGate(
                    _projects, projectId, uploadBytes,
                    youtubeVideoId: videoId, youtubeUrl: url);
                await AppendLogAsync($"Film build publish recorded ({finalPublish.Path}).");
                if (string.Equals(finalPublish.Path, FilmBuildPublish.PathStudioIntact, StringComparison.Ordinal))
                {
                    var lp = LearningPackageService.CreateFromProject(
                        _projects, projectId, workspaceRoot: TryFindWorkspaceRoot());
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

            // Best-effort cleanup of temporary staged MP4 to conserve server disk space
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to clean up temporary staged movie file {Path} after YouTube upload", path);
            }

            await FinishAsync("done", $"Uploaded to YouTube: {url}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "YouTube upload cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "YouTube upload failed for {ProjectId}", projectId);
            var errMessage = ex is Google.GoogleApiException gex
                ? $"Google API Error ({gex.HttpStatusCode}): {gex.Message} — {gex.Error?.Message}"
                : ex.Message;
            await AppendLogAsync($"❌ YouTube upload exception: {errMessage}");
            await FinishAsync("error", errMessage, errMessage);
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
            Status = "running",
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
                await FinishAsync("error", ctxErr ?? "Voice not configured", ctxErr ?? "Voice not configured")
                    .ConfigureAwait(false);
                return;
            }
            var entry = ctx.Entry;
            var providerId = ctx.ProviderId;

            var work = await BuildSpeakBatchWorkAsync(req, projectId, charKey, ct).ConfigureAwait(false);
            if (work.Count == 0)
            {
                await AppendLogAsync("Speak-batch: nothing to synthesize (only_missing or no dialogue).")
                    .ConfigureAwait(false);
                await FinishAsync("done", "No lines to speak").ConfigureAwait(false);
                return;
            }

            var maxParallel = Math.Clamp(req.MaxParallel <= 0 ? 3 : req.MaxParallel, 1, 8);
            var maxLen = ctx.MaxLen;

            await UpdateAsync(s =>
            {
                s.Total = work.Count;
                s.Index = 0;
                s.Message = $"Speak-batch: {work.Count} line(s) · parallel {maxParallel} · {providerId}";
            }).ConfigureAwait(false);
            await AppendLogAsync(Snapshot.Message!).ConfigureAwait(false);

            var done = 0;
            var failed = 0;
            var handoffGate = new SemaphoreSlim(1, 1);
            var gate = new SemaphoreSlim(maxParallel, maxParallel);

            async Task ProcessOneAsync(SpeakWorkItem item)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var text = item.Text.Trim();
                    if (text.Length == 0)
                    {
                        Interlocked.Increment(ref done);
                        return;
                    }
                    if (text.Length > maxLen)
                    {
                        await handoffGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await AppendLogAsync(
                                    $"  S{item.Scene:D2}C{item.Clip:D2}: text {text.Length} chars exceeds model limit {maxLen} — skip")
                                .ConfigureAwait(false);
                        }
                        finally { handoffGate.Release(); }
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref done);
                        return;
                    }

                    var (audioBytes, ext, err) = await SynthesizeLineAsync(
                        ctx, projectId, charKey, text, "speak_batch", ct).ConfigureAwait(false);

                    if (audioBytes is not { Length: > 0 })
                    {
                        await handoffGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await AppendLogAsync(
                                    $"  S{item.Scene:D2}C{item.Clip:D2}: fail — {err ?? "no audio"}")
                                .ConfigureAwait(false);
                        }
                        finally { handoffGate.Release(); }
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref done);
                        return;
                    }

                    var relPath = MediaRegistryService.RevoiceAudioRelativePath(item.Scene, item.Clip, ext);
                    var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
                    var absPath = Path.Combine(
                        projectDir,
                        relPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
                    await File.WriteAllBytesAsync(absPath, audioBytes, ct).ConfigureAwait(false);

                    // Ticket form used by GET /api/projects/{id}/media/file
                    var ticket = _mediaProxy.Issue($"{projectId}:{relPath}", TimeSpan.FromMinutes(45));
                    var clientUrl =
                        $"/api/projects/{Uri.EscapeDataString(projectId)}/media/file" +
                        $"?path={Uri.EscapeDataString(relPath)}&ticket={ticket}";

                    await handoffGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var idx = Interlocked.Increment(ref done);
                        await UpdateAsync(s =>
                        {
                            s.Index = idx;
                            s.Scene = item.Scene;
                            s.Clip = item.Clip;
                            s.ClientMediaUrl = clientUrl;
                            s.ClientRelativePath = relPath;
                            s.Message = $"Speak-batch: S{item.Scene:D2} C{item.Clip} ({idx}/{work.Count})…";
                        }).ConfigureAwait(false);
                        await AppendLogAsync(
                                $"  S{item.Scene:D2}C{item.Clip:D2}: ready → {relPath} ({audioBytes.Length / 1024} KB)")
                            .ConfigureAwait(false);
                    }
                    finally { handoffGate.Release(); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await handoffGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        await AppendLogAsync($"  S{item.Scene:D2}C{item.Clip:D2}: exception — {ex.Message}")
                            .ConfigureAwait(false);
                    }
                    finally { handoffGate.Release(); }
                    Interlocked.Increment(ref failed);
                    Interlocked.Increment(ref done);
                }
                finally
                {
                    gate.Release();
                }
            }

            var tasks = work.Select(ProcessOneAsync).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (failed == 0)
                await FinishAsync("done", $"Speak-batch complete — {work.Count} line(s)").ConfigureAwait(false);
            else if (failed >= work.Count)
                await FinishAsync("error", $"Speak-batch failed — all {failed} line(s) failed", "all failed")
                    .ConfigureAwait(false);
            else
                await FinishAsync(
                        "partial",
                        $"Speak-batch partial — {work.Count - failed} ok, {failed} failed")
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Speak-batch cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Speak-batch failed for {ProjectId}", projectId);
            await FinishAsync("error", ex.Message, ex.Message).ConfigureAwait(false);
        }
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
        var list = new List<SpeakWorkItem>();
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);

        // Explicit clips: text override or pull from blueprint
        if (req.Clips is { Count: > 0 })
        {
            using var bp = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
            foreach (var c in req.Clips.OrderBy(x => x.Scene).ThenBy(x => x.Clip))
            {
                if (c.Scene <= 0 || c.Clip <= 0) continue;
                var text = (c.Text ?? "").Trim();
                if (text.Length == 0 && bp is not null)
                    text = FindClipDialogue(bp.RootElement, c.Scene, c.Clip);
                text = ClipVideoPromptBuilder.SanitizeSpokenDialogue(text);
                if (text.Length == 0) continue;

                var rel = MediaRegistryService.RevoiceAudioRelativePath(c.Scene, c.Clip);
                if (req.OnlyMissing && File.Exists(Path.Combine(projectDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                    continue;

                list.Add(new SpeakWorkItem { Scene = c.Scene, Clip = c.Clip, Text = text });
            }
            return list;
        }

        // Auto: all blueprint clips (optionally narrator-only)
        using var blueprint = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
        if (blueprint is null)
            throw new InvalidOperationException(
                $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

        if (!blueprint.RootElement.TryGetProperty("scenes", out var scenesEl) ||
            scenesEl.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var s in scenesEl.EnumerateArray())
        {
            var sn = s.TryGetProperty("scene_number", out var snEl) && snEl.TryGetInt32(out var n) ? n : 0;
            if (sn <= 0) continue;
            if (!s.TryGetProperty("veo_clips", out var clipsEl) || clipsEl.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var c in clipsEl.EnumerateArray())
            {
                var cn = ClipKeying.ClipNumber(c);
                if (cn <= 0) continue;

                string? speaker = null;
                var dialogue = "";
                if (c.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object)
                {
                    if (ap.TryGetProperty("dialogue", out var d))
                        dialogue = d.GetString() ?? "";
                    if (ap.TryGetProperty("speaker", out var sp))
                        speaker = sp.GetString();
                }
                if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty("dialogue", out var rootD))
                    dialogue = rootD.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(speaker) && c.TryGetProperty("speaker", out var rootSp))
                    speaker = rootSp.GetString();

                dialogue = ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue);
                if (string.IsNullOrWhiteSpace(dialogue)) continue;

                if (req.NarratorOnly && !IsNarratorSpeaker(speaker, charKey))
                    continue;

                var rel = MediaRegistryService.RevoiceAudioRelativePath(sn, cn);
                if (req.OnlyMissing &&
                    File.Exists(Path.Combine(projectDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                    continue;

                list.Add(new SpeakWorkItem { Scene = sn, Clip = cn, Text = dialogue });
            }
        }

        return list.OrderBy(x => x.Scene).ThenBy(x => x.Clip).ToList();
    }

    private static bool IsNarratorSpeaker(string? speaker, string narratorKey) =>
        CastKindClassifier.IsNarratorSpeaker(speaker, narratorKey);

    private static string FindClipDialogue(JsonElement root, int scene, int clip)
    {
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var s in scenes.EnumerateArray())
        {
            var sn = s.TryGetProperty("scene_number", out var snEl) && snEl.TryGetInt32(out var n) ? n : 0;
            if (sn != scene) continue;
            if (!s.TryGetProperty("veo_clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
                return "";
            foreach (var c in clips.EnumerateArray())
            {
                var cn = ClipKeying.ClipNumber(c);
                if (cn != clip) continue;
                if (c.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object &&
                    ap.TryGetProperty("dialogue", out var d))
                    return d.GetString() ?? "";
                if (c.TryGetProperty("dialogue", out var rootD))
                    return rootD.GetString() ?? "";
            }
        }
        return "";
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
        if (string.IsNullOrWhiteSpace(model))
        {
            var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            EnsureLabModelsAllowed(cfg);
            if (cfg.TryGetValue("voice_model_name", out var vm) && vm.ValueKind == JsonValueKind.String)
                model = vm.GetString();
        }

        SupportedModelEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(model))
            entry = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                    ?? SupportedModelCatalog.Find(model);
        if (entry is { IsVoiceCloneStep: true })
        {
            entry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Enabled &&
                    string.Equals(m.ProviderId, entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            model = entry?.Id;
        }
        if (entry is null)
        {
            entry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Enabled &&
                    (string.IsNullOrWhiteSpace(seedProvider) ||
                     string.Equals(m.ProviderId, seedProvider, StringComparison.OrdinalIgnoreCase)));
            model = entry?.Id ?? model;
        }

        var providerId = entry?.ProviderId
                         ?? (string.IsNullOrWhiteSpace(seedProvider) ? null : seedProvider)
                         ?? "unknown";
        var useEleven = providerId.Equals("elevenlabs", StringComparison.OrdinalIgnoreCase)
                        || entry?.Provider == ModelProviderFamily.ElevenLabs
                        || voiceId!.StartsWith("mock_", StringComparison.OrdinalIgnoreCase);

        if (useEleven && !_voiceClient.IsConfigured && !voiceId!.StartsWith("mock_", StringComparison.OrdinalIgnoreCase))
            return (null, "ElevenLabs key is not configured.");
        if (!useEleven && !_voiceClone.IsConfigured)
            return (null, "Voice provider (Fal) is not configured.");

        var speakModelId = useEleven
            ? (entry?.Id
               ?? SupportedModelCatalog.Find("eleven_multilingual_v2", ModelCapability.Voice)?.Id
               ?? model ?? "eleven_multilingual_v2")
            : (entry?.Id
               ?? SupportedModelCatalog.Find("fal-ai/minimax/speech-02-hd", ModelCapability.Voice)?.Id
               ?? model ?? "");

        return (new SpeakContext
        {
            VoiceId = voiceId!,
            Entry = entry,
            ProviderId = providerId,
            UseEleven = useEleven,
            SpeakModelId = speakModelId ?? "",
            MaxLen = entry?.MaxPromptLength
                ?? throw new InvalidOperationException(
                    $"Model '{entry?.Id ?? "(null)"}' has no maxPromptLength in models_catalog.json."),
        }, null);
    }

    /// <summary>
    /// Synthesize one line of cloned-voice speech (ElevenLabs bytes or Fal url→download) and log the
    /// TTS telemetry. Returns the audio bytes + file extension (or an error). Keys stay on the server.
    /// </summary>
    private async Task<(byte[]? Audio, string Ext, string? Error)> SynthesizeLineAsync(
        SpeakContext ctx, string projectId, string charKey, string text, string mode, CancellationToken ct)
    {
        byte[]? audioBytes = null;
        string ext = ".mp3";
        string? err = null;

        if (ctx.UseEleven)
        {
            var tts = await _voiceClient.TextToSpeechAsync(ctx.VoiceId, text, ctx.SpeakModelId, ct)
                .ConfigureAwait(false);
            if (!tts.Ok || tts.AudioBytes is not { Length: > 0 })
                err = tts.Error ?? "TTS failed";
            else
            {
                audioBytes = tts.AudioBytes;
                ext = tts.FileExtension ?? ".mp3";
            }
        }
        else
        {
            var audioUrl = await _voiceClone.SynthesizeSpeechAsync(text, ctx.VoiceId, ctx.SpeakModelId, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(audioUrl))
                err = "Speech synthesis failed";
            else
            {
                try
                {
                    var http = _httpFactory.CreateClient();
                    using var resp = await http.GetAsync(audioUrl, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        audioBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                        var ctHeader = resp.Content.Headers.ContentType?.MediaType ?? "";
                        if (ctHeader.Contains("wav", StringComparison.OrdinalIgnoreCase))
                            ext = ".wav";
                        else if (ctHeader.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
                                 ctHeader.Contains("m4a", StringComparison.OrdinalIgnoreCase))
                            ext = ".m4a";
                        else
                            ext = ".mp3";
                    }
                    else
                        err = $"Download TTS failed ({(int)resp.StatusCode})";
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
            }
        }

        if (_telemetry is not null)
        {
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

        return (audioBytes, ext, err);
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
            Status = "running",
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
            if (_voiceAlignment is null)
            {
                await FinishAsync("error", "Voice alignment store unavailable.", "no alignment store")
                    .ConfigureAwait(false);
                return;
            }

            var (ctx, ctxErr) = await ResolveSpeakContextAsync(projectId, charKey, req.Model, ct).ConfigureAwait(false);
            if (ctx is null)
            {
                await FinishAsync("error", ctxErr ?? "Voice not configured", ctxErr ?? "Voice not configured")
                    .ConfigureAwait(false);
                return;
            }

            using var blueprint = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
            if (blueprint is null)
            {
                await FinishAsync("error", "No shot plan for this project yet.", "no blueprint")
                    .ConfigureAwait(false);
                return;
            }

            // Associate lines with speakers straight from the blueprint (not guesswork).
            Func<string, bool>? filter = req.NarratorOnly
                ? spk => IsNarratorSpeaker(spk, charKey)
                : null;
            var clipLines = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, filter);
            if (clipLines.Count == 0)
            {
                await FinishAsync("done", "No matching dialogue lines to substitute.").ConfigureAwait(false);
                return;
            }

            // Which scenes contain a non-narrator speaker (e.g. the mom) baked into the clip audio —
            // those keep their original audio; narrator-only scenes get muted + fully replaced by the
            // clone. Only meaningful under NarratorOnly (otherwise every speaker is being replaced).
            var scenesWithOtherSpeakers = new HashSet<int>();
            if (req.NarratorOnly)
            {
                foreach (var cl in VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, null))
                    if (cl.Lines.Any(l => !IsNarratorSpeaker(l.CharacterKey, charKey)))
                        scenesWithOtherSpeakers.Add(cl.Scene);
            }

            // Per-SCENE strategy: concatenate every narrator line in a scene into one continuous read
            // and synthesize it in a single TTS call, so the prosody flows across the whole scene
            // instead of restarting every clip. The browser overlays one track onto the stitched scene.
            var sceneGroups = clipLines
                .GroupBy(c => c.Scene)
                .OrderBy(g => g.Key)
                .ToList();

            var alignment = new ProjectVoiceAlignment
            {
                ProjectId = projectId,
                CharKey = charKey,
                SceneVoices = new List<SceneVoiceTrack>(sceneGroups.Count),
            };

            var maxLen = ctx.MaxLen;
            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var totalScenes = sceneGroups.Count;
            var totalLines = clipLines.Sum(c => c.Lines.Count);

            await UpdateAsync(s =>
            {
                s.Total = totalLines;
                s.Index = 0;
                s.Message = $"Voice substitution: {totalLines} line(s) across {totalScenes} scene(s) · {ctx.ProviderId}";
            }).ConfigureAwait(false);
            await AppendLogAsync(Snapshot.Message!).ConfigureAwait(false);

            var done = 0;
            var failed = 0;

            foreach (var group in sceneGroups)
            {
                ct.ThrowIfCancellationRequested();
                var sceneNo = group.Key;

                var track = new SceneVoiceTrack
                {
                    Scene = sceneNo,
                    HasOtherSpeakers = scenesWithOtherSpeakers.Contains(sceneNo),
                };

                // Each narrator line in the scene (clip order, then line order) is synthesized on its
                // own so the browser can place + time-stretch it onto the detected speech window.
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
                    var text = lineTextRaw;
                    if (text.Length > maxLen)
                    {
                        await AppendLogAsync(
                                $"  S{sceneNo:D2} L{lineNo:D2}: {text.Length} chars exceeds model limit {maxLen} — truncating.")
                            .ConfigureAwait(false);
                        text = text[..maxLen];
                    }

                    var svl = new SceneVoiceLine { Index = lineNo, Text = text };
                    var relPath = MediaRegistryService.RevoiceSceneLineAudioRelativePath(sceneNo, lineNo);
                    var absPath = Path.Combine(projectDir, relPath.Replace('/', Path.DirectorySeparatorChar));

                    if (req.OnlyMissing && File.Exists(absPath))
                    {
                        svl.VoiceAudioRelativePath = relPath;
                        track.Lines.Add(svl);
                        var idxSkip = Interlocked.Increment(ref done);
                        await UpdateAsync(s => { s.Index = idxSkip; s.Scene = sceneNo; }).ConfigureAwait(false);
                        await AppendLogAsync($"  S{sceneNo:D2} L{lineNo:D2}: reuse existing → {relPath}").ConfigureAwait(false);
                        lineNo++;
                        continue;
                    }

                    var (audioBytes, ext, err) = await SynthesizeLineAsync(
                        ctx, projectId, charKey, text, "voice_substitution", ct).ConfigureAwait(false);

                    if (audioBytes is not { Length: > 0 })
                    {
                        await AppendLogAsync($"  S{sceneNo:D2} L{lineNo:D2}: fail — {err ?? "no audio"}").ConfigureAwait(false);
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref done);
                        track.Lines.Add(svl);
                        lineNo++;
                        continue;
                    }

                    relPath = MediaRegistryService.RevoiceSceneLineAudioRelativePath(sceneNo, lineNo, ext);
                    absPath = Path.Combine(projectDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
                    await File.WriteAllBytesAsync(absPath, audioBytes, ct).ConfigureAwait(false);
                    svl.VoiceAudioRelativePath = relPath;
                    track.Lines.Add(svl);

                    var ticket = _mediaProxy.Issue($"{projectId}:{relPath}", TimeSpan.FromMinutes(45));
                    var clientUrl =
                        $"/api/projects/{Uri.EscapeDataString(projectId)}/media/file" +
                        $"?path={Uri.EscapeDataString(relPath)}&ticket={ticket}";

                    var idx = Interlocked.Increment(ref done);
                    await UpdateAsync(s =>
                    {
                        s.Index = idx;
                        s.Scene = sceneNo;
                        s.ClientMediaUrl = clientUrl;
                        s.ClientRelativePath = relPath;
                        s.Message = $"Voice substitution: S{sceneNo:D2} L{lineNo:D2} ({idx}/{totalLines})…";
                    }).ConfigureAwait(false);
                    await AppendLogAsync(
                            $"  S{sceneNo:D2} L{lineNo:D2}: ready → {relPath} ({audioBytes.Length / 1024} KB)")
                        .ConfigureAwait(false);
                    lineNo++;
                }

                alignment.SceneVoices.Add(track);
            }

            // Persist the alignment (per-scene voice tracks) as a project file.
            await _voiceAlignment.SaveAsync(projectId, alignment, ct).ConfigureAwait(false);
            await AppendLogAsync($"Alignment saved → {VoiceAlignmentStore.RelativePath}").ConfigureAwait(false);

            if (failed == 0)
                await FinishAsync("done", $"Voice substitution ready — {totalLines} line(s) across {totalScenes} scene(s)").ConfigureAwait(false);
            else if (failed >= totalLines)
                await FinishAsync("error", $"Voice substitution failed — all {failed} line(s) failed", "all failed")
                    .ConfigureAwait(false);
            else
                await FinishAsync(
                        "partial",
                        $"Voice substitution partial — {totalLines - failed} ok, {failed} failed")
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Voice substitution cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voice substitution failed for {ProjectId}", projectId);
            await FinishAsync("error", ex.Message, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task RunBatchGenAsync(StartBatchGenRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        var hasClips = req.Clips is { Count: > 0 };
        var scenes = (hasClips ? req.Clips!.Select(c => c.Scene) : req.Scenes)
            .Distinct().OrderBy(s => s).ToList();
        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "batch",
            ProjectId = projectId,
            Message = hasClips
                ? $"Batch: {req.Clips!.Count} clip(s)…"
                : $"Batch: {scenes.Count} scene(s)…",
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

            if (req.RequireLockedCharacters)
            {
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

            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(projectDir, "assets", "video"));

            // Pre-count work units
            var work = new List<(int Scene, int Clip, JsonElement ClipEl)>();
            if (hasClips)
            {
                // Explicit multi-select of specific clips — always force-regen (ignore OnlyMissing),
                // same as single-clip regen.
                foreach (var target in req.Clips!.OrderBy(c => c.Scene).ThenBy(c => c.Clip))
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
            }
            else
            {
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
                    if (!sceneEl.Value.TryGetProperty("veo_clips", out var clipsEl) ||
                        clipsEl.ValueKind != JsonValueKind.Array)
                    {
                        await AppendLogAsync($"Scene {sn}: no veo_clips — skip");
                        continue;
                    }

                    foreach (var c in clipsEl.EnumerateArray())
                    {
                        var cn = ClipKeying.ClipNumber(c);
                        if (cn <= 0) continue;
                        var path = Path.Combine(projectDir, "assets", "video", $"scene_{sn:D2}_clip_{cn:D2}.mp4");
                        var missing = !ClipPresentOnServerOrClient(path);
                        if (!req.OnlyMissing || missing)
                            work.Add((Scene: sn, Clip: cn, ClipEl: c.Clone()));
                    }
                }
            }

            if (work.Count == 0)
            {
                await AppendLogAsync("Batch: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            // Fail before any API spend if the selected video model cannot do multi-clip / plates.
            await EnsureVideoModelCapabilitiesAsync(
                    projectId,
                    needContinue: work.Any(w => w.Clip > 1),
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
            await AppendLogAsync(Snapshot.Message!);

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
                await AppendLogAsync(Snapshot.Message!);

                try
                {
                    // Previous clip element in same scene (for prompt context)
                    JsonElement? prevClipEl = null;
                    if (cn > 1)
                    {
                        var sceneEl = FindScene(bp.RootElement, sn);
                        if (sceneEl is not null)
                            prevClipEl = FindClipInScene(sceneEl.Value, cn - 1);
                    }

                    var prior = sceneCarryover.TryGetValue(sn, out var p) ? p : (LastClip: 0, PaddingSec: 0.0);
                    var incomingPadding = ResolveIncomingDurationPadding(cn, prior.LastClip, prior.PaddingSec);
                    var overrun = await GenerateOneClipAsync(
                        projectId, projectDir, sn, cn, clip, resolution, ct,
                        previousClipEl: prevClipEl,
                        blueprintRoot: bp.RootElement,
                        incomingDurationPaddingSec: incomingPadding,
                        modelOverride: req.VideoModel);
                    sceneCarryover[sn] = (cn, overrun);
                    done++;
                    // Fresh clips x/y + status pills while batch is still running.
                    _projects.InvalidateSceneListCache(projectId);
                    await AppendLogAsync($"Done S{sn:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    firstClipError ??= ex.Message;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", sn, cn);
                    await AppendLogAsync($"Failed S{sn:D2} C{cn}: {ex.Message}");
                }
            }

            var status = failed > 0 && done == 0 ? "error"
                : failed > 0 ? "partial"
                : "done";
            var msg = status switch
            {
                "error" => !string.IsNullOrWhiteSpace(firstClipError)
                    ? $"Batch failed: {firstClipError}"
                    : $"Batch failed ({failed} clip(s) failed, none ok)",
                "partial" => !string.IsNullOrWhiteSpace(firstClipError)
                    ? $"Batch partial ({done} ok, {failed} failed): {firstClipError}"
                    : $"Batch partial ({done} ok, {failed} failed)",
                _ => $"Batch finished ({done} clip(s))",
            };
            await FinishAsync(status, msg, failed > 0 ? msg : null);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunSceneGenAsync(StartSceneGenRequest req, string projectId, CancellationToken ct)
    {
        await _projects.RequireProjectAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
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

            // The end-credits card is rendered deterministically client-side (canvas -> ffmpeg.wasm),
            // never through the video model — a video model asked to render a text-heavy title card
            // hallucinates unrelated footage. The Scenes page already routes credits scenes elsewhere,
            // but any other caller of this endpoint must be stopped here too, before spending an API call.
            if (IsCreditsScene(sceneEl))
                throw new InvalidOperationException(
                    $"Scene {req.Scene} is the end-credits scene — it is rendered client-side, not through the video model.");

            if (req.RequireLockedCharacters)
            {
                EnsureCastReadyForVideo(projectId);
                EnsureSceneCharactersLocked(projectId, req.Scene);
            }

            if (!sceneEl.TryGetProperty("veo_clips", out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Scene {req.Scene} has no veo_clips.");
            }

            var clips = clipsEl.EnumerateArray().ToList();
            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var videoDir = Path.Combine(projectDir, "assets", "video");
            Directory.CreateDirectory(videoDir);

            var todo = new List<(int ClipNum, JsonElement Clip)>();
            foreach (var c in clips)
            {
                var cn = ClipKeying.ClipNumber(c);
                if (cn <= 0) continue;
                if (req.Clip is int onlyClip && onlyClip > 0 && cn != onlyClip)
                    continue;
                var path = Path.Combine(videoDir, $"scene_{req.Scene:D2}_clip_{cn:D2}.mp4");
                var missing = !ClipPresentOnServerOrClient(path);
                if (!req.OnlyMissing || missing)
                    todo.Add((cn, c.Clone()));
            }

            if (todo.Count == 0)
            {
                await AppendLogAsync($"Scene {req.Scene}: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            // Fail before any API spend if the selected video model cannot do multi-clip / plates.
            await EnsureVideoModelCapabilitiesAsync(
                    projectId,
                    needContinue: todo.Any(t => t.ClipNum > 1),
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

            // Admin-only quality gate: after dialogue QA fails, auto-regen up to qa_max_retries.
            var qaRetryOnFail = false;
            var qaMaxRetries = 1;
            try
            {
                var cfgMap = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
                if (cfgMap.TryGetValue("qa_retry_on_fail", out var qe))
                {
                    if (qe.ValueKind is JsonValueKind.True) qaRetryOnFail = true;
                    else if (qe.ValueKind is JsonValueKind.False) qaRetryOnFail = false;
                    else if (qe.ValueKind == JsonValueKind.String &&
                             bool.TryParse(qe.GetString(), out var qb))
                        qaRetryOnFail = qb;
                }
                else
                    qaRetryOnFail = true; // match Configuration default

                if (cfgMap.TryGetValue("qa_max_retries", out var qm) && qm.TryGetInt32(out var qmi))
                    qaMaxRetries = Math.Clamp(qmi, 0, 5);
            }
            catch { /* keep defaults */ }

            var adminQaRetry = qaRetryOnFail && _user.IsAdmin &&
                               _dialogueVerification is not null &&
                               _dialogueVerification.IsConfigured;
            if (qaRetryOnFail && !_user.IsAdmin)
                await AppendLogAsync("Quality gate retry is on, but auto-regen runs in admin mode only.");
            else if (adminQaRetry)
                await AppendLogAsync(
                    $"Admin quality gate retry ON (max {qaMaxRetries} re-gen(s) per clip on dialogue fail).");

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
                await AppendLogAsync(Snapshot.Message!);

                try
                {
                    JsonElement? prevClipEl = null;
                    if (cn > 1)
                    {
                        foreach (var (pcn, pclip) in todo)
                        {
                            if (pcn == cn - 1) { prevClipEl = pclip; break; }
                        }
                        // Also scan full scene clips for prev not in todo
                        if (prevClipEl is null)
                            prevClipEl = FindClipInScene(sceneEl, cn - 1);
                    }

                    var incomingPadding = ResolveIncomingDurationPadding(cn, lastGeneratedClipNum, carryoverPaddingSec);
                    carryoverPaddingSec = await GenerateOneClipAsync(
                        projectId, projectDir, req.Scene, cn, clip, resolution, ct,
                        previousClipEl: prevClipEl,
                        blueprintRoot: bp.RootElement,
                        incomingDurationPaddingSec: incomingPadding);

                    if (adminQaRetry && ClipHasSpokenAudio(clip))
                    {
                        for (var qaAttempt = 1; qaAttempt <= qaMaxRetries; qaAttempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            ClipDialogueVerificationResult? ver = null;
                            try
                            {
                                ver = await _dialogueVerification!
                                    .VerifyClipDialogueAsync(projectId, req.Scene, cn, force: true, ct: ct)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                await AppendLogAsync(
                                    $"  [QA] dialogue check failed to run S{req.Scene:D2}C{cn}: {ex.Message}");
                                break;
                            }

                            if (ver is null || !DialogueQaNeedsRegen(ver))
                            {
                                if (ver is not null)
                                    await AppendLogAsync(
                                        $"  [QA] S{req.Scene:D2}C{cn} ok ({ver.Status})");
                                break;
                            }

                            await AppendLogAsync(
                                $"  [QA] S{req.Scene:D2}C{cn} {ver.Status} — auto-regen {qaAttempt}/{qaMaxRetries} (admin)…");
                            try
                            {
                                await _learning.AppendAsync(new ReviewLearningEvent
                                {
                                    ProjectId = projectId,
                                    Type = "qa_auto_retry",
                                    Scene = req.Scene,
                                    Clip = cn,
                                    Note = ver.Status,
                                    Outcome = $"attempt_{qaAttempt}",
                                    JobId = Snapshot.JobId,
                                    ActionTaken = "admin_dialogue_qa_regen",
                                }).ConfigureAwait(false);
                            }
                            catch { /* non-fatal */ }

                            carryoverPaddingSec = await GenerateOneClipAsync(
                                projectId, projectDir, req.Scene, cn, clip, resolution, ct,
                                previousClipEl: prevClipEl,
                                blueprintRoot: bp.RootElement,
                                incomingDurationPaddingSec: incomingPadding);
                        }
                    }

                    lastGeneratedClipNum = cn;
                    done++;
                    // Fresh clips x/y + status pills while scene gen is still running.
                    _projects.InvalidateSceneListCache(projectId);
                    await AppendLogAsync($"Done S{req.Scene:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", req.Scene, cn);
                    await AppendLogAsync($"Failed S{req.Scene:D2} C{cn}: {ex.Message}");
                    // Full-scene sequential gen: later clips need previous on disk — stop after first fail.
                    // Single-clip regen (req.Clip set) keeps trying only that one clip (already filtered).
                    if (req.Clip is null or <= 0 && i + 1 < todo.Count)
                    {
                        await AppendLogAsync(
                            "Stopping scene gen after first clip failure " +
                            $"(remaining {todo.Count - i - 1} clip(s) need previous video).");
                        break;
                    }
                }
            }

            // partial = some clips ok, some failed (not "done" — remux/continue need a clear signal)
            var status = failed > 0 && done == 0 ? "error"
                : failed > 0 ? "partial"
                : "done";
            var msg = status switch
            {
                "error" => $"Scene gen failed ({failed} clip(s) failed, none ok)",
                "partial" => $"Scene gen partial ({done} ok, {failed} failed)",
                _ => $"Generation finished ({done} clip(s))",
            };
            await FinishAsync(status, msg, failed > 0 ? msg : null);

            // P0 learning: single-clip regen (typical after auto-review apply)
            if (req.Clip is int regenClip && regenClip > 0)
            {
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
                    }).ConfigureAwait(false);
                }
                catch { /* non-fatal */ }
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scene gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
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
            var videoDir = Path.GetDirectoryName(outPath)!;
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
        string? modelOverride = null)
    {
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var overrunSec = 0.0;

        // Previous clip in this scene — Imagine /videos/extensions continues from that video.
        // Cast-set changes reseed fresh+refs (PR2).
        string? prevVisual = null;
        string? prevVideoPath = null;
        var reseedFresh = false;
        var cont = clipEl.TryGetProperty("veo_continuation_source", out var ce)
            ? (ce.GetString() ?? "none")
            : "none";
        var wantContinue =
            string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase) ||
            clip > 1;

        var model = await ResolveVideoModelAsync(projectId, ct, modelOverride).ConfigureAwait(false);
        var modelEntry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video);

        // Real video-extend: the browser client prepares and uploads this file — its own copy of
        // the previous clip's display video, tail-trimmed to ≤ the model's max input length —
        // before requesting this clip (server has no native ffmpeg to do that trim itself, and
        // Grok's /videos/extensions rejects input video over its max). Presence is the only
        // signal; a missing file (client didn't prepare one, an older/manual regenerate, or a
        // model that doesn't support continue) always falls back to fresh gen + locked refs,
        // exactly as before this feature existed — never blocks clip generation.
        string? extendSourcePath = null;
        // maxExtensionSeconds is only consulted later for duration; continue eligibility is the bool.
        if (clip > 1 && modelEntry.SupportsVideoContinue)
        {
            var candidate = Path.Combine(
                projectDir, "assets", "video", $"_extend_src_s{scene:D2}c{clip:D2}.mp4");
            if (File.Exists(candidate) && new FileInfo(candidate).Length >= 1024)
                extendSourcePath = candidate;
        }
        if (extendSourcePath is not null)
            prevVideoPath = extendSourcePath;

        if (previousClipEl is { } prevEl &&
            prevEl.TryGetProperty("visual_prompt", out var pvp))
            prevVisual = pvp.GetString();

        if (prevVisual is null && wantContinue && blueprintRoot is { } root)
            prevVisual = FindClipVisualInBlueprint(root, scene, clip - 1);

        // PR2: reseed with locked refs when on-screen cast set changes (API drops refs on extend).
        string? extendInputTemp = extendSourcePath;
        try
        {
            if (prevVideoPath is not null && _opts.IdentityReseedOnCastChange)
            {
                var curKeys = ClipVideoPromptBuilder.ResolveOnScreenCharacterKeys(clipEl)
                    .Where(k => !(profiles.TryGetValue(k, out var cp) && cp.VoiceOnly))
                    .Select(k => k)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var prevKeys = previousClipEl is { } pe
                    ? ClipVideoPromptBuilder.ResolveOnScreenCharacterKeys(pe)
                        .Where(k => !(profiles.TryGetValue(k, out var pp) && pp.VoiceOnly))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();
                if (prevKeys.Count > 0 && !OnScreenSetsEqual(curKeys, prevKeys))
                {
                    reseedFresh = true;
                    await AppendLogAsync(
                        $"  [Identity] Cast set changed " +
                        $"[{string.Join(", ", prevKeys)}] → [{string.Join(", ", curKeys)}] — " +
                        "fresh gen with locked refs (not video-extend)");
                    prevVideoPath = null; // API: attach refs
                    // Keep prevVisual for continuity prose only
                }
            }

            // Silent → first spoken/VO: video-extend often clips the opening word (mouth stays closed
            // from the prior silent clip). Require prev on disk for order, but gen fresh + plates.
            if (prevVideoPath is not null)
            {
                JsonElement? prevMeta = previousClipEl;
                if (prevMeta is null && blueprintRoot is { } br)
                    prevMeta = FindClipElementInBlueprint(br, scene, clip - 1);
                if (prevMeta is { } pm && ClipHasSpokenAudio(clipEl) && !ClipHasSpokenAudio(pm))
                {
                    reseedFresh = true;
                    prevVideoPath = null;
                    await AppendLogAsync(
                        $"  [Speech] S{scene:D2}C{clip:D2} is first spoken after silence — " +
                        "fresh gen with locked refs (not video-extend) so the opening word is not clipped");
                }
            }

            if (prevVideoPath is not null)
            {
                await AppendLogAsync(
                    $"  [Continuity] Imagine video-extend from S{scene:D2}C{clip - 1:D2} " +
                    $"({Path.GetFileName(prevVideoPath)})");
            }
            else if (reseedFresh && extendSourcePath is not null)
            {
                await AppendLogAsync(
                    $"  [Identity] Reseed S{scene:D2}C{clip:D2} after S{scene:D2}C{clip - 1:D2} " +
                    "(locked character refs attached)");
            }

            string? styleHead = null;
            try
            {
                var rules = _projectRules.GetActiveRulesBlock(projectId);
                if (!string.IsNullOrWhiteSpace(rules))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        rules, @"STYLE LOCK:\s*([^\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                        styleHead = "STYLE LOCK: " + m.Groups[1].Value.Trim().TrimEnd('.', ' ');
                }
            }
            catch { /* non-fatal */ }

            var built = ClipVideoPromptBuilder.Build(
                clipEl,
                projectDir,
                characters: profiles,
                previousClipVisualPrompt: prevVisual,
                previousClipVideoPath: prevVideoPath,
                startFrameImagePath: null,
                maxRefs: modelEntry.MaxReferenceImages
                    ?? throw new InvalidOperationException(
                        $"Video model '{modelEntry.Id}' has no maxReferenceImages in models_catalog.json."),
                styleHead: styleHead,
                videoModel: model);

            if (string.IsNullOrWhiteSpace(built.Prompt))
                throw new InvalidOperationException("clip missing visual_prompt");

            // Fresh / reseed: every on-screen cast key must have a locked ref attached
            if (prevVideoPath is null)
                EnsureFreshGenHasLockedRefs(projectId, projectDir, built, profiles);
            else
            {
                // Extend still requires locks on disk even when API cannot attach them
                EnsureOnScreenLocksExist(projectId, projectDir, built, profiles);
            }

            // Approved project-scoped house rules (learning). Global clip gen rules live in
            // embedded prompts/clip_gen_rules.txt and are composed inside ClipVideoPromptBuilder.
            try
            {
                var rules = _projectRules.GetActiveRulesBlock(projectId);
                if (!string.IsNullOrWhiteSpace(rules))
                {
                    built = built.WithPrompt(
                        built.Prompt.TrimEnd() + "\n\n" + rules.Trim(),
                        " · project-rules");
                }
            }
            catch { /* non-fatal */ }

            if (string.IsNullOrWhiteSpace(resolution))
                resolution = await ResolveVideoResolutionAsync(projectId, null, ct);

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

            // Persist + log full prompt for evaluation (admin logs surface this)
            await WriteAndLogPromptAsync(projectId, projectDir, scene, clip, built, ct).ConfigureAwait(false);

            if (built.Prompt.Contains("<VoiceLock>", StringComparison.OrdinalIgnoreCase))
                await AppendLogAsync("  [Voice] VOICE LOCK from character profile");
            if (built.ReferenceImagePaths.Count > 0)
                await AppendLogAsync(
                    $"  [Refs] attached={built.RefsAttachedToApi} count={built.ReferenceImagePaths.Count}: " +
                    string.Join(", ", built.ReferenceImagePaths.Select(Path.GetFileName)));
            else if (prevVideoPath is not null)
                await AppendLogAsync("  [Refs] video-extend — locked plates not attached to API (IDENTITY text only)");

            // Only continuation-chain models get carried-forward padding: clip N+1 already can't
            // start before clip N is on disk for these, so reconciling against N's real measurement
            // costs nothing extra. Non-continuation models don't have that same-scene coupling.
            var supportsContinue = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).SupportsVideoContinue;

            // Dialogue-aware duration (tight for short lines — billed per second), clamped to the
            // actually-selected model's own duration caps (SupportedModelCatalog) instead of a
            // hardcoded provider assumption.
            var (durMin, durMax, durAbsMax) = ClipDurationEstimator.ResolveBoundsForModel(model);
            var duration = ClipDurationEstimator.EstimateForClip(clipEl, durMin, durMax, durAbsMax);
            if (supportsContinue && incomingDurationPaddingSec > 0)
            {
                var padded = ApplyIncomingDurationPadding(duration, incomingDurationPaddingSec, durAbsMax);
                await AppendLogAsync(
                    $"  [Duration] +{incomingDurationPaddingSec:F1}s carried from previous clip's overrun -> {duration}s to {padded}s");
                duration = padded;
            }
            await AppendLogAsync($"  [Duration] estimated {duration}s (dialogue-aware, max {durMax}s, model={model})");
            // Reference-conditioned / continuation generation is bounded by the model's own
            // tighter extension cap (catalog MaxExtensionSeconds), not a bare hardcoded 10 — keeps
            // this correct if a future model's real ref-conditioned max differs from Grok's ~10s.
            if (prevVideoPath is not null || built.ReferenceImagePaths.Count > 0)
                duration = ClipDurationEstimator.ResolveActualDurationForModel(model, duration, isExtensionMode: true);

            var modeLabel = prevVideoPath is not null ? "video-extend" : built.Mode;
            await AppendLogAsync(
                $"  [Grok] Submit S{scene:D2}C{clip} duration={duration}s res={resolution} " +
                $"model={model} mode={modeLabel} {built.PromptLogSummary}");

            // Prefer official video continue; character refs only on fresh gens (API: no mix)
            var requestId = await _grok.SubmitGenerationAsync(
                built.Prompt,
                duration,
                resolution,
                model,
                ct,
                referenceImagePaths: prevVideoPath is null && built.ReferenceImagePaths.Count > 0
                    ? built.ReferenceImagePaths
                    : null,
                startFrameImagePath: null,
                continueFromVideoPath: prevVideoPath);
            await AppendLogAsync($"  [Grok] request_id={requestId}");

            var url = await _grok.PollForVideoUrlAsync(
                requestId,
                msg => { _ = AppendLogAsync($"  [Grok] {msg}"); },
                ct);

            // Save MP4 file to server project directory so client media sync delivers MP4 files to client folder.
            // Via IVideoClient.DownloadToFileAsync, not a raw HttpClient GET — the URL a fake
            // provider returns isn't necessarily real http(s) (FakeGrokVideoClient.DownloadToFileAsync
            // resolves its own "fake-fixture:" scheme to a local file instead of attempting a request;
            // a bare GetByteArrayAsync silently failed on that scheme and this whole save was skipped).
            var mp4Path = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");
            try
            {
                await _grok.DownloadToFileAsync(url, mp4Path, ct).ConfigureAwait(false);
                var bytesLength = File.Exists(mp4Path) ? new FileInfo(mp4Path).Length : 0;
                if (bytesLength > 0)
                {
                    await AppendLogAsync($"  [Media] Saved {bytesLength} bytes to {Path.GetFileName(mp4Path)}");

                    // Trigger 100% automated background clip dialogue & speaker verification.
                    // Telemetry recording below awaits this (if started) so DialogueTruncated
                    // reflects the real Expected-vs-Heard result instead of staying hardcoded false.
                    Task<ClipDialogueVerificationResult?>? dialogueVerificationTask = null;
                    if (_dialogueVerification is not null && _dialogueVerification.IsConfigured)
                    {
                        var projId = Snapshot.ProjectId ?? projectId ?? _projects.ActiveProjectId;
                        dialogueVerificationTask = Task.Run(async () =>
                        {
                            try
                            {
                                return await _dialogueVerification.VerifyClipDialogueAsync(projId, scene, clip, force: true, ct: CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Background dialogue verification failed for S{Scene:D2}C{Clip:D2}", scene, clip);
                                return null;
                            }
                        });
                    }

                    // Probe the real rendered duration once — used both to carry a same-scene
                    // continuation-chain padding nudge into the next clip (below) and, if timing
                    // calibration is configured, for telemetry.
                    var probedSec = Mp4DurationReader.TryReadSeconds(mp4Path) ?? (double)duration;
                    overrunSec = ComputeCarryoverOverrunSec(supportsContinue, probedSec, duration);

                    // Record dynamic cut timing telemetry into SQLite database for continuous server learning
                    if (_timingCalibration is not null)
                    {
                        var projId = Snapshot.ProjectId ?? projectId ?? _projects.ActiveProjectId;

                        // Attribute evaluator to project Video review / planning model (Settings), never invent.
                        var evaluatorModelId = "";
                        try
                        {
                            var evalCfg = await _projects.GetConfigAsync(projId, ct).ConfigureAwait(false);
                            evaluatorModelId = ProjectModelSelection.TryGet(
                                evalCfg,
                                ProjectModelSelection.QualityConfigKey,
                                ProjectModelSelection.PlanningConfigKey,
                                ProjectModelSelection.ChatConfigKey) ?? "";
                        }
                        catch { /* telemetry only */ }

                        // 1. Extract dialogue text & word count from clip blueprint
                        string dialogueText = "";
                        if (clipEl.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object &&
                            ap.TryGetProperty("dialogue", out var dEl))
                        {
                            dialogueText = dEl.GetString() ?? "";
                        }
                        int wordCount = string.IsNullOrWhiteSpace(dialogueText)
                            ? 0
                            : dialogueText.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

                        // 2. Extract camera movement category from blueprint or visual prompt
                        string camCat = "cam_push_in";
                        if (clipEl.TryGetProperty("camera", out var camEl) && camEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(camEl.GetString()))
                            camCat = camEl.GetString()!;
                        else if (clipEl.TryGetProperty("camera_category", out var ccEl) && ccEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ccEl.GetString()))
                            camCat = ccEl.GetString()!;

                        // 3. Dynamically classify scene action category via AiActionOverheadClassifier
                        var promptToAnalyze = built.Prompt ?? "";
                        string actCat = "act_generic_action";
                        if (_timingClassifier is not null)
                        {
                            var estimation = _timingClassifier.ClassifyNovelAction(promptToAnalyze, null);
                            if (!string.IsNullOrWhiteSpace(estimation.MatchCategoryId))
                                actCat = estimation.MatchCategoryId;
                        }

                        // 4. Calculate measured camera and physical action overheads
                        double camOverhead = _timingLedger?.GetOverheadSec(camCat, 1.6) ?? 1.6;
                        double netSpeechSec = wordCount > 0 ? (wordCount / ClipDurationEstimator.DialogueWordsPerSecond) : 0.0;
                        double measuredActOverhead = Math.Max(0.5, Math.Round(probedSec - camOverhead - netSpeechSec, 2));

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var dialogueTruncated = false;
                                if (dialogueVerificationTask is not null)
                                {
                                    var verification = await dialogueVerificationTask.ConfigureAwait(false);
                                    if (verification is not null)
                                        dialogueTruncated = ClipDialogueVerificationService.LooksTruncated(verification);
                                }

                                await _timingCalibration.RecordCutTelemetryAsync(
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
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not save MP4 bytes to server project directory for S{Scene:D2}C{Clip:D2}", scene, clip);
            }

            var relPath = MediaRegistryService.ClipRelativePath(scene, clip);
            var ticket = _mediaProxy.Issue(url, TimeSpan.FromMinutes(45));
            var clientUrl = $"/api/media/proxy/{ticket}";
            await UpdateAsync(s =>
            {
                s.ClientMediaUrl = clientUrl;
                s.ClientRelativePath = relPath;
                s.Scene = scene;
                s.Clip = clip;
            });
            await AppendLogAsync(
                $"  [Grok] video ready for client save → {relPath} (not stored on server disk)");

            if (_sidecars is not null)
            {
                try
                {
                    var projDir = await _projects.GetProjectDirAsync(Snapshot.ProjectId ?? projectId ?? _projects.ActiveProjectId, ct).ConfigureAwait(false);
                    // xAI Files API reference for this exact clip, when generation requested
                    // storage and it succeeded (see GrokVideoClient's storage_options) — lets a
                    // later "AI Edit" reuse the file instead of re-uploading. Absent for
                    // non-Grok providers or when storage wasn't granted; never required.
                    var (sourceFileId, sourceFileExpiresAt) = _grok.TryGetStoredFileReference(requestId);
                    await _sidecars.WriteSidecarAsync(
                        projDir,
                        scene,
                        clip,
                        prompt: built.Prompt ?? "",
                        scriptText: "",
                        model: model,
                        resolution: resolution,
                        durationSeconds: (double)duration,
                        sha256: "",
                        sizeBytes: 0,
                        // Persist the provider-hosted video URL so an exported project can be re-hydrated
                        // by another user on import (xAI/Grok URLs are long-lived). Provider is resolved
                        // from the model via the catalog (SSoT) rather than hardcoded.
                        sourceUrl: url,
                        sourceProvider: SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).ProviderId,
                        sourceFileId: sourceFileId,
                        sourceFileExpiresAtUnixSeconds: sourceFileExpiresAt,
                        ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not write clip sidecar for S{Scene:D2}C{Clip:D2}", scene, clip);
                }
            }

            // Cost uses requested duration (no server file to probe until client registers).
            var costDurationSec = (double)duration;

            try
            {
                var costProjectId = Snapshot.ProjectId ?? projectId ?? _projects.ActiveProjectId;
                await _costs.RecordVideoGenerationAsync(
                    costProjectId,
                    scene,
                    clip,
                    costDurationSec,
                    resolution,
                    model,
                    hasRefImage: built.ReferenceImagePaths.Count > 0 || prevVideoPath is not null,
                    isExtend: prevVideoPath is not null,
                    requestId: requestId,
                    requestedDurationSec: duration,
                    userId: Snapshot.UserId ?? _user.UserId,
                    ct: ct);
                await AppendLogAsync(
                    $"  [Cost] tracked list-rate for S{scene:D2}C{clip} ({costDurationSec:F2}s)");
            }
            catch (Exception ex)
            {
                await AppendLogAsync($"  [Cost] ledger write skipped: {ex.Message}");
            }
        }
        finally
        {
            // Single-use: consumed extend-source is deleted so a later plain regenerate (no fresh
            // upload) falls back to fresh gen instead of silently reusing stale continuity data.
            if (extendInputTemp is not null)
            {
                try { File.Delete(extendInputTemp); } catch { /* ignore */ }
            }
        }

        return overrunSec;
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
            var dir = Path.Combine(projectDir, "assets", "video", "prompts");
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
            var historyDir = Path.Combine(projectDir, "assets", "video", "history");
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
    public static List<ClipPromptHistoryEntry> ListClipPromptHistory(string projectDir, int scene, int clip)
    {
        var result = new List<ClipPromptHistoryEntry>();
        var historyDir = Path.Combine(projectDir, "assets", "video", "history");
        if (!Directory.Exists(historyDir)) return result;

        var prefix = $"scene_{scene:D2}_clip_{clip:D2}_";
        foreach (var file in Directory.GetFiles(historyDir, $"{prefix}*.meta.json"))
        {
            try
            {
                var name = Path.GetFileName(file);
                var stamp = name[prefix.Length..^".meta.json".Length];
                if (!long.TryParse(stamp, out var ms)) continue;

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
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
            var sec = Mp4DurationReader.TryReadSeconds(videoPath);
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
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        var missing = MissingOnScreenLockKeys(projectId, projectDir, built, profiles);
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            "Locked character reference images required on disk before video-extend " +
            "(identity continuity even though the API cannot attach plates). " +
            $"Missing ref for: {string.Join(", ", missing)}. " +
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
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        var missing = MissingOnScreenLockKeys(projectId, projectDir, built, profiles);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Locked character reference images required for fresh video gen (avoids face drift). " +
                $"Missing ref for: {string.Join(", ", missing)}. " +
                "Open Characters → generate + lock a portrait for each on-screen role.");
        }

        var onScreen = OnScreenVisualKeys(built, profiles);
        if (onScreen.Count > 0 && built.ReferenceImagePaths.Count == 0)
        {
            throw new InvalidOperationException(
                "Fresh video gen built a prompt with on-screen cast but attached 0 reference images. " +
                "Lock portraits under Characters and retry.");
        }
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
            if (path is null || !File.Exists(path))
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
            if (!root.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var s in scenes.EnumerateArray())
            {
                if (!s.TryGetProperty("scene_number", out var sn) || !sn.TryGetInt32(out var n) || n != scene)
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
        if (!clipEl.TryGetProperty("audio_payload", out var ap) ||
            ap.ValueKind != JsonValueKind.Object)
            return false;
        var dialogue = ap.TryGetProperty("dialogue", out var d) ? d.GetString() ?? "" : "";
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
        if (!sceneEl.TryGetProperty("veo_clips", out var clips) ||
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
        if (!root.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in scenes.EnumerateArray())
        {
            if (s.TryGetProperty("scene_number", out var n) && n.TryGetInt32(out var sn) && sn == sceneNum)
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
        string resolution;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            resolution = NormalizeResolution(requested);
        }
        else
        {
            resolution = null!;
            try
            {
                var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            EnsureLabModelsAllowed(cfg);
                if (cfg.TryGetValue("resolution", out var el))
                {
                    var fromCfg = el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.Number => el.ToString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(fromCfg))
                        resolution = NormalizeResolution(fromCfg);
                }
            }
            catch
            {
                // fall through to app default
            }

            resolution ??= NormalizeResolution(
                string.IsNullOrWhiteSpace(_opts.DefaultResolution) ? "480p" : _opts.DefaultResolution);
        }

        var locked = await GetLockedResolutionAsync(projectId, ct).ConfigureAwait(false);
        if (locked is not null && !string.Equals(locked, resolution, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"This project's existing clips are {locked} — generating at {resolution} would mix " +
                $"resolutions in one movie. Delete the existing clips first, or generate at {locked}.");
        }

        return resolution;
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
    /// Fail closed before video spend when the selected model lacks continue/refs required for this job.
    /// </summary>
    private async Task EnsureVideoModelCapabilitiesAsync(
        string projectId,
        bool needContinue,
        bool needReferenceImages,
        CancellationToken ct,
        string? modelOverride = null)
    {
        if (!needReferenceImages)
            return;

        var modelId = await ResolveVideoModelAsync(projectId, ct, modelOverride).ConfigureAwait(false);
        var entry = SupportedModelCatalog.ResolveOrDefault(modelId, ModelCapability.Video);

        if (needReferenceImages && !entry.SupportsReferenceImages)
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
    private async Task ReportStage1ProgressAsync(string line)
    {
        // Single UpdateAsync so Index/Total + log stay atomic (no race losing counters).
        // Keep Total on a 10-step phase scale so single-pass adapt still moves the bar
        // (legacy chunk-only counters left Total=0 → UI stuck at 35%).
        await UpdateAsync(s =>
        {
            if (s.Log.Count == 0 || s.Log[^1] != line)
            {
                s.Log.Add(line);
                if (s.Log.Count > 120)
                    s.Log = s.Log.TakeLast(120).ToList();
            }
            s.Message = line;
            s.Total = Math.Max(s.Total, 10);

            // Multi-chunk adapt: map chunk i/N into phases 4–8
            var m = System.Text.RegularExpressions.Regex.Match(
                line, @"chunk\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out var idx) &&
                int.TryParse(m.Groups[2].Value, out var tot) &&
                tot > 0)
            {
                var chunkDone = line.Contains("done", StringComparison.OrdinalIgnoreCase);
                var frac = chunkDone
                    ? Math.Clamp((double)idx / tot, 0, 1)
                    : Math.Clamp((idx - 1.0) / tot, 0, 1);
                s.Index = Math.Max(s.Index, 4 + (int)Math.Round(4.0 * frac));
                return;
            }

            // Vision prepare: page i/N → phases 1–3
            var mVis = System.Text.RegularExpressions.Regex.Match(
                line, @"(?:Grok vision|Reading page|page)\s+(\d+)\s*/\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mVis.Success &&
                int.TryParse(mVis.Groups[1].Value, out var vi) &&
                int.TryParse(mVis.Groups[2].Value, out var vt) &&
                vt > 0)
            {
                var frac = Math.Clamp((vi - 1.0) / vt, 0, 1);
                s.Index = Math.Max(s.Index, 1 + (int)Math.Round(2.0 * frac));
                return;
            }

            if (line.Contains("Screenplay ready", StringComparison.OrdinalIgnoreCase))
                s.Index = Math.Max(s.Index, 10);
            else if (line.Contains("approving", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Fountain draft saved", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("plate", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Attaching", StringComparison.OrdinalIgnoreCase))
                s.Index = Math.Max(s.Index, 9);
            else if (line.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Stitch", StringComparison.OrdinalIgnoreCase))
                s.Index = Math.Max(s.Index, 8);
            else if (line.Contains("repair", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Refin", StringComparison.OrdinalIgnoreCase))
                s.Index = Math.Max(s.Index, 7);
            else if (line.Contains("single pass", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Adapting book", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Book split", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("multi-chunk", StringComparison.OrdinalIgnoreCase))
                s.Index = Math.Max(s.Index, 4);
            else if (line.Contains("Target runtime", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("building Fountain", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Writing screenplay", StringComparison.OrdinalIgnoreCase))
                s.Index = Math.Max(s.Index, 3);
            else if (line.Contains("prepare", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Extract", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Vision", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("book text", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Checking book", StringComparison.OrdinalIgnoreCase))
                s.Index = Math.Max(s.Index, 1);
            else
                s.Index = Math.Max(s.Index, 1);
        });
        if (_sink is not null)
            await _sink.OnJobLogAsync(line);
    }

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
            if (s.Total > 0 && status == "done")
                s.Index = s.Total;
            projectId = s.ProjectId;
            kind = s.Kind;
        });
        await AppendLogAsync(message);

        // Scene list cache: clip/composite counts change on gen/remux/stage done
        if (status is "done" or "error" or "cancelled")
        {
            if (string.IsNullOrWhiteSpace(projectId))
                projectId = CurrentRun.Value?.Snapshot.ProjectId;
            _projects.InvalidateSceneListCache(projectId);
        }

        // PR4.5b: keep ARTIFACTS.md / artifact_index.json current after pipeline work
        if (status == "done" &&
            !string.IsNullOrWhiteSpace(projectId) &&
            ShouldRefreshArtifactIndex(kind))
        {
            await TryRefreshArtifactIndexAsync(projectId!).ConfigureAwait(false);
        }

        // Stage-end package history: one debounced commit for finished film/music work
        // (text artifacts only — MP4/MP3 stay gitignored). Intermediate clip writes do not commit.
        if ((status == "done" || status == "partial") &&
            !string.IsNullOrWhiteSpace(projectId) &&
            StageEndAutoGitMessage(kind) is { } gitMsg)
        {
            _projects.TriggerAutoGitCommit(projectId!, gitMsg);
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
    private static bool ClipPresentOnServerOrClient(string mp4Path) =>
        (File.Exists(mp4Path) && new FileInfo(mp4Path).Length >= 1024) ||
        File.Exists(mp4Path + ".client.json");

    private static bool ShouldRefreshArtifactIndex(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        return kind is
            "remux" or
            "gen-scene" or
            "gen-batch" or
            "clip-auto-review" or
            "clip-auto-review-batch" or
            "stage2" or
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
