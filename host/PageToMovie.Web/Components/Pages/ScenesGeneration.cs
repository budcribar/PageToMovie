using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes
{
    /// <summary>Generation domain for the Scenes page. Owns related UI state and behavior.</summary>
    public sealed class ScenesGeneration
    {

    private readonly Scenes S;
    public ScenesGeneration(Scenes host) => S = host;

    private const string StatusRunning = "running";
    private const string StatusQueued = "queued";
    private const string StatusError = "error";
    private const string KindBatch = "batch";
    private const string KindRemux = "remux";
    private const string KindScene = "scene";
    private const string KindStage2 = "stage2";


    internal JobSnapshot? _job;


    internal List<JobSnapshot> _myJobs = new();


    /// <summary>Admin-only: expand finished-job log under the compact result card.</summary>
    internal bool _showAdminJobLog;


    /// <summary>Highest progress % shown for the current job — bar never bounces backward.</summary>
    internal int _progressFloor;


    internal string? _progressFloorJobId;


    /// <summary>Throttle mid-job list refresh so clips x/y + status pills stay live without thrashing.</summary>
    internal int _lastListRefreshIndex = -1;


    internal int? _lastListRefreshScene;


    internal string? _lastListRefreshMessage;


    internal DateTimeOffset _lastListRefreshAt = DateTimeOffset.MinValue;


    internal bool _listRefreshInFlight;

    private CancellationTokenSource? _jobPollCts;

    /// <summary>Job the live <see cref="PollLostJobLoopAsync"/> is watching, so repeat
    /// JobUpdated ticks for the same job do not restart it.</summary>
    private string? _polledJobId;



    /// <summary>
    /// Set for the brief window between kicking off a regen and the job snapshot round-trip
    /// confirming it server-side — closes the gap where <see cref="IsSceneGenBusy"/> would
    /// otherwise still see the previous (already-finished) job and let a stale composite show.
    /// </summary>
    internal int? _pendingRegenScene;

    /// <summary>H3 — after user_regen, offer optional one-click reason chips for this clip.</summary>
    internal int? _pendingTakeReasonScene;
    internal int? _pendingTakeReasonClip;
    internal string? _takeReasonSaved;



    // Batch-generate confirm modal: resolution + cost decided at the moment of spend.
    internal bool _showGenerateConfirm;
    /// <summary>True = re-render every clip in the selection as a new take (today's Regenerated).
    /// False = fill missing clips only (today's Generate).</summary>
    internal bool _generateForceAllTakes;
    /// <summary>Live progress modal — opened the moment a clip/scene/batch job is requested so the
    /// user sees something immediately (the card at the top of the page is easy to miss).</summary>
    internal bool _showJobModal;

    internal const string PreparingDefaultMessage = "Preparing…";
    internal const string PreparingExtendMessage = "Preparing previous clip for extend…";
    internal const string UploadingPredecessorMessage = "Uploading predecessor…";
    internal const string UploadingReferencesMessage = "Uploading reference pictures…";

    /// <summary>
    /// Show the Generating popup immediately after cheap gates (cast / folder / already-running).
    /// Uses a local snapshot so the card is not empty or a leftover Waiting job while predecessor
    /// prep (plate upload, MP4 upload, ffmpeg trim + extend-source POST) still runs.
    /// </summary>
    internal void OpenJobModal(
        string? preparingMessage = null,
        string kind = KindScene,
        int? scene = null,
        int? clip = null)
    {
        if (IsLocalPreparingJob(_job))
        {
            if (!string.IsNullOrWhiteSpace(preparingMessage))
                SetPreparingMessage(preparingMessage);
        }
        else
            _job = CreateLocalPreparingJob(preparingMessage ?? PreparingDefaultMessage, kind, scene, clip);
        _showJobModal = true;
    }

    internal async Task OpenJobModalAndPaintAsync(
        string? preparingMessage = null,
        string kind = KindScene,
        int? scene = null,
        int? clip = null)
    {
        OpenJobModal(preparingMessage, kind, scene, clip);
        try { await S.InvokeAsync(S.StateHasChanged); }
        catch { S.StateHasChanged(); }
    }

    internal void HideJobModal() => _showJobModal = false;

    internal static JobSnapshot CreateLocalPreparingJob(
        string message,
        string kind = KindScene,
        int? scene = null,
        int? clip = null)
    {
        var snap = new JobSnapshot
        {
            Kind = kind,
            Status = StatusRunning,
            Message = message,
            Scene = scene,
            Clip = clip,
            StartedAt = DateTimeOffset.UtcNow,
        };
        snap.Log.Add(message);
        return snap;
    }

    internal static bool IsLocalPreparingJob(JobSnapshot? job) =>
        job is { } j &&
        string.IsNullOrEmpty(j.JobId) &&
        (string.Equals(j.Status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(j.Status, StatusQueued, StringComparison.OrdinalIgnoreCase));

    internal void SetPreparingMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (_job is null)
            _job = CreateLocalPreparingJob(message);
        else if (IsLocalPreparingJob(_job))
        {
            _job.Message = message;
            if (_job.Log.Count == 0 || !string.Equals(_job.Log[^1], message, StringComparison.Ordinal))
                _job.Log.Add(message);
        }
    }

    internal void FailLocalPreparingJob(string message)
    {
        if (_job is null || !IsLocalPreparingJob(_job)) return;
        _job.Status = StatusError;
        _job.Message = message;
        _job.Error = message;
        _job.FinishedAt = DateTimeOffset.UtcNow;
        if (_job.Log.Count == 0 || !string.Equals(_job.Log[^1], message, StringComparison.Ordinal))
            _job.Log.Add(message);
    }



    // Admin-only: video models offered as a one-off per-batch override in the Generate modal, so an
    // admin can A/B different generators without editing project Configuration. "" = project default.
    internal List<SupportedModelDto> _videoModels = new();


    internal string _selectedVideoModel = "";



    /// <summary>Video gen resolution (defaults from Configuration).</summary>
    internal string _genResolution = "480p";



    /// <summary>True once the shot plan already has an end-credits scene (auto-inserted or re-added).</summary>
    internal bool HasCreditsScene => S.List._scenes?.Any(s => s.IsCredits) == true;



    internal bool JobRunning =>
        string.Equals(_job?.Status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_job?.Status, StatusQueued, StringComparison.OrdinalIgnoreCase) ||
        _myJobs.Any(j =>
            string.Equals(j.Status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(j.Status, StatusQueued, StringComparison.OrdinalIgnoreCase));



    /// <summary>
    /// True when a clip/scene/remux job is active for this scene — hide stale composite player.
    /// </summary>
    internal bool IsSceneGenBusy(int sceneNumber)
    {
        if (sceneNumber <= 0) return false;
        if (_pendingRegenScene == sceneNumber) return true;

        static bool Active(string? status) =>
            string.Equals(status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, StatusQueued, StringComparison.OrdinalIgnoreCase);

        bool Affects(JobSnapshot j)
        {
            if (!Active(j.Status) || !IsScenesWorkflowJob(j.Kind))
                return false;
            // Batch may include this scene; hide composite to avoid playing stale mux.
            // Remux job.Scene goes null during the WIP-stitch phase ("Combining scenes
            // into movie…") — treat that the same way: hide every composite rather than
            // let one sit there mid-rewrite with a Play button live on it.
            if (string.Equals(j.Kind, KindBatch, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Kind, KindRemux, StringComparison.OrdinalIgnoreCase))
                return true;
            return j.Scene is int sn && sn == sceneNumber;
        }

        if (_job is not null && Affects(_job))
            return true;
        return _myJobs.Any(Affects);
    }



    /// <summary>Jobs that belong on Scenes (not leftover character jobs).</summary>
    internal static bool IsScenesWorkflowJob(string? kind) =>
        kind is KindScene or KindBatch or KindRemux or KindStage2 or "stage2" or "music" or "lip_sync" or "video_edit";



    /// <summary>
    /// One compact progress card while video work runs — operators and admin.
    /// No badges, provider names, raw engine message, or live log.
    /// </summary>
    internal bool ShowLiveGenProgress
    {
        get
        {
            var job = _job;
            return job is { } live &&
                   IsScenesWorkflowJob(live.Kind) &&
                   (live.Status is StatusRunning or StatusQueued);
        }
    }

    internal bool ShowOperatorGenError
    {
        get
        {
            var job = _job;
            return !S.Session.IsAdmin &&
                   job is { } live &&
                   IsScenesWorkflowJob(live.Kind) &&
                   string.Equals(live.Status, StatusError, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal bool ShowOperatorGenPartial
    {
        get
        {
            var job = _job;
            return !S.Session.IsAdmin &&
                   job is { } live &&
                   IsScenesWorkflowJob(live.Kind) &&
                   string.Equals(live.Status, "partial", StringComparison.OrdinalIgnoreCase);
        }
    }



    /// <summary>Short outcome label for the live bar (no provider / path / status dump).</summary>
    internal static string LiveGenStatusLabel(JobSnapshot job)
    {
        // Local pre-server snapshot: show what prep is doing, not "Waiting…" / "Generating…".
        if (IsLocalPreparingJob(job) && !string.IsNullOrWhiteSpace(job.Message))
            return job.Message;

        if (string.Equals(job.Status, StatusQueued, StringComparison.OrdinalIgnoreCase))
            return "Waiting…";

        var kind = job.Kind ?? "";
        if (string.Equals(kind, "music", StringComparison.OrdinalIgnoreCase))
            return NonEmptyOr(job.Message, "Scoring background music…");
        if (string.Equals(kind, "lip_sync", StringComparison.OrdinalIgnoreCase))
            return NonEmptyOr(job.Message, "Lip-syncing dialogue…");
        if (string.Equals(kind, KindStage2, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "stage2", StringComparison.OrdinalIgnoreCase))
            return NonEmptyOr(job.Message, "Rebuilding shot plan from screenplay…");
        if (string.Equals(kind, KindRemux, StringComparison.OrdinalIgnoreCase))
            return RemuxLiveLabel(job.Message);
        if (job.Total > 0 &&
            !string.Equals(kind, KindRemux, StringComparison.OrdinalIgnoreCase))
        {
            var display = job.Index <= 0 ? 1 : Math.Min(job.Index, job.Total);
            return $"Generating… {display} of {job.Total}";
        }
        return SceneOrBatchLiveLabel(kind, job.Clip);
    }

    private static string NonEmptyOr(string? message, string fallback) =>
        !string.IsNullOrWhiteSpace(message) ? message : fallback;

    private static string RemuxLiveLabel(string? message)
    {
        var msg = message ?? "";
        if (msg.StartsWith("Combining", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Measuring", StringComparison.OrdinalIgnoreCase))
            return msg.Length > 80 ? msg[..77] + "…" : msg;
        return "Combining video…";
    }

    private static string SceneOrBatchLiveLabel(string kind, int? clip)
    {
        if (string.Equals(kind, KindScene, StringComparison.OrdinalIgnoreCase) && clip is int)
            return "Generating clip…";
        if (string.Equals(kind, KindScene, StringComparison.OrdinalIgnoreCase))
            return "Generating scene…";
        if (string.Equals(kind, KindBatch, StringComparison.OrdinalIgnoreCase))
            return "Generating clips…";
        return "Generating…";
    }



    /// <summary>
    /// Progress percent while running. Remux uses a fixed 0–100 scale and is monotonic
    /// (no soft-crawl / waiting math that made the bar bounce during ffmpeg).
    /// </summary>
    internal int LiveGenProgressPercent(JobSnapshot job)
    {
        // New job id → reset floor so a finished 92% doesn't pin the next job.
        var jid = job.JobId ?? "";
        if (!string.Equals(jid, _progressFloorJobId, StringComparison.Ordinal))
        {
            _progressFloorJobId = jid;
            _progressFloor = 0;
        }

        int pct;
        var kind = job.Kind ?? "";
        if (string.Equals(kind, KindRemux, StringComparison.OrdinalIgnoreCase))
        {
            // Engine: Total=100, Index=0..99 while running (FinishAsync → 100).
            var total = job.Total > 0 ? job.Total : 100;
            var index = Math.Clamp(job.Index, 0, total);
            pct = (int)Math.Round(100.0 * index / total);
            pct = Math.Clamp(pct, 5, 92);
        }
        else
        {
            var total = job.Total;
            var index = Math.Max(0, job.Index);
            // Clip gen: do not treat as "waiting" soft-crawl — discrete clip steps only.
            // (IsJobInFlightMessage soft-crawl is for long screenplay LLM calls.)
            pct = AdaptationPageBase.AdaptationStepUi.ComputeProgressPercent(
                displayIndex: index,
                total: total,
                waiting: string.Equals(job.Status, StatusQueued, StringComparison.OrdinalIgnoreCase),
                jobRunning: true,
                startedAt: job.StartedAt);
            pct = EaseAcrossCurrentStep(pct, index, total);
        }

        // Monotonic while the same job runs — never bounce backward on mid-phase resets.
        if (pct < _progressFloor)
            pct = _progressFloor;
        else
            _progressFloor = pct;
        return pct;
    }

    private int _easeStepIndex = -1;
    private DateTimeOffset _easeStepAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creep from the last completed step toward the next one so the bar keeps moving between
    /// updates. Steps here are minutes apart — a shot plan reports once per scene and each scene
    /// is roughly nine classifier calls — so a purely stepped bar sits frozen at its 5% floor for
    /// the whole first scene and looks like nothing is happening at all.
    /// </summary>
    /// <remarks>
    /// The ceiling is the NEXT step, never beyond, so the bar can never claim more than one unit
    /// of work it has not finished. Timed from when the index last moved rather than from job
    /// start, or every step after the first would jump straight to its ceiling.
    /// </remarks>
    internal int EaseAcrossCurrentStep(int steppedPct, int index, int total)
    {
        if (total <= 0)
            return steppedPct;
        if (index != _easeStepIndex)
        {
            _easeStepIndex = index;
            _easeStepAt = DateTimeOffset.UtcNow;
            return steppedPct;
        }
        var next = (int)Math.Round(100.0 * Math.Clamp(index + 1, 0, total) / total);
        if (next <= steppedPct)
            return steppedPct;
        var eased = AdaptationPageBase.AdaptationStepUi.SoftCrawlPercent(
            _easeStepAt, floor: steppedPct, ceiling: next, tauSeconds: 60);
        return Math.Max(steppedPct, eased);
    }



    internal void OnJobUpdated(JobSnapshot snap)
    {
        _job = snap;
        if (JobLostOnRestart.IsInFlight(snap.Status) && !string.IsNullOrWhiteSpace(snap.JobId))
            StartJobPolling();
        else
            DisposePolling();
        _ = S.InvokeAsync(async () =>
        {
            if (S.Session.IsAdmin)
                await RefreshMyJobsAsync();

            if (snap.Status is "done" or "partial" or StatusError or "cancelled")
                await HandleTerminalJobAsync(snap);
            else if (ShouldRefreshSceneListWhileRunning(snap))
                await SoftReloadListLiveAsync();

            S.StateHasChanged();
        });
    }

    private async Task HandleTerminalJobAsync(JobSnapshot snap)
    {
        _lastListRefreshIndex = -1;
        _lastListRefreshScene = null;
        _lastListRefreshMessage = null;
        S._message = null;
        // Progress modal: a clean finish closes itself (the clips are right there in the table).
        // Error, partial, and cancelled stay up so the outcome is read, not missed.
        if (snap.Status == "done")
            _showJobModal = false;
        await SoftReloadAsync();
        // A5: final remaining numbers after job ends
        try { await S.List.RefreshCostEstimateAsync(); } catch { /* soft */ }
        if (snap.Status == "done")
            ApplyDoneJobSideEffects(snap);
    }

    private void ApplyDoneJobSideEffects(JobSnapshot snap)
    {
        if (string.Equals(snap.Kind, KindRemux, StringComparison.OrdinalIgnoreCase))
            ApplyRemuxDone();
        else if (string.Equals(snap.Kind, "preview", StringComparison.OrdinalIgnoreCase))
            ApplyPreviewDone();
        else if (string.Equals(snap.Kind, KindScene, StringComparison.OrdinalIgnoreCase) &&
                 snap.Clip is int cn &&
                 snap.Scene is int gsn)
            ApplySceneClipDone(cn, gsn);
        else if (string.Equals(snap.Kind, "video_edit", StringComparison.OrdinalIgnoreCase) &&
                 snap.Clip is int vecn &&
                 snap.Scene is int vesn)
            ApplyVideoEditDone(vecn, vesn);
        else if (string.Equals(snap.Kind, KindBatch, StringComparison.OrdinalIgnoreCase))
        {
            S.List._selected.Clear();
            // A batch is how per-clip Regen actually runs (one clip, kind=batch), so the open
            // clip's video and Takes count have to be re-read here too. Only the KindScene branch
            // used to do it, which left the inspector showing the pre-regen take count.
            if (S.ClipForm._selectedClip is int openClip)
                S.ClipForm.SelectClip(openClip);
        }
    }

    private void ApplyRemuxDone()
    {
        // Bust cache so next manual play / inline preview loads the new file.
        var bust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        S.Playback._sceneVideoKey = bust;
        S.Playback._inlineCompositeKey = bust;

        if (S.Playback._playSceneAfterRemux is not int playSn)
            return;
        // Play scene (auto-remux) — open player once remux finishes.
        S.Playback._playSceneAfterRemux = null;
        S.Playback._playingScene = playSn;
        S.Playback._showScenePlayer = true;
        S._message = $"Scene S{playSn:D2} ready — playing";
    }

    private void ApplyPreviewDone()
    {
        S.Playback._previewVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        S.Playback._showPreviewPlayer = true;
        S._message = $"Preview ready — {S.Playback._previewScenes.Count} scene(s): " +
                    string.Join(", ", S.Playback._previewScenes.Select(s => $"S{s:D2}"));
    }

    private void ApplySceneClipDone(int cn, int gsn)
    {
        S.Playback._clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        S._message = $"Clip S{gsn:D2}C{cn:D2} finished — Play scene when you want the updated composite";
        if (S.ClipForm._selectedClip == cn)
            S.ClipForm.SelectClip(cn);
    }

    private void ApplyVideoEditDone(int vecn, int vesn)
    {
        // New take saved as the active clip — bust the cache key so the inline
        // <video> shows the edit result, and refresh the open clip's Takes list.
        // SelectClip clears S._message as its first line, so it must run BEFORE the
        // completion message is set, not after (setting it after got silently wiped).
        S.Playback._clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (S.ClipForm._selectedClip == vecn)
            S.ClipForm.SelectClip(vecn);
        S._message = $"Clip S{vesn:D2}C{vecn:D2} edited — saved as a new take";
    }



    /// <summary>
    /// True when a live scene/batch/remux job advanced far enough that list counts may have changed.
    /// Throttled so long ffmpeg/API polls don't hammer GetScenes.
    /// </summary>
    internal bool ShouldRefreshSceneListWhileRunning(JobSnapshot snap)
    {
        if (!IsScenesWorkflowJob(snap.Kind))
            return false;
        if (snap.Status is not (StatusRunning or StatusQueued))
            return false;

        var msg = snap.Message ?? "";
        var indexChanged = snap.Index != _lastListRefreshIndex || snap.Scene != _lastListRefreshScene;
        var msgChanged = !string.Equals(msg, _lastListRefreshMessage, StringComparison.Ordinal);
        var clipFinished =
            msg.StartsWith("Done S", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Failed S", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Remuxed S", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("complete", StringComparison.OrdinalIgnoreCase);
        // Starting the next clip also implies the previous one landed on disk.
        var nextClipStarted =
            msg.Contains("Generating S", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Combining scene", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Combining clips", StringComparison.OrdinalIgnoreCase);

        if (!indexChanged && !clipFinished && !nextClipStarted)
            return false;

        // Avoid double SoftReload for the same snapshot payload.
        if (!indexChanged && !msgChanged)
            return false;

        var now = DateTimeOffset.UtcNow;
        // Clip-finished lines always refresh (after min gap); other advances throttle harder.
        var minGap = clipFinished ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(1.5);
        if (now - _lastListRefreshAt < minGap)
            return false;

        _lastListRefreshIndex = snap.Index;
        _lastListRefreshScene = snap.Scene;
        _lastListRefreshMessage = msg;
        _lastListRefreshAt = now;
        return true;
    }



    /// <summary>Light list/detail refresh while a job runs (no cast/cost thrash).</summary>
    internal async Task SoftReloadListLiveAsync()
    {
        if (_listRefreshInFlight) return;
        _listRefreshInFlight = true;
        try
        {
            var dto = await S.Engine.GetScenesAsync(S._projectId);
            S.List._scenes = dto?.Scenes ?? new List<SceneSummary>();
            await S.RefreshUncommittedStatusAsync();
            if (S.List._selectedScene is int sn)
            {
                var detail = await S.Engine.GetSceneDetailAsync(S._projectId, sn);
                S.List._detail = detail?.Scene;
                if (S.ClipForm._selectedClip is int cn && S.List._detail is not null)
                    S.ClipForm._clip = S.List._detail.Clips.FirstOrDefault(c => c.ClipNumber == cn);
            }
            // A5 polish: keep Film remaining strip in sync while gen runs (throttled).
            await MaybeRefreshCostWhileRunningAsync();
        }
        catch { /* ignore mid-job refresh errors */ }
        finally { _listRefreshInFlight = false; }
    }

    private DateTimeOffset _lastCostRefreshAt = DateTimeOffset.MinValue;

    /// <summary>Refresh cost report at most every ~3s while batch/scene gen is live.</summary>
    internal async Task MaybeRefreshCostWhileRunningAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCostRefreshAt < TimeSpan.FromSeconds(3))
            return;
        _lastCostRefreshAt = now;
        try { await S.List.RefreshCostEstimateAsync(); }
        catch { /* soft */ }
    }



    internal void OnJobLog(string line)
    {
        // Keep log for admin "Details" after finish — never push raw engine lines into the live status.
        if (_job is not null && !string.IsNullOrWhiteSpace(line))
        {
            _job.Log.Add(line);
            if (_job.Log.Count > 200)
                _job.Log.RemoveRange(0, _job.Log.Count - 200);
        }
        // No StateHasChanged while running — Index/Total come from JobUpdated; avoid thrashing UI on every log line.
        if (_job is not null &&
            (_job.Status is "done" or "partial" or StatusError or "cancelled"))
            _ = S.InvokeAsync(S.StateHasChanged);
    }



    /// <summary>
    /// A job the page still shows as queued/running may no longer exist on the server (the job store
    /// is in-memory; a redeploy drops it). Left alone, the page shows "Waiting…" forever with a Cancel
    /// that 404s. Ask the server; if the job is gone, fail the snapshot so the modal can Close.
    /// </summary>
    internal async Task ReconcileJobWithServerAsync()
    {
        var j = _job;
        if (j is null || string.IsNullOrWhiteSpace(j.JobId)) return;
        if (!JobLostOnRestart.IsInFlight(j.Status))
            return;

        var lookup = await S.Engine.LookupJobAsync(j.JobId);
        var current = await S.Engine.LookupCurrentJobAsync();
        var currentKnown = current.Status == JobLookupStatus.Found;

        if (lookup.Status == JobLookupStatus.Unreachable && !currentKnown)
            return;

        ApplyServerJobView(
            lookup.Status == JobLookupStatus.Found ? lookup.Job : null,
            byIdNotFound: lookup.Status == JobLookupStatus.NotFound,
            current: currentKnown ? current.Job : null,
            currentKnown: currentKnown);
    }

    /// <summary>
    /// Apply a current-job / job-by-id poll. 404, empty, or a different job fails a stale
    /// queued snapshot and clears <see cref="JobRunning"/> so the Generating modal can Close.
    /// Keeps the modal open so the operator sees the error instead of a silent hide.
    /// </summary>
    internal void ApplyServerJobView(
        JobSnapshot? byId,
        bool byIdNotFound,
        JobSnapshot? current,
        bool currentKnown)
    {
        var local = _job;
        if (local is null)
            return;
        var next = JobLostOnRestart.ApplyServerView(local, byId, byIdNotFound, current, currentKnown);
        _job = next;
        ReplaceMyJob(next);
        if (JobLostOnRestart.IsFinishedStatus(next.Status))
            DisposePolling();
    }

    /// <summary>
    /// Slow backstop for "the server died and the hub never told us". The fast paths are
    /// <c>Hub.Reconnected</c> and <c>ServerHealthState.Recovered</c>, which reconcile
    /// immediately — this loop only has to catch a socket that never comes back at all.
    /// Each tick costs two REST calls, so it must not run at UI-refresh cadence.
    /// </summary>
    internal static readonly TimeSpan LostJobPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Take ownership of a job this page just started: show it, and arm the lost-job watchdog.
    /// </summary>
    /// <remarks>
    /// Every start site used to assign <c>_job</c> and stop there, leaving polling to be armed by
    /// the first hub JobUpdated. When the hub said nothing — wrong per-user group, dropped socket,
    /// a job that died before its first update — nothing ever reconciled and the Generating modal
    /// sat on "Queued batch gen…" indefinitely while the server reported no active jobs at all.
    /// Arming it here means the watchdog does not depend on the transport it is meant to backstop.
    /// </remarks>
    internal void AdoptStartedJob(JobSnapshot? started)
    {
        if (started is not null)
            _job = started;
        StartJobPolling();
    }

    internal void StartJobPolling()
    {
        var jobId = _job?.JobId;
        if (!JobLostOnRestart.IsInFlight(_job?.Status) || string.IsNullOrWhiteSpace(jobId))
            return;
        // Idempotent: JobUpdated fires many times per job, and tearing the loop down and
        // rebuilding it on every hub tick both churns allocations and keeps resetting the delay.
        if (_jobPollCts is not null && string.Equals(_polledJobId, jobId, StringComparison.OrdinalIgnoreCase))
            return;
        DisposePolling();
        _polledJobId = jobId;
        _jobPollCts = new CancellationTokenSource();
        var ct = _jobPollCts.Token;
        _ = Task.Run(() => PollLostJobLoopAsync(ct), CancellationToken.None);
    }

    internal void DisposePolling()
    {
        _jobPollCts?.Cancel();
        _jobPollCts?.Dispose();
        _jobPollCts = null;
        _polledJobId = null;
    }

    private async Task PollLostJobLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && JobLostOnRestart.IsInFlight(_job?.Status))
            {
                await Task.Delay(LostJobPollInterval, ct);
                if (ct.IsCancellationRequested)
                    return;
                await ReconcileJobWithServerAsync();
                if (_job is { } live && JobLostOnRestart.IsFinishedStatus(live.Status))
                {
                    // Run the SAME finish work the hub path does — close the modal, reload the
                    // scene list, apply the per-kind side effects. Correcting the status text was
                    // not enough: on a run where SignalR delivered nothing (Mary19 S02C02,
                    // 2026-08-25) the clip was generated, verified and committed server-side while
                    // the modal sat on "Queued batch gen…" and the page never picked up the take.
                    // Re-running this if the hub later delivers the same terminal event is
                    // harmless — it re-reads the list and re-closes an already-closed modal.
                    await S.InvokeAsync(() => HandleTerminalJobAsync(live));
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* ignore poll errors */ }
    }

    private void ReplaceMyJob(JobSnapshot next)
    {
        if (string.IsNullOrWhiteSpace(next.JobId))
            return;
        for (var i = 0; i < _myJobs.Count; i++)
        {
            if (string.Equals(_myJobs[i].JobId, next.JobId, StringComparison.OrdinalIgnoreCase))
                _myJobs[i] = next;
        }
    }

    internal async Task SoftReloadAsync()
    {
        try
        {
            var dto = await S.Engine.GetScenesAsync(S._projectId);
            S.List._scenes = dto?.Scenes ?? new List<SceneSummary>();
            S.List._selected.RemoveWhere(sn => S.List._scenes.All(s => s.SceneNumber != sn));
            S.List.ReconcileSelectedSceneWithList();
            await ReconcileJobWithServerAsync();
            await RefreshMyJobsAsync();
            await S.List.RefreshCastGateAsync();
            await S.List.RefreshResolutionLockAsync();
            if (S.List._selectedScene is int sn && S.List._scenes.Any(s => s.SceneNumber == sn))
            {
                try
                {
                    var detail = await S.Engine.GetSceneDetailAsync(S._projectId, sn);
                    S.List._detail = detail?.Scene;
                    if (S.ClipForm._selectedClip is int cn && S.List._detail is not null)
                        S.ClipForm._clip = S.List._detail.Clips.FirstOrDefault(c => c.ClipNumber == cn);
                }
                catch
                {
                    // Deleted/missing open scene — keep the refreshed list counts; drop stale detail.
                    S.List._detail = null;
                }
            }
        }
        catch { /* ignore soft reload errors */ }
    }



    internal async Task RefreshMyJobsAsync()
    {
        try
        {
            var list = await S.Engine.GetJobsAsync(mine: true);
            _myJobs = list?.Jobs?
                .Where(j =>
                    string.Equals(j.Status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(j.Status, StatusQueued, StringComparison.OrdinalIgnoreCase) ||
                    (j.FinishedAt is DateTimeOffset f && f > DateTimeOffset.UtcNow.AddMinutes(-5)))
                .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
                .Take(12)
                .ToList()
                ?? new List<JobSnapshot>();
        }
        catch
        {
            // keep previous
        }
    }




    internal Task GenerateMissingInSceneAsync(int sn) => GenOneSceneAsync(sn);

    /// <summary>E4/E6: force-regenerate entire scene (all clips).</summary>
    internal async Task ForceRegenSceneAsync(int sn)
    {
        if (IsCreditsSceneNum(sn)) { await GenerateCreditsEntryAsync(sn); return; }
        if (!S.List.CastReady) { S._error = S.List.CastBlockedTitle; return; }
        if (JobRunning && IsScenesWorkflowJob(_job?.Kind))
        {
            S._message = "A generate job is already running — watch progress.";
            return;
        }
        S._busy = true;
        S._error = null;
        _pendingRegenScene = sn;
        try
        {
            await OpenJobModalAndPaintAsync(PreparingDefaultMessage, KindScene, scene: sn);
            await EnsureHubAsync();
            await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
            await S.Engine.StartSceneGenAsync(S._projectId, sn, onlyMissing: false, resolution: _genResolution);
            AdoptStartedJob((await S.Engine.GetJobAsync())?.Job);
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
            FailLocalPreparingJob(ex.Message);
        }
        finally { S._busy = false; _pendingRegenScene = null; }
    }

    /// <summary>E4/E6: force-regen only stale clips in the open scene.</summary>
    internal async Task RegenStaleInSceneAsync(int sn)
    {
        if (S.List._detail is null || S.List._detail.SceneNumber != sn)
        {
            await S.List.OpenSceneAsync(sn);
        }
        var stale = S.List._detail?.Clips?.Where(c => c.IsStale).Select(c => c.ClipNumber).ToList() ?? new();
        if (stale.Count == 0)
        {
            S._message = "No stale clips in this scene.";
            return;
        }
        if (!S.List.CastReady) { S._error = S.List.CastBlockedTitle; return; }
        if (JobRunning && IsScenesWorkflowJob(_job?.Kind))
        {
            S._message = "A generate job is already running — watch progress.";
            return;
        }
        S._busy = true;
        S._error = null;
        try
        {
            await OpenJobModalAndPaintAsync(PreparingDefaultMessage, KindScene, scene: sn);
            await EnsureHubAsync();
            var targets = stale.Select(cn => (sn, cn)).ToList();
            // force via clip batch
            AdoptStartedJob(await S.Engine.StartClipBatchGenAsync(S._projectId, targets, resolution: _genResolution));
            if (_job is null)
                AdoptStartedJob((await S.Engine.GetJobAsync())?.Job);
            S._message = $"Regenerating {stale.Count} stale clip(s) in S{sn:D2}…";
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
            FailLocalPreparingJob(ex.Message);
        }
        finally { S._busy = false; }
    }

    internal async Task GenOneSceneAsync(int sn)
    {
        // Credits are rendered deterministically client-side — never sent to the video model.
        if (IsCreditsSceneNum(sn)) { await GenerateCreditsEntryAsync(sn); return; }
        if (!S.List.CastReady)
        {
            S._error = S.List.CastBlockedTitle;
            return;
        }

        S._busy = true;
        S._error = null;
        S._message = null;
        _showAdminJobLog = false;
        _progressFloor = 0;
        _progressFloorJobId = null;
        _lastListRefreshIndex = -1;
        _lastListRefreshScene = null;
        _lastListRefreshMessage = null;
        _pendingRegenScene = sn;
        try
        {
            await OpenJobModalAndPaintAsync(PreparingDefaultMessage, KindScene, scene: sn);
            await EnsureHubAsync();
            await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
            await S.Engine.StartSceneGenAsync(S._projectId, sn, onlyMissing: true, resolution: _genResolution);
            // Live progress card only — no duplicate "started" banner.
            var jobs = await S.Engine.GetJobAsync();
            AdoptStartedJob(jobs?.Job);
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
            FailLocalPreparingJob(ex.Message);
        }
        finally { S._busy = false; _pendingRegenScene = null; }
    }



    internal async Task StartBatchAsync()
    {
        if (S.IsSimpleFilm)
        {
            await StartSimpleMovieAsync();
            return;
        }
        if (S.List._selected.Count == 0) return;
        if (!S.List.CastReady)
        {
            S._error = S.List.CastBlockedTitle;
            return;
        }
        if (!await EnsureMediaFolderForVideoAsync()) return;
        if (JobRunning && IsScenesWorkflowJob(_job?.Kind))
        {
            S._message = "GeneratingBusy: a video job is already running. Watch the progress card.";
            return;
        }

        S._busy = true;
        S._error = null;
        S._message = null;
        _showAdminJobLog = false;
        ResetLiveProgressFloor();
        try
        {
            await RunSelectedBatchAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
            FailLocalPreparingJob(ex.Message);
        }
        finally { S._busy = false; }
    }

    private void ResetLiveProgressFloor()
    {
        _progressFloor = 0;
        _progressFloorJobId = null;
        _lastListRefreshIndex = -1;
        _lastListRefreshScene = null;
        _lastListRefreshMessage = null;
    }

    private async Task RunSelectedBatchAsync()
    {
        var list = S.List._selected.OrderBy(x => x).ToList();

        var creditsScenes = list.Where(IsCreditsSceneNum).ToList();
        var videoScenes = list.Where(sn => !creditsScenes.Contains(sn)).ToList();

        if (videoScenes.Count > 0)
        {
            // Credits-only selections have no server job (browser-rendered card) — do not open
            // the Generating modal there. For video work, open first so prep is not a dead UI.
            await OpenJobModalAndPaintAsync(PreparingDefaultMessage, KindBatch);
            await EnsureHubAsync();
            foreach (var sn in videoScenes)
                await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
            var videoModelOverride = S.Session.IsAdmin && !string.IsNullOrWhiteSpace(_selectedVideoModel)
                ? _selectedVideoModel
                : null;
            await S.Engine.StartBatchGenAsync(S._projectId, videoScenes, onlyMissing: true, resolution: _genResolution, videoModel: videoModelOverride, takeTrigger: VideoTakeKinds.FillHoles);
            var jobs = await S.Engine.GetJobAsync();
            AdoptStartedJob(jobs?.Job);
        }

        foreach (var sn in creditsScenes)
            await RenderCreditsSceneClientSideAsync(sn);
        if (creditsScenes.Count > 0)
            await SoftReloadAsync();
    }



    internal bool IsCreditsSceneNum(int sn) =>
        S.List._scenes?.FirstOrDefault(s => s.SceneNumber == sn)?.IsCredits == true;



    /// <summary>
    /// Single entry point every generation path funnels a credits scene through: render the deterministic
    /// card client-side instead of calling the video model, so no path (batch, per-clip regen, single
    /// scene) can ever produce a hallucinated credits clip. No cast gate — a credits card has no cast.
    /// </summary>
    internal async Task GenerateCreditsEntryAsync(int sn)
    {
        S._busy = true;
        S._error = null;
        S._message = "Rendering end-credits card…";
        await S.InvokeAsync(S.StateHasChanged);
        try
        {
            await RenderCreditsSceneClientSideAsync(sn);
            await SoftReloadAsync();
        }
        catch (Exception ex) { S._error = ex.Message; }
        finally { S._busy = false; }
    }



    /// <summary>
    /// Render the end-credits card client-side (deterministic canvas → ffmpeg.wasm roll) for every clip
    /// of a credits scene and store each as a normal on-disk clip, so the stitch concatenates it like any
    /// other clip. Replaces the hallucination-prone video-gen path for the credits scene.
    /// </summary>
    internal async Task RenderCreditsSceneClientSideAsync(int sn)
    {
        var (w, h) = ScenesListState.ResolutionDims(_genResolution);
        SceneDetail? detail = null;
        try { detail = (await S.Engine.GetSceneDetailAsync(S._projectId, sn))?.Scene; }
        catch { /* fall back to a single clip below */ }

        if (detail?.Clips is { Count: > 0 } clips)
        {
            foreach (var c in clips)
            {
                var dur = c.DurationSeconds > 0 ? c.DurationSeconds : (detail.PlannedDurationSeconds ?? 5);
                await RenderOneCreditsClipAsync(sn, c.ClipNumber, dur, w, h);
            }
        }
        else
        {
            await RenderOneCreditsClipAsync(sn, 1, detail?.PlannedDurationSeconds ?? 5, w, h);
        }
    }



    internal async Task RenderOneCreditsClipAsync(int sn, int clip, double durationSeconds, int width, int height)
    {
        S._message = "Rendering end-credits card…";
        await S.InvokeAsync(S.StateHasChanged);
        var (ok, err) = await S.Stitch.RenderAndStoreCreditsClipAsync(
            new ProjectClipRef(S._projectId, sn, clip), durationSeconds, width, height, fps: 24);
        if (!ok)
            S._error = err ?? "Credits card render failed";
        else
            S._message = "End-credits card ready";
    }



    internal async Task CancelAsync()
    {
        _ = await S.Engine.TryCancelJobAsync();
        if (_job is not null)
        {
            _job.Status = "cancelled";
            _job.Message = "Cancelled";
            _job.FinishedAt = DateTimeOffset.UtcNow;
        }
        S._busy = false;
        S._error = null;
        // Admins already see "Cancelled" on the finished-job card; one message is enough.
        S._message = S.Session.IsAdmin ? null : "Cancelled. You can try again when ready.";
        S.StateHasChanged();
    }



    internal async Task EnsureHubAsync()
    {
        await S.Hub.EnsureStartedAsync();
        try { await S.MediaFolder.EnsureHubHookAsync(); } catch { /* optional */ }
    }



    /// <summary>
    /// Video generation needs the media folder: clips are saved on this computer, not on the server
    /// (the provider copy of an extended clip is the combined video — only the local save is the
    /// clean standalone clip). Prompts to connect when needed; false = do not start.
    /// </summary>
    internal async Task<bool> EnsureMediaFolderForVideoAsync()
    {
        if (S.MediaFolder.IsConnected) return true;
        var connected = await S.MediaFolder.ConnectFolderAsync();
        if (connected || S.MediaFolder.IsConnected) return true;
        S._error = "Connect a folder first — clips are saved on this computer, not on the server.";
        return false;
    }

    internal async Task OpenGenerateConfirmAsync()
    {
        if (S.IsSimpleFilm)
        {
            await StartSimpleMovieAsync();
            return;
        }
        if (S.List._selected.Count == 0) return;
        if (!S.List.CastReady) { S._error = S.List.CastBlockedTitle; return; }
        if (!await EnsureMediaFolderForVideoAsync()) return;
        // Nothing missing → default to All as new takes so the operator is not sent to a
        // removed Regenerated button. Require an explicit scene selection either way.
        _generateForceAllTakes = S.ClipSel.EstimateSelectedClips() == 0;
        _showGenerateConfirm = true;
        await S.List.RefreshCostEstimateAsync();
    }

    /// <summary>
    /// Easy Start Step 3: existing voice-substitution job + client detect/overlay,
    /// gated on reviewed Dialogue Timing windows. Does not start video gen.
    /// </summary>
    internal async Task StartSimpleMovieAsync()
    {
        if (string.IsNullOrEmpty(S._projectId)) return;
        if (!await EnsureMediaFolderForVideoAsync()) return;
        if (JobRunning)
        {
            S._message = "A job is already running — watch progress instead of starting another.";
            return;
        }

        S._busy = true;
        S._error = null;
        S._message = null;
        try
        {
            if (!S.MediaFolder.IsConnected)
            {
                var connected = await S.MediaFolder.ConnectFolderAsync();
                if (!connected && !S.MediaFolder.IsConnected)
                {
                    S._error = "Connect your media folder so we can place your voice on the pictures.";
                    return;
                }
            }

            var chars = await S.Engine.GetCharactersAsync(S._projectId);
            var charKey = VoiceSubstitutionOverlayGate.FirstRecordedCharacterKey(chars?.Characters);
            if (string.IsNullOrWhiteSpace(charKey))
            {
                S._error = "Record a voice on story & voice first.";
                return;
            }

            DialogueTimingDoc? timing = null;
            ProjectVoiceAlignment? alignment = null;
            try { timing = await S.Engine.GetDialogueTimingAsync(S._projectId); } catch { /* gate treats null as not reviewed */ }
            try { alignment = await S.Engine.GetVoiceAlignmentAsync(S._projectId); } catch { /* same */ }
            if (!VoiceSubstitutionOverlayGate.CanOverlay(alignment, timing))
            {
                S._error = VoiceSubstitutionOverlayGate.ReviewRequiredMessage;
                return;
            }

            S._message = "Placing your voice…";
            var res = await S.VoiceSub.DubMovieInMyVoiceAsync(
                S._projectId,
                charKey: charKey,
                onProgress: s => { S._message = s; _ = S.InvokeAsync(S.StateHasChanged); });
            ApplySimpleMovieDubResult(res);
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
        }
        finally
        {
            S._busy = false;
        }
    }

    private void ApplySimpleMovieDubResult(ClientVoiceSubstitutionService.DubMovieResult res)
    {
        if (res.Ok)
            S._message = $"{res.ClipsDubbed} scene(s) in your voice"
                         + (res.ClipsFailed > 0 ? $" · {res.ClipsFailed} skipped" : "");
        else
            S._error = res.Error ?? VoiceSubstitutionOverlayGate.ReviewRequiredMessage;
    }



    internal void CloseGenerateConfirm() => _showGenerateConfirm = false;




    /// <summary>F4: full-film fill holes — all non-credits scenes, onlyMissing=true.</summary>
    internal async Task StartFillHolesMovieAsync()
    {
        if (S.IsSimpleFilm)
        {
            await StartSimpleMovieAsync();
            return;
        }
        if (!S.List.CastReady) { S._error = S.List.CastBlockedTitle; return; }
        if (S.List._scenes is null || S.List._scenes.Count == 0) { S._error = "No scenes loaded."; return; }
        // F5: don't double-start
        if (JobRunning && IsScenesWorkflowJob(_job?.Kind))
        {
            S._message = "A generate job is already running — watch progress instead of starting another.";
            return;
        }
        S.List._selected.Clear();
        foreach (var s in S.List._scenes.Where(x => !x.IsCredits))
            S.List._selected.Add(s.SceneNumber);
        await StartBatchAsync();
    }

    /// <summary>E4/E6: force regen selected (or all stale) scenes — onlyMissing=false for force full scene.</summary>
    internal async Task StartRegenStaleSelectedAsync()
    {
        if (S.List._selected.Count == 0)
            S.List.SelectStaleScenes();
        if (S.List._selected.Count == 0)
        {
            S._message = "No stale scenes to regenerate.";
            return;
        }
        await StartBatchForceSelectedAsync();
    }

    /// <summary>E4 full scope: re-generate every selected clip (force).</summary>
    internal async Task StartBatchForceSelectedAsync()
    {
        if (S.List._selected.Count == 0) return;
        if (!S.List.CastReady) { S._error = S.List.CastBlockedTitle; return; }
        if (JobRunning && IsScenesWorkflowJob(_job?.Kind))
        {
            S._message = "A generate job is already running — watch progress instead of starting another.";
            return;
        }
        if (!await EnsureMediaFolderForVideoAsync()) return;
        S._busy = true;
        S._error = null;
        S._message = null;
        _showAdminJobLog = false;
        ResetLiveProgressFloor();
        try
        {
            // Same split as the fill-holes batch: credits render in the browser, the rest is one server job.
            var creditsScenes = S.List._selected.Where(IsCreditsSceneNum).OrderBy(x => x).ToList();
            var list = S.List._selected.Where(sn => !IsCreditsSceneNum(sn)).OrderBy(x => x).ToList();
            if (list.Count > 0)
            {
                await OpenJobModalAndPaintAsync(PreparingDefaultMessage, KindBatch);
                await EnsureHubAsync();
                foreach (var sn in list)
                    await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
                var videoModelOverride = S.Session.IsAdmin && !string.IsNullOrWhiteSpace(_selectedVideoModel)
                    ? _selectedVideoModel
                    : null;
                await S.Engine.StartBatchGenAsync(S._projectId, list, onlyMissing: false, resolution: _genResolution, videoModel: videoModelOverride, takeTrigger: VideoTakeKinds.StaleRegen);
                var jobs = await S.Engine.GetJobAsync();
                AdoptStartedJob(jobs?.Job);
            }
            foreach (var sn in creditsScenes)
                await RenderCreditsSceneClientSideAsync(sn);
            if (creditsScenes.Count > 0)
                await SoftReloadAsync();
        }
            catch (Exception ex)
        {
            S._error = ex.Message;
            FailLocalPreparingJob(ex.Message);
        }
        finally { S._busy = false; }
    }

    internal async Task ConfirmGenerateAsync()
    {
        _showGenerateConfirm = false;
        if (_generateForceAllTakes)
            await StartBatchForceSelectedAsync();
        else
            await StartBatchAsync();
    }



    internal async Task LoadVideoModelsAsync()
    {
        try
        {
            var models = await S.Engine.GetSupportedModelsAsync();
            _videoModels = models.Where(m => m.Capability == "video" &&
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch { /* leave empty — modal then offers project default only */ }
    }


    }
}
