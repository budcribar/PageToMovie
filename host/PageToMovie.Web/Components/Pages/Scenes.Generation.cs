using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Domain: Generation — partial methods/properties for the Scenes page
public partial class Scenes
{

    /// <summary>True once the shot plan already has an end-credits scene (auto-inserted or re-added).</summary>
    internal bool HasCreditsScene => _scenes?.Any(s => s.IsCredits) == true;


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
        !Session.IsAdmin &&
        _job is not null &&
        IsScenesWorkflowJob(_job.Kind) &&
        string.Equals(_job.Status, "error", StringComparison.OrdinalIgnoreCase);


    internal bool ShowOperatorGenPartial =>
        !Session.IsAdmin &&
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
            pct = AdaptationPageBase.ComputeProgressPercent(
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
        _ = InvokeAsync(async () =>
        {
            if (Session.IsAdmin)
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
                    _sceneVideoKey = bust;
                    _inlineCompositeKey = bust;

                    if (_playSceneAfterRemux is int playSn)
                    {
                        // Play scene (auto-remux) — open player once remux finishes.
                        _playSceneAfterRemux = null;
                        _playingScene = playSn;
                        _showScenePlayer = true;
                        _message = $"Scene S{playSn:D2} ready — playing";
                    }
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "preview", StringComparison.OrdinalIgnoreCase))
                {
                    _previewVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _showPreviewPlayer = true;
                    _message = $"Preview ready — {_previewScenes.Count} scene(s): " +
                                string.Join(", ", _previewScenes.Select(s => $"S{s:D2}"));
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "scene", StringComparison.OrdinalIgnoreCase) &&
                         snap.Clip is int cn &&
                         snap.Scene is int gsn)
                {
                    _clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _message = $"Clip S{gsn:D2}C{cn:D2} finished — Play scene when you want the updated composite";
                    if (_selectedClip == cn)
                        SelectClip(cn);
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "video_edit", StringComparison.OrdinalIgnoreCase) &&
                         snap.Clip is int vecn &&
                         snap.Scene is int vesn)
                {
                    // New take saved as the active clip — bust the cache key so the inline
                    // <video> shows the edit result, and refresh the open clip's Takes list.
                    // SelectClip clears _message as its first line, so it must run BEFORE the
                    // completion message is set, not after (setting it after got silently wiped).
                    _clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_selectedClip == vecn)
                        SelectClip(vecn);
                    _message = $"Clip S{vesn:D2}C{vecn:D2} edited — saved as a new take";
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "batch", StringComparison.OrdinalIgnoreCase))
                {
                    // Batch generation finished — clear the scene selection so the toolbar no longer
                    // reads "Generate N scenes" (which looked like it would regenerate everything).
                    _selected.Clear();
                }
            }
            else if (ShouldRefreshSceneListWhileRunning(snap))
            {
                // Clips x/y + scene status pills: refresh while gen/remux is still running.
                await SoftReloadListLiveAsync();
            }

            StateHasChanged();
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
            var dto = await Engine.GetScenesAsync(_projectId);
            _scenes = dto?.Scenes ?? new List<SceneSummary>();
            await RefreshUncommittedStatusAsync();
            if (_selectedScene is int sn)
            {
                var detail = await Engine.GetSceneDetailAsync(_projectId, sn);
                _detail = detail?.Scene;
                if (_selectedClip is int cn && _detail is not null)
                    _clip = _detail.Clips.FirstOrDefault(c => c.ClipNumber == cn);
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
            _ = InvokeAsync(StateHasChanged);
    }


    internal async Task SoftReloadAsync()
    {
        try
        {
            var dto = await Engine.GetScenesAsync(_projectId);
            _scenes = dto?.Scenes ?? new List<SceneSummary>();
            await RefreshMyJobsAsync();
            await RefreshCastGateAsync();
            await RefreshResolutionLockAsync();
            if (_selectedScene is int sn)
            {
                var detail = await Engine.GetSceneDetailAsync(_projectId, sn);
                _detail = detail?.Scene;
                if (_selectedClip is int cn && _detail is not null)
                    _clip = _detail.Clips.FirstOrDefault(c => c.ClipNumber == cn);
            }
        }
        catch { /* ignore soft reload errors */ }
    }


    internal async Task RefreshMyJobsAsync()
    {
        try
        {
            var list = await Engine.GetJobsAsync(mine: true);
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


    internal async Task GenOneSceneAsync(int sn)
    {
        // Credits are rendered deterministically client-side — never sent to the video model.
        if (IsCreditsSceneNum(sn)) { await GenerateCreditsEntryAsync(sn); return; }
        if (!CastReady)
        {
            _error = CastBlockedTitle;
            return;
        }

        _busy = true;
        _error = null;
        _message = null;
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
            await EnsurePredecessorsUploadedAsync(await MissingClipTargetsAsync(sn));
            await Engine.StartSceneGenAsync(_projectId, sn, onlyMissing: true, resolution: _genResolution);
            // Live progress card only — no duplicate "started" banner.
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; _pendingRegenScene = null; }
    }


    internal async Task StartBatchAsync()
    {
        if (_selected.Count == 0) return;
        if (!CastReady)
        {
            _error = CastBlockedTitle;
            return;
        }

        _busy = true;
        _error = null;
        _message = null;
        _showAdminJobLog = false;
        _progressFloor = 0;
        _progressFloorJobId = null;
        _lastListRefreshIndex = -1;
        _lastListRefreshScene = null;
        _lastListRefreshMessage = null;
        try
        {
            var list = _selected.OrderBy(x => x).ToList();
            await EnsureHubAsync();

            // The end-credits card is rendered deterministically in the browser (canvas → ffmpeg.wasm),
            // not by the hallucination-prone video model — split it out of the server video batch.
            var creditsScenes = list.Where(IsCreditsSceneNum).ToList();
            var videoScenes = list.Where(sn => !creditsScenes.Contains(sn)).ToList();

            foreach (var sn in videoScenes)
            {
                await EnsurePredecessorsUploadedAsync(await MissingClipTargetsAsync(sn));
            }
            if (videoScenes.Count > 0)
            {
                var videoModelOverride = Session.IsAdmin && !string.IsNullOrWhiteSpace(_selectedVideoModel)
                    ? _selectedVideoModel
                    : null;
                await Engine.StartBatchGenAsync(_projectId, videoScenes, onlyMissing: true, resolution: _genResolution, videoModel: videoModelOverride);
                // Live progress card only — no duplicate "started" banner.
                var jobs = await Engine.GetJobAsync();
                _job = jobs?.Job;
            }

            foreach (var sn in creditsScenes)
            {
                await RenderCreditsSceneClientSideAsync(sn);
            }
            if (creditsScenes.Count > 0)
                await SoftReloadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }


    internal bool IsCreditsSceneNum(int sn) =>
        _scenes?.FirstOrDefault(s => s.SceneNumber == sn)?.IsCredits == true;


    /// <summary>
    /// Single entry point every generation path funnels a credits scene through: render the deterministic
    /// card client-side instead of calling the video model, so no path (batch, per-clip regen, single
    /// scene) can ever produce a hallucinated credits clip. No cast gate — a credits card has no cast.
    /// </summary>
    internal async Task GenerateCreditsEntryAsync(int sn)
    {
        _busy = true;
        _error = null;
        _message = "Rendering end-credits card…";
        await InvokeAsync(StateHasChanged);
        try
        {
            await RenderCreditsSceneClientSideAsync(sn);
            await SoftReloadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }


    /// <summary>
    /// Render the end-credits card client-side (deterministic canvas → ffmpeg.wasm roll) for every clip
    /// of a credits scene and store each as a normal on-disk clip, so the stitch concatenates it like any
    /// other clip. Replaces the hallucination-prone video-gen path for the credits scene.
    /// </summary>
    internal async Task RenderCreditsSceneClientSideAsync(int sn)
    {
        var (w, h) = ResolutionDims(_genResolution);
        SceneDetail? detail = null;
        try { detail = (await Engine.GetSceneDetailAsync(_projectId, sn))?.Scene; }
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
        _message = "Rendering end-credits card…";
        await InvokeAsync(StateHasChanged);
        var (ok, err) = await Stitch.RenderAndStoreCreditsClipAsync(
            _projectId, sn, clip, durationSeconds, width, height, fps: 24);
        if (!ok)
            _error = err ?? "Credits card render failed";
        else
            _message = "End-credits card ready";
    }


    internal async Task CancelAsync()
    {
        _busy = true;
        try
        {
            await Engine.CancelJobAsync();
            _message = "Cancel requested";
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }


    internal async Task EnsureHubAsync()
    {
        await Hub.EnsureStartedAsync();
        try { await MediaFolder.EnsureHubHookAsync(); } catch { /* optional */ }
    }


    internal async Task OpenGenerateConfirmAsync()
    {
        if (_selected.Count == 0) return;
        if (!CastReady) { _error = CastBlockedTitle; return; }
        _showGenerateConfirm = true;
        await RefreshCostEstimateAsync();
    }


    internal void CloseGenerateConfirm() => _showGenerateConfirm = false;


    internal async Task ConfirmGenerateAsync()
    {
        _showGenerateConfirm = false;
        await StartBatchAsync();
    }


    internal async Task LoadVideoModelsAsync()
    {
        try
        {
            var models = await Engine.GetSupportedModelsAsync();
            _videoModels = models.Where(m => m.Capability == "video" &&
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch { /* leave empty — modal then offers project default only */ }
    }

}
