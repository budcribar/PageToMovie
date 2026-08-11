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



    // Admin-only: video models offered as a one-off per-batch override in the Generate modal, so an
    // admin can A/B different generators without editing project Configuration. "" = project default.
    internal List<SupportedModelDto> _videoModels = new();


    internal string _selectedVideoModel = "";



    /// <summary>Video gen resolution (defaults from Configuration).</summary>
    internal string _genResolution = "480p";



    /// <summary>True once the shot plan already has an end-credits scene (auto-inserted or re-added).</summary>
    internal bool HasCreditsScene => S.List._scenes?.Any(s => s.IsCredits) == true;



    internal bool JobRunning =>
        string.Equals(_job?.Status, "running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_job?.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
        _myJobs.Any(j =>
            string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase));



    /// <summary>
    /// True when a clip/scene/remux job is active for this scene — hide stale composite player.
    /// </summary>
    internal bool IsSceneGenBusy(int sceneNumber)
    {
        if (sceneNumber <= 0) return false;
        if (_pendingRegenScene == sceneNumber) return true;

        static bool Active(string? status) =>
            string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase);

        bool Affects(JobSnapshot j)
        {
            if (!Active(j.Status) || !IsScenesWorkflowJob(j.Kind))
                return false;
            // Batch may include this scene; hide composite to avoid playing stale mux.
            // Remux job.Scene goes null during the WIP-stitch phase ("Combining scenes
            // into movie…") — treat that the same way: hide every composite rather than
            // let one sit there mid-rewrite with a Play button live on it.
            if (string.Equals(j.Kind, "batch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Kind, "remux", StringComparison.OrdinalIgnoreCase))
                return true;
            return j.Scene is int sn && sn == sceneNumber;
        }

        if (_job is not null && Affects(_job))
            return true;
        return _myJobs.Any(Affects);
    }



    /// <summary>Jobs that belong on Scenes (not leftover stage2 / character jobs).</summary>
    internal static bool IsScenesWorkflowJob(string? kind) =>
        kind is "scene" or "batch" or "remux" or "music" or "lip_sync" or "video_edit";



    /// <summary>
    /// One compact progress card while video work runs — operators and admin.
    /// No badges, provider names, raw engine message, or live log.
    /// </summary>
    internal bool ShowLiveGenProgress =>
        _job is not null &&
        IsScenesWorkflowJob(_job.Kind) &&
        (_job.Status is "running" or "queued");



    internal bool ShowOperatorGenError =>
        !S.Session.IsAdmin &&
        _job is not null &&
        IsScenesWorkflowJob(_job.Kind) &&
        string.Equals(_job.Status, "error", StringComparison.OrdinalIgnoreCase);



    internal bool ShowOperatorGenPartial =>
        !S.Session.IsAdmin &&
        _job is not null &&
        IsScenesWorkflowJob(_job.Kind) &&
        string.Equals(_job.Status, "partial", StringComparison.OrdinalIgnoreCase);



    /// <summary>Short outcome label for the live bar (no provider / path / status dump).</summary>
    internal static string LiveGenStatusLabel(JobSnapshot job)
    {
        var kind = job.Kind ?? "";
        if (string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
            return "Waiting…";

        if (string.Equals(kind, "music", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(job.Message) ? job.Message : "Scoring background music…";
        if (string.Equals(kind, "lip_sync", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(job.Message) ? job.Message : "Lip-syncing dialogue…";

        if (string.Equals(kind, "remux", StringComparison.OrdinalIgnoreCase))
        {
            var msg = job.Message ?? "";
            // Engine already sends short operator lines for remux ("Combining scene 2 of 5…").
            if (msg.StartsWith("Combining", StringComparison.OrdinalIgnoreCase) ||
                msg.StartsWith("Measuring", StringComparison.OrdinalIgnoreCase))
                return msg.Length > 80 ? msg[..77] + "…" : msg;
            return "Combining video…";
        }

        if (job.Total > 0 &&
            !string.Equals(kind, "remux", StringComparison.OrdinalIgnoreCase))
        {
            // Clip gen: Index is current clip (1..Total).
            var display = job.Index <= 0 ? 1 : Math.Min(job.Index, job.Total);
            return $"Generating… {display} of {job.Total}";
        }

        if (string.Equals(kind, "scene", StringComparison.OrdinalIgnoreCase) && job.Clip is int)
            return "Generating clip…";
        if (string.Equals(kind, "scene", StringComparison.OrdinalIgnoreCase))
            return "Generating scene…";
        if (string.Equals(kind, "batch", StringComparison.OrdinalIgnoreCase))
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
        if (string.Equals(kind, "remux", StringComparison.OrdinalIgnoreCase))
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
                waiting: string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase),
                jobRunning: true,
                startedAt: job.StartedAt);
        }

        // Monotonic while the same job runs — never bounce backward on mid-phase resets.
        if (pct < _progressFloor)
            pct = _progressFloor;
        else
            _progressFloor = pct;
        return pct;
    }



    internal void OnJobUpdated(JobSnapshot snap)
    {
        _job = snap;
        _ = S.InvokeAsync(async () =>
        {
            if (S.Session.IsAdmin)
                await RefreshMyJobsAsync();

            if (snap.Status is "done" or "partial" or "error" or "cancelled")
            {
                _lastListRefreshIndex = -1;
                _lastListRefreshScene = null;
                _lastListRefreshMessage = null;
                await SoftReloadAsync();
                if (snap.Status == "done" &&
                    string.Equals(snap.Kind, "remux", StringComparison.OrdinalIgnoreCase))
                {
                    // Bust cache so next manual play / inline preview loads the new file.
                    var bust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S.Playback._sceneVideoKey = bust;
                    S.Playback._inlineCompositeKey = bust;

                    if (S.Playback._playSceneAfterRemux is int playSn)
                    {
                        // Play scene (auto-remux) — open player once remux finishes.
                        S.Playback._playSceneAfterRemux = null;
                        S.Playback._playingScene = playSn;
                        S.Playback._showScenePlayer = true;
                        S._message = $"Scene S{playSn:D2} ready — playing";
                    }
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "preview", StringComparison.OrdinalIgnoreCase))
                {
                    S.Playback._previewVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S.Playback._showPreviewPlayer = true;
                    S._message = $"Preview ready — {S.Playback._previewScenes.Count} scene(s): " +
                                string.Join(", ", S.Playback._previewScenes.Select(s => $"S{s:D2}"));
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "scene", StringComparison.OrdinalIgnoreCase) &&
                         snap.Clip is int cn &&
                         snap.Scene is int gsn)
                {
                    S.Playback._clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S._message = $"Clip S{gsn:D2}C{cn:D2} finished — Play scene when you want the updated composite";
                    if (S.ClipForm._selectedClip == cn)
                        S.ClipForm.SelectClip(cn);
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "video_edit", StringComparison.OrdinalIgnoreCase) &&
                         snap.Clip is int vecn &&
                         snap.Scene is int vesn)
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
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "batch", StringComparison.OrdinalIgnoreCase))
                {
                    // Batch generation finished — clear the scene selection so the toolbar no longer
                    // reads "Generate N scenes" (which looked like it would regenerate everything).
                    S.List._selected.Clear();
                }
            }
            else if (ShouldRefreshSceneListWhileRunning(snap))
            {
                // Clips x/y + scene status pills: refresh while gen/remux is still running.
                await SoftReloadListLiveAsync();
            }

            S.StateHasChanged();
        });
    }



    /// <summary>
    /// True when a live scene/batch/remux job advanced far enough that list counts may have changed.
    /// Throttled so long ffmpeg/API polls don't hammer GetScenes.
    /// </summary>
    internal bool ShouldRefreshSceneListWhileRunning(JobSnapshot snap)
    {
        if (!IsScenesWorkflowJob(snap.Kind))
            return false;
        if (snap.Status is not ("running" or "queued"))
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
        }
        catch { /* ignore mid-job refresh errors */ }
        finally { _listRefreshInFlight = false; }
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
            (_job.Status is "done" or "partial" or "error" or "cancelled"))
            _ = S.InvokeAsync(S.StateHasChanged);
    }



    internal async Task SoftReloadAsync()
    {
        try
        {
            var dto = await S.Engine.GetScenesAsync(S._projectId);
            S.List._scenes = dto?.Scenes ?? new List<SceneSummary>();
            await RefreshMyJobsAsync();
            await S.List.RefreshCastGateAsync();
            await S.List.RefreshResolutionLockAsync();
            if (S.List._selectedScene is int sn)
            {
                var detail = await S.Engine.GetSceneDetailAsync(S._projectId, sn);
                S.List._detail = detail?.Scene;
                if (S.ClipForm._selectedClip is int cn && S.List._detail is not null)
                    S.ClipForm._clip = S.List._detail.Clips.FirstOrDefault(c => c.ClipNumber == cn);
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
                    string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
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
            await EnsureHubAsync();
            await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
            await S.Engine.StartSceneGenAsync(S._projectId, sn, onlyMissing: false, resolution: _genResolution);
            _job = (await S.Engine.GetJobAsync())?.Job;
        }
        catch (Exception ex) { S._error = ex.Message; }
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
            await EnsureHubAsync();
            var targets = stale.Select(cn => (sn, cn)).ToList();
            // force via clip batch
            _job = await S.Engine.StartClipBatchGenAsync(S._projectId, targets, resolution: _genResolution);
            if (_job is null)
                _job = (await S.Engine.GetJobAsync())?.Job;
            S._message = $"Regenerating {stale.Count} stale clip(s) in S{sn:D2}…";
        }
        catch (Exception ex) { S._error = ex.Message; }
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
            await EnsureHubAsync();
            await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
            await S.Engine.StartSceneGenAsync(S._projectId, sn, onlyMissing: true, resolution: _genResolution);
            // Live progress card only — no duplicate "started" banner.
            var jobs = await S.Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { S._error = ex.Message; }
        finally { S._busy = false; _pendingRegenScene = null; }
    }



    internal async Task StartBatchAsync()
    {
        if (S.List._selected.Count == 0) return;
        if (!S.List.CastReady)
        {
            S._error = S.List.CastBlockedTitle;
            return;
        }
        // F5: single full-film/batch job — second caller monitors
        if (JobRunning && IsScenesWorkflowJob(_job?.Kind))
        {
            S._message = "GeneratingBusy: a video job is already running. Watch the progress card.";
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
        try
        {
            var list = S.List._selected.OrderBy(x => x).ToList();
            await EnsureHubAsync();

            // The end-credits card is rendered deterministically in the browser (canvas → ffmpeg.wasm),
            // not by the hallucination-prone video model — split it out of the server video batch.
            var creditsScenes = list.Where(IsCreditsSceneNum).ToList();
            var videoScenes = list.Where(sn => !creditsScenes.Contains(sn)).ToList();

            foreach (var sn in videoScenes)
            {
                await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
            }
            if (videoScenes.Count > 0)
            {
                var videoModelOverride = S.Session.IsAdmin && !string.IsNullOrWhiteSpace(_selectedVideoModel)
                    ? _selectedVideoModel
                    : null;
                await S.Engine.StartBatchGenAsync(S._projectId, videoScenes, onlyMissing: true, resolution: _genResolution, videoModel: videoModelOverride, takeTrigger: VideoTakeKinds.FillHoles);
                // Live progress card only — no duplicate "started" banner.
                var jobs = await S.Engine.GetJobAsync();
                _job = jobs?.Job;
            }

            foreach (var sn in creditsScenes)
            {
                await RenderCreditsSceneClientSideAsync(sn);
            }
            if (creditsScenes.Count > 0)
                await SoftReloadAsync();
        }
        catch (Exception ex) { S._error = ex.Message; }
        finally { S._busy = false; }
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

        var clips = detail?.Clips;
        if (clips is { Count: > 0 })
        {
            foreach (var c in clips)
            {
                var dur = c.DurationSeconds > 0 ? c.DurationSeconds : (detail?.PlannedDurationSeconds ?? 5);
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
            S._projectId, sn, clip, durationSeconds, width, height, fps: 24);
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
        S._message = "Cancelled. You can try again when ready.";
        S.StateHasChanged();
    }



    internal async Task EnsureHubAsync()
    {
        await S.Hub.EnsureStartedAsync();
        try { await S.MediaFolder.EnsureHubHookAsync(); } catch { /* optional */ }
    }



    internal async Task OpenGenerateConfirmAsync()
    {
        if (S.List._selected.Count == 0) return;
        if (!S.List.CastReady) { S._error = S.List.CastBlockedTitle; return; }
        _showGenerateConfirm = true;
        await S.List.RefreshCostEstimateAsync();
    }



    internal void CloseGenerateConfirm() => _showGenerateConfirm = false;




    /// <summary>F4: full-film fill holes — all non-credits scenes, onlyMissing=true.</summary>
    internal async Task StartFillHolesMovieAsync()
    {
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
        S._busy = true;
        S._error = null;
        S._message = null;
        _showAdminJobLog = false;
        try
        {
            var list = S.List._selected.OrderBy(x => x).Where(sn => !IsCreditsSceneNum(sn)).ToList();
            await EnsureHubAsync();
            foreach (var sn in list)
                await S.ClipRegen.EnsurePredecessorsUploadedAsync(await S.ClipRegen.MissingClipTargetsAsync(sn));
            if (list.Count > 0)
            {
                await S.Engine.StartBatchGenAsync(S._projectId, list, onlyMissing: false, resolution: _genResolution, takeTrigger: VideoTakeKinds.StaleRegen);
                var jobs = await S.Engine.GetJobAsync();
                _job = jobs?.Job;
            }
        }
        catch (Exception ex) { S._error = ex.Message; }
        finally { S._busy = false; }
    }

    internal async Task ConfirmGenerateAsync()
    {
        _showGenerateConfirm = false;
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
