using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes
{
    private bool IsSimpleFilm =>
        ActiveProject.IsSimpleVoice
        || (Nav.ToAbsoluteUri(Nav.Uri).Query?.Contains("simple=1", StringComparison.OrdinalIgnoreCase) ?? false);

    private bool _busy;
    private bool _gateChecked;
    private string? _error;
    private string? _message;
    private string _projectId = "";
    private List<string> _projectIds = new();
    private string _pickSetting = "";
    private string _pickCharacter = "";
    private string _pickLocation = "";
    private bool _showFilters;

    /// <summary>Video gen resolution (defaults from Configuration).</summary>
    private string _genResolution = "480p";
    /// <summary>Resolution already used by this project's on-disk clips, if consistent — null when unset.</summary>
    private string? _resolutionLock;
    private bool ResolutionLocked => !string.IsNullOrWhiteSpace(_resolutionLock) || (_scenes is not null && _scenes.Sum(s => s.ClipsOnDisk) > 0);
    private string _sortBy = "number"; // "number" or "duration"
    private bool _sortAscending = true;
    /// <summary>Clip table: when true, sort by duration; else keep plan order (clip number).</summary>
    private bool _clipSortByDuration;
    private bool _clipSortAscending = true;

    private void ToggleSort(string column)
    {
        if (_sortBy == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortBy = column;
            _sortAscending = true;
        }
    }

    private void ToggleClipDurationSort()
    {
        if (_clipSortByDuration)
            _clipSortAscending = !_clipSortAscending;
        else
        {
            _clipSortByDuration = true;
            _clipSortAscending = true;
        }
    }

    private IEnumerable<SceneSummary> SortedVisibleScenes
    {
        get
        {
            var scenes = VisibleScenes;
            return _sortBy switch
            {
                "duration" => _sortAscending
                    ? scenes.OrderBy(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0)
                    : scenes.OrderByDescending(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0),
                _ => _sortAscending
                    ? scenes.OrderBy(s => s.SceneNumber)
                    : scenes.OrderByDescending(s => s.SceneNumber)
            };
        }
    }

    /// <summary>Clips in open scene, optionally sorted by actual/plan duration.</summary>
    private IEnumerable<ClipSummary> SortedDetailClips
    {
        get
        {
            if (_detail?.Clips is null)
                return Array.Empty<ClipSummary>();
            if (!_clipSortByDuration)
                return _detail.Clips.OrderBy(c => c.ClipNumber);
            static double Dur(ClipSummary c) =>
                c.ActualDurationSeconds ?? (c.DurationSeconds > 0 ? c.DurationSeconds : 0);
            return _clipSortAscending
                ? _detail.Clips.OrderBy(Dur).ThenBy(c => c.ClipNumber)
                : _detail.Clips.OrderByDescending(Dur).ThenBy(c => c.ClipNumber);
        }
    }
    /// <summary>Cost estimate at the current resolution, refreshed on load and resolution change.</summary>
    private CostReport? _costReport;
    /// <summary>Project-wide cast gate: every character voice + locked image before video spend.</summary>
    private bool _castChecked;
    private bool _castReady;
    private int? _castReadyCount;
    private int? _castTotal;
    private List<string> _castMissing = new();
    private List<SceneSummary>? _scenes;

    /// <summary>True once the shot plan already has an end-credits scene (auto-inserted or re-added).</summary>
    private bool HasCreditsScene => _scenes?.Any(s => s.IsCredits) == true;
    private HashSet<int> _selected = new();
    private string _selectionMode = ""; // "" | incomplete | all
    private int? _selectedScene;
    private SceneDetail? _detail;
    private int? _selectedClip;
    private ClipSummary? _clip;
    private (int Scene, int Clip)? _deleteClipTarget;
    private int? _deleteSceneTarget;
    private ClipEditRequest? _clipEditor;
    private bool _clipEditorIsNew;
    private HashSet<string> _clipEditorCast = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Multi-select clip numbers within the currently open scene's clip table, for batch regen.</summary>
    private readonly HashSet<int> _selectedClips = new();
    private JobSnapshot? _job;
    private List<JobSnapshot> _myJobs = new();
    /// <summary>Admin-only: expand finished-job log under the compact result card.</summary>
    private bool _showAdminJobLog;
    /// <summary>Highest progress % shown for the current job — bar never bounces backward.</summary>
    private int _progressFloor;
    private string? _progressFloorJobId;
    /// <summary>Throttle mid-job list refresh so clips x/y + status pills stay live without thrashing.</summary>
    private int _lastListRefreshIndex = -1;
    private int? _lastListRefreshScene;
    private string? _lastListRefreshMessage;
    private DateTimeOffset _lastListRefreshAt = DateTimeOffset.MinValue;
    private bool _listRefreshInFlight;
    private int? _playSceneAfterRemux;
    private bool _showScenePlayer;
    private int? _playingScene;
    private long _sceneVideoKey;
    private long _inlineCompositeKey;
    private long _clipVideoKey;
    /// <summary>"Play selected" — multi-scene (possibly non-contiguous) client-stitched preview.</summary>
    private bool _showPreviewPlayer;
    private long _previewVideoKey;
    private List<int> _previewScenes = new();
    private string? _clientPreviewUrl;
    private string? _clientSceneUrl;
    private bool _clientStitching;
    private string? _clientStitchStatus;

    private bool JobRunning =>
        string.Equals(_job?.Status, "running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_job?.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
        _myJobs.Any(j =>
            string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Set for the brief window between kicking off a regen and the job snapshot round-trip
    /// confirming it server-side — closes the gap where <see cref="IsSceneGenBusy"/> would
    /// otherwise still see the previous (already-finished) job and let a stale composite show.
    /// </summary>
    private int? _pendingRegenScene;

    /// <summary>
    /// True when a clip/scene/remux job is active for this scene — hide stale composite player.
    /// </summary>
    private bool IsSceneGenBusy(int sceneNumber)
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

    /// <summary>
    /// True when this exact clip is the one currently being (re)generated — the server updates
    /// the job's Scene/Clip to whichever item it's actively working on, for both single-clip
    /// regen (kind "scene") and multi-select batch regen (kind "batch"). Used to avoid showing
    /// a stale "on disk" pill or letting Play open the file mid-overwrite.
    /// </summary>
    private bool IsClipGenBusy(int clipNumber)
    {
        if (_detail is null) return false;
        var sn = _detail.SceneNumber;
        if (_pendingRegenScene == sn) return true;

        bool Affects(JobSnapshot j) =>
            (string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)) &&
            IsScenesWorkflowJob(j.Kind) &&
            j.Scene == sn && j.Clip == clipNumber;

        if (_job is not null && Affects(_job))
            return true;
        return _myJobs.Any(Affects);
    }

    /// <summary>True when every cast member has approved voice + locked look (or voice-only + voice).</summary>
    private bool CastReady => _castReady;

    private string CastBlockedTitle =>
        _castMissing.Count > 0
            ? $"Approve voice + locked image first: {string.Join(", ", _castMissing.Take(4))}{(_castMissing.Count > 4 ? "…" : "")}"
            : "Approve voice + locked image for every character before generating video";

    /// <summary>
    /// Clip N (N&gt;1) needs clip N-1 on disk — Imagine continues from the previous video.
    /// </summary>
    private bool PreviousClipMissing(int clipNumber)
    {
        if (clipNumber <= 1 || _detail is null) return false;
        var prev = _detail.Clips.FirstOrDefault(c => c.ClipNumber == clipNumber - 1);
        return prev is null || !prev.OnDisk;
    }

    /// <summary>Jobs that belong on Scenes (not leftover stage2 / character jobs).</summary>
    private static bool IsScenesWorkflowJob(string? kind) =>
        kind is "scene" or "batch" or "remux" or "music" or "lip_sync" or "video_edit";

    /// <summary>
    /// One compact progress card while video work runs — operators and admin.
    /// No badges, provider names, raw engine message, or live log.
    /// </summary>
    private bool ShowLiveGenProgress =>
        _job is not null &&
        IsScenesWorkflowJob(_job.Kind) &&
        (_job.Status is "running" or "queued");

    private bool ShowOperatorGenError =>
        !Session.IsAdmin &&
        _job is not null &&
        IsScenesWorkflowJob(_job.Kind) &&
        string.Equals(_job.Status, "error", StringComparison.OrdinalIgnoreCase);

    private bool ShowOperatorGenPartial =>
        !Session.IsAdmin &&
        _job is not null &&
        IsScenesWorkflowJob(_job.Kind) &&
        string.Equals(_job.Status, "partial", StringComparison.OrdinalIgnoreCase);

    /// <summary>Short outcome label for the live bar (no provider / path / status dump).</summary>
    private static string LiveGenStatusLabel(JobSnapshot job)
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
    private int LiveGenProgressPercent(JobSnapshot job)
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

    private bool SelectedLockedByOther =>
        _scenes is not null &&
        _selected.Any(sn => _scenes.Any(s => s.SceneNumber == sn && s.LockedByOther));

    private bool DetailLockedByOther =>
        _detail is not null &&
        (_scenes?.FirstOrDefault(s => s.SceneNumber == _detail.SceneNumber)?.LockedByOther ?? false);

    private string? _detailLockOwner =>
        _detail is null
            ? null
            : _scenes?.FirstOrDefault(s => s.SceneNumber == _detail.SceneNumber)?.LockOwnerUserId;

    private string SelectionMode => _selectionMode;

    // Tri-state progress glyph for a scene's clip generation:
    //   ○ (muted)   nothing generated yet, or no clips planned
    //   ◐ (warning) some clips on disk, not all
    //   ● (success) every planned clip generated
    private (string Glyph, string Css, string Title) SceneProgressGlyph(SceneSummary s)
    {
        if (s.ClipCount <= 0)
            return ("○", "text-muted", "No clips planned");
        if (s.ClipsComplete)
            return ("●", "text-success", $"All {s.ClipCount} clips generated");
        if (s.ClipsOnDisk > 0)
            return ("◐", "text-warning", $"{s.ClipsOnDisk} of {s.ClipCount} clips generated");
        return ("○", "text-muted", $"0 of {s.ClipCount} clips generated");
    }

    private List<string> CharacterOptions
    {
        get
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_scenes is not null)
            {
                foreach (var s in _scenes)
                {
                    foreach (var c in s.CharactersOnScreen)
                        if (!string.IsNullOrWhiteSpace(c)) set.Add(c);
                }
            }
            if (_detail is not null)
            {
                foreach (var c in _detail.CharactersOnScreen)
                    if (!string.IsNullOrWhiteSpace(c)) set.Add(c);

                if (_detail.Clips is not null)
                {
                    foreach (var cl in _detail.Clips)
                    {
                        foreach (var c in cl.CharactersOnScreen)
                            if (!string.IsNullOrWhiteSpace(c)) set.Add(c);
                    }
                }
            }
            if (_clipEditorCast is not null)
            {
                foreach (var c in _clipEditorCast)
                    if (!string.IsNullOrWhiteSpace(c)) set.Add(c);
            }
            if (_castMissing is not null)
            {
                foreach (var c in _castMissing)
                    if (!string.IsNullOrWhiteSpace(c)) set.Add(c);
            }
            return set.OrderBy(c => ShortChar(c), StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private List<string> LocationOptions =>
        _scenes is null
            ? new List<string>()
            : _scenes
                .SelectMany(s =>
                {
                    var list = new List<string>(s.LocationIds);
                    if (!string.IsNullOrWhiteSpace(s.PrimaryLocationId))
                        list.Add(s.PrimaryLocationId!);
                    return list;
                })
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ShortLoc, StringComparer.OrdinalIgnoreCase)
                .ToList();

    private bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(_pickCharacter) ||
        !string.IsNullOrWhiteSpace(_pickLocation) ||
        !string.IsNullOrWhiteSpace(_pickSetting);

    private void ClearFilters()
    {
        _pickCharacter = "";
        _pickLocation = "";
        _pickSetting = "";
    }

    private IEnumerable<SceneSummary> FilteredScenes
    {
        get
        {
            if (_scenes is null) return Enumerable.Empty<SceneSummary>();
            IEnumerable<SceneSummary> list = _scenes;
            if (!string.IsNullOrWhiteSpace(_pickCharacter))
            {
                var match = _pickCharacter;
                list = list.Where(s => s.CharactersOnScreen.Any(c =>
                    string.Equals(c, match, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ShortChar(c), match, StringComparison.OrdinalIgnoreCase)));
            }
            if (!string.IsNullOrWhiteSpace(_pickLocation))
            {
                var match = _pickLocation;
                list = list.Where(s =>
                    string.Equals(s.PrimaryLocationId, match, StringComparison.OrdinalIgnoreCase) ||
                    s.LocationIds.Any(l => string.Equals(l, match, StringComparison.OrdinalIgnoreCase)));
            }
            if (!string.IsNullOrWhiteSpace(_pickSetting))
            {
                var text = _pickSetting;
                list = list.Where(s => (s.Setting ?? "").Contains(text, StringComparison.OrdinalIgnoreCase));
            }
            return list;
        }
    }

    private List<SceneSummary> VisibleScenes => FilteredScenes.ToList();

    private void SelectByCharacter()
    {
        if (_scenes is null) return;
        if (string.IsNullOrWhiteSpace(_pickCharacter)) return;
        var match = _pickCharacter;
        _selected.Clear();
        foreach (var s in _scenes.Where(s =>
            s.CharactersOnScreen.Any(c =>
                string.Equals(c, match, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ShortChar(c), match, StringComparison.OrdinalIgnoreCase))))
        {
            _selected.Add(s.SceneNumber);
        }
    }

    private void SelectByLocation()
    {
        if (_scenes is null || string.IsNullOrWhiteSpace(_pickLocation)) return;
        var match = _pickLocation;
        _selected.Clear();
        foreach (var s in _scenes.Where(s =>
            string.Equals(s.PrimaryLocationId, match, StringComparison.OrdinalIgnoreCase) ||
            s.LocationIds.Any(l => string.Equals(l, match, StringComparison.OrdinalIgnoreCase))))
        {
            _selected.Add(s.SceneNumber);
        }
    }

    /// <summary>Select scenes that still need clips (not fully on disk).</summary>
    private void SelectMissingScenes()
    {
        if (_scenes is null) return;
        _selected.Clear();
        foreach (var s in VisibleScenes.Where(s => !s.ClipsComplete || s.ClipsOnDisk < s.ClipCount))
            _selected.Add(s.SceneNumber);
        _selectionMode = _selected.Count > 0 ? "missing" : "";
    }

    /// <summary>Select clips in the open scene that are not on disk yet.</summary>
    private void SelectMissingClips()
    {
        if (_detail is null) return;
        _selectedClips.Clear();
        foreach (var c in _detail.Clips.Where(c => !c.OnDisk))
            _selectedClips.Add(c.ClipNumber);
    }

    /// <summary>Select clips in the open scene that have dialogue mismatches or speaker swaps.</summary>
    private void SelectMismatchedClips()
    {
        if (_detail is null) return;
        _selectedClips.Clear();
        foreach (var c in _detail.Clips.Where(c => c.DialogueVerification is { Status: "mismatch" } or { Status: "speaker_swap" }))
            _selectedClips.Add(c.ClipNumber);
    }

    private void RequestDeleteScene(int sn) => _deleteSceneTarget = sn;

    private async Task ConfirmDeleteSceneAsync()
    {
        if (_deleteSceneTarget is not int sn) return;
        _deleteSceneTarget = null;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            // Persist: remove the scene from the shot plan (blueprint) so it doesn't reappear on reload.
            var res = await Engine.DeleteSceneAsync(_projectId, sn);
            if (!res.Ok)
            {
                _error = res.Error ?? "Could not delete the scene.";
                return;
            }
            _selected.Remove(sn);
            _message = res.Message ?? $"Deleted Scene {sn:D2}";
            await SoftReloadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AddSceneAsync(bool credits)
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.AddSceneAsync(_projectId, credits);
            if (!res.Ok)
            {
                _error = res.Error ?? "Could not add the scene.";
                return;
            }
            _message = res.Message ?? (credits ? "Added credits scene" : $"Added Scene {res.Scene:D2}");
            await SoftReloadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private Task AdjustFitLengthAsync() => ConfirmScreenplayAdjustAndNavigateAsync("adaptation/trim");

    private Task AdjustEmbellishAsync() => ConfirmScreenplayAdjustAndNavigateAsync("adaptation/embellish");

    // Read-only: just open the screenplay view. Unlike Fit length / Embellish it does not re-open
    // (un-approve) the screenplay, so no confirm — the user asked to "go back and see" it from Film.
    private void ViewScreenplay() => Nav.NavigateTo("adaptation/screenplay");

    /// <summary>
    /// Navigate to a screenplay-shaping step (Fit length / Enrich). Those edit the screenplay, which
    /// un-approves it and re-gates cast, so confirm first rather than surprising the user mid-Film.
    /// </summary>
    private async Task ConfirmScreenplayAdjustAndNavigateAsync(string route)
    {
        if (JobRunning) return;
        var ok = await JS.InvokeAsync<bool>(
            "confirm",
            "This opens the screenplay to change it. You'll re-approve the screenplay afterward, " +
            "and the cast will re-check against the updated script. Continue?");
        if (ok)
            Nav.NavigateTo(route);
    }

    /// <summary>
    /// Replan from the screenplay — scoped to the checked scenes when any are selected, so editing
    /// the Fountain (e.g. just the title) and regenerating doesn't re-prompt the AI for scenes whose
    /// script text didn't change (Stage2PlannerService merges a scoped replan into the existing
    /// blueprint instead of rebuilding it from scratch). Falls back to every scene — the original
    /// "restore missing scenes" behavior — when nothing is checked.
    /// </summary>
    private async Task RebuildShotPlanAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var scoped = _selected.Count > 0;
            await Engine.StartStage2Async(new StartStage2Request
            {
                ProjectId = _projectId,
                Scenes = scoped ? string.Join(",", _selected.OrderBy(x => x)) : "all"
            });
            _message = scoped
                ? $"Regenerating {_selected.Count} selected scene(s) from the screenplay…"
                : "Rebuilding shot plan from screenplay…";
            await SoftReloadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally { _busy = false; }
    }

    private void ResetPickers()
    {
        _pickSetting = "";
        _pickCharacter = "";
        _pickLocation = "";
    }

    protected override async Task OnInitializedAsync()
    {
        await ActiveProject.EnsureLoadedAsync(Engine);
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        MediaFolder.Changed += OnMediaFolderChanged;
        try
        {
            try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }

            var projs = await Engine.GetProjectsAsync();
            _projectIds = projs?.Projects.Select(p => p.Id ?? "").Where(s => s.Length > 0).ToList()
                          ?? new List<string>();
            if (projs?.Active?.Id is { Length: > 0 } aid &&
                _projectIds.Exists(id => string.Equals(id, aid, StringComparison.OrdinalIgnoreCase)))
            {
                _projectId = aid;
                ActiveProject.Set(aid, projs.Active?.Label ?? projs.Active?.Title ?? aid);
            }
            else if (_projectIds.Count > 0)
                _projectId = _projectIds[0];
            else
                _projectId = "";

            await ActiveProject.RefreshReadinessAsync(Engine);
            _gateChecked = true;
            if (!string.IsNullOrEmpty(_projectId))
                await Caps.RefreshAsync(Engine);

            if (string.IsNullOrEmpty(_projectId) || !ActiveProject.CanScenes)
                return;

            await LoadGenResolutionFromConfigAsync();
            await LoadAudioModelsAsync();
            if (Session.IsAdmin)
                await LoadVideoModelsAsync();

            try
            {
                await Hub.StartAsync();
                await MediaFolder.EnsureHubHookAsync();
                // Contextual sync: pull this project's media to the local folder now that we're
                // actually in it (this replaces the old sync-on-every-page-load behaviour).
                if (!MediaFolder.IsConnected) await MediaFolder.TryReconnectAsync();
                MediaFolder.TriggerAutoSyncIfConnected();
            }
            catch { /* SignalR / media folder optional for browse */ }

            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
            if (Session.IsAdmin)
                await RefreshMyJobsAsync();

            await ReloadListAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }

    private void OnMediaFolderChanged() => _ = InvokeAsync(async () =>
    {
        if (_showScenePlayer && _playingScene is int sn && !MediaFolder.IsSyncing && string.IsNullOrEmpty(_clientSceneUrl))
        {
            await PlaySceneCompositeAsync(sn);
        }
        StateHasChanged();
    });

    private void DismissLocalSaveWarning() => MediaFolder.DismissLocalSaveWarning();

    private async Task ConnectMediaFolderFromWarningAsync()
    {
        try
        {
            if (MediaFolder.NeedsReconnect)
                await MediaFolder.ReconnectAsync();
            else
                await MediaFolder.ConnectFolderAsync();
            await MediaFolder.EnsureHubHookAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private string _preferredVideoEditor = "ClipChamp";

    private async Task LoadGenResolutionFromConfigAsync()
    {
        try
        {
            var dto = await Engine.GetConfigAsync(_projectId);
            if (dto?.Config is { } cfg)
            {
                if (cfg.TryGetValue("resolution", out var el) &&
                    el.ValueKind == JsonValueKind.String &&
                    el.GetString() is { Length: > 0 } res)
                {
                    _genResolution = res.Trim().ToLowerInvariant() switch
                    {
                        "480" or "480p" => "480p",
                        "720" or "720p" => "720p",
                        "1080" or "1080p" => "1080p",
                        _ => res.Trim(),
                    };
                }
                if (cfg.TryGetValue("preferred_video_editor", out var edEl) &&
                    edEl.ValueKind == JsonValueKind.String &&
                    edEl.GetString() is { Length: > 0 } pve)
                {
                    _preferredVideoEditor = pve.Trim();
                }
                if (cfg.TryGetValue("audio_model_name", out var amEl) &&
                    amEl.ValueKind == JsonValueKind.String &&
                    amEl.GetString() is { Length: > 0 } am &&
                    !string.Equals(am, "none", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedAudioModel = am.Trim();
                }
            }
        }
        catch { /* keep default */ }
    }

    private async Task OpenInExternalEditorAsync(int? sceneNumber = null, int? clipNumber = null)
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.OpenInExternalEditorAsync(_projectId, sceneNumber, clipNumber, _preferredVideoEditor);
            if (res.Ok)
            {
                _message = $"🎬 Opened video in {res.Editor ?? _preferredVideoEditor}.";
            }
            else if (!string.IsNullOrWhiteSpace(res.VideoUrl))
            {
                var cleanPid = System.Text.RegularExpressions.Regex.Replace(_projectId, @"[^\w\.-]", "_");
                var fileName = sceneNumber is int sn
                    ? (clipNumber is int cn ? $"{cleanPid}_S{sn:D2}C{cn:D2}.mp4" : $"{cleanPid}_S{sn:D2}_composite.mp4")
                    : $"{cleanPid}_full.mp4";
                _message = $"🎬 Downloaded video to your PC — open {fileName} in {res.Editor ?? _preferredVideoEditor}.";
                try
                {
                    await JS.InvokeVoidAsync("eval", $"const a=document.createElement('a');a.href='{res.VideoUrl}';a.download='{fileName}';document.body.appendChild(a);a.click();document.body.removeChild(a);");
                }
                catch { /* ignore */ }
            }
            else
            {
                _error = res.Error ?? "Could not open external video editor.";
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnJobUpdated(JobSnapshot snap)
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
    private bool ShouldRefreshSceneListWhileRunning(JobSnapshot snap)
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
    private async Task SoftReloadListLiveAsync()
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

    private void OnJobLog(string line)
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

    private async Task OnProjectChangedAsync()
    {
        _selectedScene = null;
        _detail = null;
        _selectedClip = null;
        _clip = null;
        _selected.Clear();
        ResetPickers();
        await LoadGenResolutionFromConfigAsync();
        await ReloadListAsync();
    }

    private async Task ReloadListAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var dto = await Engine.GetScenesAsync(_projectId);
            _scenes = dto?.Scenes ?? new List<SceneSummary>();
            // Drop selections that no longer exist
            _selected.RemoveWhere(sn => _scenes.All(s => s.SceneNumber != sn));
            if (_selectedScene is int sn)
                await LoadDetailAsync(sn);
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
            await RefreshMyJobsAsync();
            await RefreshCastGateAsync();
            await RefreshResolutionLockAsync();
            await RefreshCostEstimateAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _scenes = null;
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// Once a project has on-disk clips at a consistent resolution, lock the resolution
    /// picker to it so a later Regen/batch can't silently mix resolutions in one movie.
    /// </summary>
    private async Task RefreshResolutionLockAsync()
    {
        try
        {
            _resolutionLock = await Engine.GetResolutionLockAsync(_projectId);
            if (_resolutionLock is { Length: > 0 })
                _genResolution = _resolutionLock;
        }
        catch { /* fail open — leave picker editable */ }
    }

    /// <summary>Refreshes the per-scene cost report at the currently selected generation resolution.</summary>
    private async Task RefreshCostEstimateAsync()
    {
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            var dto = await Engine.GetCostAsync(_projectId, draftResolution: _genResolution, heroResolution: _genResolution);
            _costReport = dto?.Cost;
        }
        catch { _costReport = null; }
    }

    private double EstimateSelectedCostUsd()
    {
        if (_costReport is null) return 0;
        var sum = 0.0;
        // The end-credits card renders client-side (canvas → ffmpeg.wasm) for free — see
        // StartBatchAsync, which already splits it out of the paid video-model batch. The cost
        // report itself doesn't know that, so exclude it here too or the confirm modal quotes a
        // price for a scene that will never actually be sent to a video model.
        foreach (var row in _costReport.Scenes.Where(r => _selected.Contains(r.Scene) && !IsCreditsSceneNum(r.Scene)))
            sum += row.RemainingDraftUsd;
        return sum;
    }

    private double? EstimateSceneCostUsd(int sceneNumber)
    {
        var row = _costReport?.Scenes.FirstOrDefault(r => r.Scene == sceneNumber);
        return row?.RemainingDraftUsd;
    }

    /// <summary>
    /// Refresh project-wide cast readiness (voice + locked image for every character).
    /// Soft-fails: if adaptation cannot load, keep previous gate state.
    /// </summary>
    private async Task RefreshCastGateAsync()
    {
        try
        {
            var adapt = await Engine.GetAdaptationAsync(_projectId);
            var cast = adapt?.Adaptation?.Cast;
            if (cast is null)
            {
                _castChecked = true;
                _castReady = false;
                _castReadyCount = null;
                _castTotal = null;
                _castMissing = new List<string>();
                return;
            }

            _castChecked = true;
            _castReady = cast.ReadyForShots;
            _castReadyCount = cast.Ready;
            _castTotal = cast.Total;
            _castMissing = cast.Missing?.Count > 0
                ? cast.Missing.ToList()
                : new List<string>();
        }
        catch
        {
            // Keep last known; mark checked so UI does not hang open forever
            _castChecked = true;
        }
    }

    private async Task SoftReloadAsync()
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

    private async Task RefreshMyJobsAsync()
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

    private async Task OpenSceneAsync(int sn)
    {
        _busy = true;
        _error = null;
        _message = null; // clear any leftover completion message from a previous scene/action
        try
        {
            await LoadDetailAsync(sn);
            _selectedScene = sn;
            _selectedClip = null;
            _clip = null;
            _selectedClips.Clear();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task LoadDetailAsync(int sn)
    {
        var dto = await Engine.GetSceneDetailAsync(_projectId, sn);
        _detail = dto?.Scene
            ?? throw new InvalidOperationException($"Scene {sn} not found");

        _sceneCompositeVideoUrl = null;
        // Resolved once per scene load, not inline in markup — CacheBust() stamps the current
        // second, so calling it inline re-evaluates on every render (any SignalR/job-poll
        // re-render elsewhere on the page) and gives the <video> a new src each time, which
        // makes the browser reload the resource and restart playback — looks like looping.
        _sceneCompositeServerUrl = CacheBust(Engine.CompositeVideoUrl(_projectId, sn));
        if (MediaFolder.IsConnected && _detail.CompositeExists)
        {
            try
            {
                var localBlob = await MediaFolder.GetLocalBlobUrlAsync(_projectId, $"assets/video/scene_{sn:D2}.mp4");
                if (!string.IsNullOrWhiteSpace(localBlob))
                    _sceneCompositeVideoUrl = localBlob;
            }
            catch { /* fallback */ }
        }
    }

    private async Task BackToListAsync()
    {
        _selectedScene = null;
        _detail = null;
        _selectedClip = null;
        _clip = null;
        _selectedClips.Clear();
        _message = null; // clear any leftover completion message from a previous scene/action
        await ReloadListAsync();
    }

    private void ToggleClipSelect(int cn, bool on)
    {
        if (on) _selectedClips.Add(cn);
        else _selectedClips.Remove(cn);
    }

    private void ClearClipSelection() => _selectedClips.Clear();

    private bool AllClipsSelected =>
        _detail is { Clips.Count: > 0 } && _detail.Clips.All(c => _selectedClips.Contains(c.ClipNumber));

    private void ToggleSelectAllClips(bool on)
    {
        if (_detail is null) return;
        if (on)
        {
            foreach (var c in _detail.Clips)
                _selectedClips.Add(c.ClipNumber);
        }
        else
        {
            _selectedClips.Clear();
        }
    }

    private double? EstimateSelectedClipsCostUsd()
    {
        if (_costReport is null || _detail is null) return null;
        var row = _costReport.Scenes.FirstOrDefault(r => r.Scene == _detail.SceneNumber);
        if (row is null || row.ClipsTotal <= 0) return null;
        // Approximate: whole-scene draft cost spread evenly per clip (force-regen ignores on-disk state).
        return row.AllDraftUsd / row.ClipsTotal * _selectedClips.Count;
    }

    private async Task EnsurePredecessorsUploadedAsync(List<(int Scene, int Clip)> targets)
    {
        if (!MediaFolder.IsConnected || string.IsNullOrEmpty(_projectId) || targets.Count == 0) return;

        // Cache scene detail per scene number — targets can span scenes (multi-scene batch),
        // and _detail may be loaded for a different scene than the one we're checking (or null,
        // when this runs from the scene-list page rather than a scene-detail view).
        var detailCache = new Dictionary<int, SceneDetail?>();
        async Task<SceneDetail?> GetSceneAsync(int sn)
        {
            if (detailCache.TryGetValue(sn, out var cached)) return cached;
            var d = _detail?.SceneNumber == sn ? _detail : (await Engine.GetSceneDetailAsync(_projectId, sn))?.Scene;
            detailCache[sn] = d;
            return d;
        }

        // Video-extend continuity (see FilmJobService.GenerateOneClipAsync + ClientMediaFolderService.
        // PrepareExtendSourceAsync): resolved once per batch, not per target, since it's the same
        // active project setting for every clip here.
        var extendModel = await ResolveActiveVideoExtendModelAsync();

        foreach (var (sn, cn) in targets)
        {
            if (cn <= 1) continue;
            var prevClipNum = cn - 1;
            if (targets.Any(t => t.Scene == sn && t.Clip == prevClipNum)) continue;

            // OnDisk alone isn't enough here: it's also true when only the .client.json marker
            // exists (clip synced to the client, then pruned off server disk) — SizeBytes is 0 in
            // that case since there are no real bytes for the video-extend gate to find and copy.
            var sceneDetail = await GetSceneAsync(sn);
            var prevSummary = sceneDetail?.Clips?.FirstOrDefault(c => c.ClipNumber == prevClipNum);
            var serverHasRealBytes = prevSummary?.OnDisk == true && prevSummary.SizeBytes >= 1024;
            if (!serverHasRealBytes)
            {
                var localBytes = await MediaFolder.GetClipBytesAsync(_projectId, sn, prevClipNum);
                if (localBytes is { Length: >= 1024 })
                {
                    _message = $"Uploading local predecessor S{sn:D2}C{prevClipNum:D2} to server…";
                    StateHasChanged();

                    await Engine.UploadClipAsync(_projectId, sn, prevClipNum, localBytes);
                }
            }

            if (extendModel is { } maxInputSeconds)
            {
                var wantsExtend = string.Equals(
                    sceneDetail?.Clips?.FirstOrDefault(c => c.ClipNumber == cn)?.Continuation,
                    "extend_previous", StringComparison.OrdinalIgnoreCase);
                if (wantsExtend)
                {
                    // Best-effort: a false return just means no extend-source file appears on the
                    // server, so it falls back to today's fresh-gen behavior — never blocks generation.
                    await MediaFolder.PrepareExtendSourceAsync(_projectId, sn, cn, maxInputSeconds);
                }
            }
        }
    }

    /// <summary>Active project video model's max input length for a real video-extend call, or
    /// null when the model doesn't support real continuity (today: only Grok's video model does)
    /// or lookup fails.</summary>
    private async Task<double?> ResolveActiveVideoExtendModelAsync()
    {
        try
        {
            var cfg = await Engine.GetConfigAsync(_projectId);
            var modelId = cfg?.Config is { } c && c.TryGetValue("model_name", out var el) &&
                          el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(modelId)) return null;

            var models = await Engine.GetSupportedModelsAsync(capability: "video");
            var entry = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (entry is not { SupportsVideoContinue: true }) return null;

            return entry.AbsMaxClipDurationSeconds ?? entry.MaxClipDurationSeconds ?? 15;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Clip numbers in scene <paramref name="sn"/> not yet on server disk (or synced-only) —
    /// used to pre-check predecessors before an "only missing" generation batch.</summary>
    private async Task<List<(int Scene, int Clip)>> MissingClipTargetsAsync(int sn)
    {
        var detail = _detail?.SceneNumber == sn ? _detail : (await Engine.GetSceneDetailAsync(_projectId, sn))?.Scene;
        return detail?.Clips?.Where(c => !c.OnDisk).Select(c => (Scene: sn, Clip: c.ClipNumber)).ToList()
            ?? new List<(int Scene, int Clip)>();
    }

    private async Task RegenSelectedClipsAsync()
    {
        if (_detail is null || _selectedClips.Count == 0) return;
        var sn = _detail.SceneNumber;
        _busy = true;
        _error = null;
        _message = null;
        _pendingRegenScene = sn;
        try
        {
            var targets = _selectedClips.OrderBy(c => c).Select(c => (Scene: sn, Clip: c)).ToList();
            await EnsureHubAsync();
            await EnsurePredecessorsUploadedAsync(targets);
            _job = await Engine.StartClipBatchGenAsync(_projectId, targets, resolution: _genResolution);
            _message = $"Regenerating {targets.Count} clip(s) in S{sn:D2} @ {_genResolution}…";
            _selectedClips.Clear();
            StateHasChanged();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; _pendingRegenScene = null; }
    }

    private string? _clipVideoUrl;
    private string? _clipServerVideoUrl;
    private bool _clipVideoLoading;
    private string? _sceneCompositeVideoUrl;
    private string? _sceneCompositeServerUrl;

    private void SelectClip(int? cn)
    {
        _message = null; // clear any leftover completion message from a previous scene/action
        _selectedClip = cn;
        _clip = cn is int n
            ? _detail?.Clips.FirstOrDefault(c => c.ClipNumber == n)
            : null;
        _clipVersions = null;
        _clipVideoUrl = null;
        if (cn is int cnv)
        {
            // Force new <video> mount so we never keep a previous composite/clip stream
            _clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Resolved once, not inline in markup — CacheBust() stamps the current second, so
            // calling it inline re-evaluates on every render (any SignalR/job-poll re-render
            // elsewhere on the page) and gives the <video> a new src each time, which makes the
            // browser reload the resource and restart playback — looks like looping.
            _clipServerVideoUrl = _detail is not null
                ? CacheBust(Engine.ClipVideoUrl(_projectId, _detail.SceneNumber, cnv))
                : null;
            // Gate the <video> behind a loading spinner while we check for a newer local copy —
            // otherwise it renders immediately with the (possibly stale) server fallback src and
            // autoplays that before swapping to the fresh one once the check resolves.
            _clipVideoLoading = MediaFolder.IsConnected;
            // Stop full-scene autoplay panel if open
            if (_showScenePlayer && _playingScene == _detail?.SceneNumber)
            {
                _showScenePlayer = false;
                _playingScene = null;
            }
            if (_detail is not null)
                _ = LoadClipVideoAndTakesCountAsync(_detail.SceneNumber, cnv);
        }
    }

    /// <summary>
    /// A local file at a clip's canonical relative path is not necessarily the *current* version
    /// — a later regen/promote may have happened without this browser open to catch the
    /// auto-save. Every call site that trusts a local clip copy (playback, dialogue
    /// re-verification upload) should gate on this against the server's currently-registered
    /// size rather than assuming presence means current. Returns null on any lookup failure —
    /// callers then fall back to their own "trust local unconditionally" or "use server" default.
    /// </summary>
    private async Task<long?> ResolveExpectedClipSizeAsync(int scene, int clip)
    {
        try
        {
            var status = await Engine.GetClipMediaStatusAsync(_projectId, scene, clip);
            if (status is { Ok: true })
                return status.OnServer ? status.ServerSizeBytes
                    : status.OnClient ? status.ClientSizeBytes
                    : null;
        }
        catch { /* best effort — falls back to unconditional local trust */ }
        return null;
    }

    private async Task LoadClipVideoAndTakesCountAsync(int scene, int clip)
    {
        if (MediaFolder.IsConnected)
        {
            try
            {
                var relPath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
                var expectedSize = await ResolveExpectedClipSizeAsync(scene, clip);

                var localBlob = expectedSize is long exp
                    ? await MediaFolder.GetCurrentBlobUrlAsync(_projectId, relPath, exp)
                    : await MediaFolder.GetLocalBlobUrlAsync(_projectId, relPath);
                if (!string.IsNullOrWhiteSpace(localBlob))
                {
                    _clipVideoUrl = localBlob;
                }
            }
            catch { /* fallback to server URL */ }
            finally
            {
                _clipVideoLoading = false;
                StateHasChanged();
            }
        }

        // Proactive, lightweight fetch so the "Takes (N)" button shows a real count without
        // requiring a click first. OpenClipCompareAsync re-fetches the authoritative list (plus
        // trash) when the modal actually opens — this is just for the label.
        try
        {
            var res = await Engine.GetClipVersionsAsync(_projectId, scene, clip);
            _clipVersions = res?.Versions;
            StateHasChanged();
        }
        catch { /* label falls back to "1" */ }
    }

    private async Task PlaySceneCompositeAsync(int sn)
    {
        // _busy flips true synchronously, before the first await — see PlaySceneAsync's comment
        // in Review.razor for why (a fast second click otherwise slips past this guard and races
        // the first over shared local blob caches).
        if (_busy || _clientStitching) return;
        _busy = true;
        try
        {
            var meta = await Engine.GetWipMovieMetaAsync(_projectId);
            var summary = _scenes?.FirstOrDefault(s => s.SceneNumber == sn);
            var compositeOk = summary?.CompositeExists == true
                              || (_detail is { SceneNumber: var dsn, CompositeExists: true } && dsn == sn);
            var clipsOnDisk = summary?.ClipsOnDisk
                              ?? (_detail is { SceneNumber: var d2 } && d2 == sn ? _detail.ClipsOnDisk : 0);
            var stale = meta?.StaleScenes?.Contains(sn) == true;
            var needsStitch = !compositeOk || stale;

            // Fresh composite on disk — stream it directly (no stitch).
            if (!needsStitch && compositeOk)
            {
                _clientSceneUrl = null;
                _playingScene = sn;
                _showScenePlayer = true;
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _inlineCompositeKey = _sceneVideoKey;
                _message = $"Playing S{sn:D2} composite";
                return;
            }

            if (clipsOnDisk <= 0 && !compositeOk)
            {
                if (MediaFolder.IsSyncing)
                {
                    _showScenePlayer = true;
                    _playingScene = sn;
                    _clientSceneUrl = null;
                    _message = $"Downloading video clips for S{sn:D2} to local folder…";
                }
                else
                {
                    _error = $"No clips for S{sn:D2} — connect local media folder or generate clips first";
                }
                return;
            }

            // Missing or stale composite: stitch clips (or fall back to stale composite) in the browser.
            _clientStitching = true;
            _error = null;
            _message = null;
            _clientStitchStatus = "Collecting clips…";
            _showPreviewPlayer = false;
            _clientPreviewUrl = null;
            _playingScene = sn;
            _showScenePlayer = true;
            _clientSceneUrl = null;
            try
            {
                SceneDetail? detail = _detail is { SceneNumber: var d } && d == sn
                    ? _detail
                    : null;
                var urls = await Stitch.CollectClipUrlsAsync(_projectId, sn, detail);
                if (urls.Count == 0 && compositeOk)
                {
                    // Stale composite still playable
                    _clientSceneUrl = null;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _message = $"Playing S{sn:D2} composite (may be stale)";
                    return;
                }

                if (urls.Count == 0)
                {
                    _error = $"No on-disk clips for S{sn:D2}";
                    _showScenePlayer = false;
                    _playingScene = null;
                    return;
                }

                _clientStitchStatus = urls.Count == 1 ? "Loading…" : $"Combining {urls.Count} clips…";
                await Stitch.RevokePreviewUrlAsync();
                var result = await Stitch.ConcatAsync(urls);
                if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                {
                    _error = result.Error ?? "Browser stitch failed";
                    _showScenePlayer = false;
                    _playingScene = null;
                    return;
                }

                // Layer locally-synced background music (if any) under the stitched video —
                // client-side replacement for the old server-side ffmpeg mix; no-op if none synced.
                _clientSceneUrl = await Stitch.MixSceneMusicAsync(_projectId, result.Url, sn);
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _inlineCompositeKey = _sceneVideoKey;
                _message = urls.Count == 1
                    ? $"Playing S{sn:D2} (single clip)"
                    : $"Playing S{sn:D2} — {urls.Count} clips stitched in browser";
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _showScenePlayer = false;
                _playingScene = null;
                _clientSceneUrl = null;
            }
            finally
            {
                _clientStitching = false;
                _clientStitchStatus = null;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task HideScenePlayer()
    {
        _showScenePlayer = false;
        _playingScene = null;
        if (!string.IsNullOrEmpty(_clientSceneUrl))
        {
            _clientSceneUrl = null;
            await Stitch.RevokePreviewUrlAsync();
        }
    }

    private int? _scenePlayerServerSrcScene;
    private string? _scenePlayerServerSrcCached;

    /// <summary>
    /// Cache-busted server URL is memoized per scene number rather than recomputed on every call
    /// — CacheBust() stamps the current second, so recomputing on every render (any SignalR/
    /// job-poll re-render elsewhere on the page) gives the &lt;video&gt; a new src each time,
    /// which makes the browser reload the resource and restart playback — looks like looping.
    /// </summary>
    private string? ScenePlayerSrc(int sn)
    {
        if (!string.IsNullOrEmpty(_clientSceneUrl) && _playingScene == sn)
            return _clientSceneUrl;

        if (_scenePlayerServerSrcScene != sn)
        {
            _scenePlayerServerSrcScene = sn;
            _scenePlayerServerSrcCached = CacheBust(Engine.CompositeVideoUrl(_projectId, sn));
        }
        return _scenePlayerServerSrcCached;
    }

    private async Task HidePreviewPlayerAsync()
    {
        _showPreviewPlayer = false;
        _clientPreviewUrl = null;
        await Stitch.RevokePreviewUrlAsync();
    }

    /// <summary>True when selection has at least one scene to play (or local media folder connected).</summary>
    private bool CanPlaySelected
    {
        get
        {
            if (_selected.Count == 0 || _scenes is null)
                return false;
            if (MediaFolder.IsConnected || MediaFolder.IsSyncing)
                return true;
            return _scenes.Any(s =>
                _selected.Contains(s.SceneNumber)
                && (s.CompositeExists || s.ClipsOnDisk > 0));
        }
    }

    /// <summary>Stitch the current selection in the browser (composites preferred, else clips).</summary>
    private async Task PlaySelectedAsync()
    {
        if (!CanPlaySelected || _busy || _clientStitching)
            return;

        _busy = true;
        _clientStitching = true;
        _error = null;
        _message = null;
        _clientStitchStatus = "Preparing…";
        _previewScenes = _selected.OrderBy(x => x).ToList();
        _showScenePlayer = false;
        _playingScene = null;
        _clientSceneUrl = null;
        _showPreviewPlayer = true;
        _clientPreviewUrl = null;
        try
        {
            // Revoke the OLD preview before collecting new segments — see the comment in
            // Review.razor's EnsureShareableMovieUrlAsync for why revoking after collection can
            // destroy a blob the segments list still needs.
            await Stitch.RevokePreviewUrlAsync();
            var meta = await Engine.GetWipMovieMetaAsync(_projectId);
            var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
            _clientStitchStatus = "Collecting media…";
            var urls = await Stitch.CollectAndMixSceneSegmentsAsync(
                _projectId, _previewScenes, _scenes, stale);
            if (urls.Count == 0)
            {
                _error = "No composites or on-disk clips for the selected scenes";
                _showPreviewPlayer = false;
                return;
            }

            _clientStitchStatus = urls.Count == 1
                ? "Loading…"
                : $"Combining {urls.Count} clip/scene file(s)…";
            var result = await Stitch.ConcatAsync(urls);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
            {
                _error = result.Error ?? "Browser stitch failed";
                _showPreviewPlayer = false;
                return;
            }

            _clientPreviewUrl = result.Url;
            _previewVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _message = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _showPreviewPlayer = false;
            _clientPreviewUrl = null;
        }
        finally
        {
            _busy = false;
            _clientStitching = false;
            _clientStitchStatus = null;
        }
    }

    /// <summary>Force re-generate a single clip (onlyMissing: false).</summary>
    private async Task RegenClipAsync(int sn, int cn)
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
        _pendingRegenScene = sn;
        try
        {
            await EnsureHubAsync();
            await EnsurePredecessorsUploadedAsync(new List<(int Scene, int Clip)> { (sn, cn) });
            await Engine.StartSceneGenAsync(_projectId, sn, onlyMissing: false, clip: cn, resolution: _genResolution);
            _message = $"Regenerating S{sn:D2}C{cn:D2} @ {_genResolution}…";
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; _pendingRegenScene = null; }
    }

    /// <summary>xAI's edit input cap — see MaxVideoEditInputSeconds's doc comment (client hint;
    /// RunVideoEditAsync is the authoritative server-side check).</summary>
    private static bool ClipExceedsEditDurationCap(ClipSummary clip) =>
        (clip.ActualDurationSeconds ?? clip.DurationSeconds) > MaxVideoEditInputSeconds + 0.01;

    private void OpenVideoEditPrompt()
    {
        _videoEditPromptText = "";
        _showVideoEditPrompt = true;
    }

    private void CloseVideoEditPrompt() => _showVideoEditPrompt = false;

    private async Task SubmitVideoEditAsync()
    {
        if (_detail is null || _clip is null || string.IsNullOrWhiteSpace(_videoEditPromptText))
            return;

        var sn = _detail.SceneNumber;
        var cn = _clip.ClipNumber;
        _showVideoEditPrompt = false;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await EnsureHubAsync();
            await Engine.StartVideoEditAsync(_projectId, sn, cn, _videoEditPromptText.Trim());
            _message = $"Editing S{sn:D2}C{cn:D2}…";
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private void OpenClipEditor(ClipSummary clip)
    {
        if (_detail is null) return;
        _clipEditorIsNew = false;
        _clipEditor = new ClipEditRequest
        {
            ProjectId = _projectId,
            Scene = _detail.SceneNumber,
            Clip = clip.ClipNumber,
            VisualPrompt = clip.VisualPrompt,
            NegativePrompt = clip.NegativePrompt,
            Dialogue = clip.Dialogue,
            Speaker = clip.Speaker,
            Delivery = clip.Delivery,
            PronunciationHint = clip.PronunciationHint,
            PrimarySubject = clip.PrimarySubject,
            CharactersOnScreen = new List<string>(clip.CharactersOnScreen),
            ColorPalette = clip.ColorPalette,
            FilmStock = clip.FilmStock,
            DurationSeconds = clip.DurationSeconds,
        };
        _clipEditorCast = new HashSet<string>(clip.CharactersOnScreen, StringComparer.OrdinalIgnoreCase);
    }

    private void OpenAddClipDialog()
    {
        if (_detail is null) return;
        var nextClip = _detail.Clips.Count == 0 ? 1 : _detail.Clips.Max(c => c.ClipNumber) + 1;
        _clipEditorIsNew = true;
        _clipEditor = new ClipEditRequest
        {
            ProjectId = _projectId,
            Scene = _detail.SceneNumber,
            Clip = nextClip,
            DurationSeconds = 5,
        };
        _clipEditorCast = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void CloseClipEditor() => _clipEditor = null;

    private void ToggleClipEditorCast(string charKey, bool on)
    {
        if (on) _clipEditorCast.Add(charKey);
        else _clipEditorCast.Remove(charKey);
    }
    private Task OnClipEditorCastToggled((string Key, bool On) args)
    {
        ToggleClipEditorCast(args.Key, args.On);
        return Task.CompletedTask;
    }



    private async Task SaveClipEditorAsync()
    {
        if (_clipEditor is null || _detail is null) return;

        // Mirror server rules for fast feedback (server still authoritative).
        if (string.IsNullOrWhiteSpace(_clipEditor.VisualPrompt))
        {
            _error = "Visual prompt is required.";
            return;
        }
        if (_clipEditor.DurationSeconds < 0 || _clipEditor.DurationSeconds > 12)
        {
            _error = "Duration must be 0 (unset) or 3–12 seconds.";
            return;
        }
        if (_clipEditor.DurationSeconds is > 0 and < 3)
        {
            _error = "Duration must be at least 3s (or 0 to leave unset).";
            return;
        }
        var dlg = (_clipEditor.Dialogue ?? "").Trim();
        var spk = (_clipEditor.Speaker ?? "").Trim();
        var del = (_clipEditor.Delivery ?? "").Trim();
        var delNone = del.Length == 0 || string.Equals(del, "none", StringComparison.OrdinalIgnoreCase);
        if (dlg.Length > 0 && spk.Length == 0)
        {
            _error = "Dialogue needs a speaker. Pick who says the line, or clear the dialogue.";
            return;
        }
        if (dlg.Length > 0 && delNone)
        {
            _error = "Dialogue needs a delivery: Spoken (on camera), Voiceover (internal), or Off camera.";
            return;
        }
        if (spk.Length > 0 && dlg.Length == 0)
        {
            _error = "Speaker is set but dialogue is empty. Add the line, or set speaker to none.";
            return;
        }
        if (_clipEditorIsNew && (_clipEditor.Clip < 1 || _clipEditor.Clip > 200))
        {
            _error = "Clip number must be between 1 and 200.";
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            _clipEditor.CharactersOnScreen = _clipEditorCast.ToList();
            if (_clipEditorIsNew)
            {
                await Engine.AddClipAsync(_projectId, _detail.SceneNumber, _clipEditor);
                _message = $"Added S{_detail.SceneNumber:D2}C{_clipEditor.Clip:D2} — generate its video when ready";
            }
            else
            {
                await Engine.UpdateClipAsync(_projectId, _detail.SceneNumber, _clipEditor.Clip, _clipEditor);
                _message = $"Saved S{_detail.SceneNumber:D2}C{_clipEditor.Clip:D2} — Regen the clip to re-render video/audio with the new fields";
            }
            try { await Engine.CommitProjectChangesAsync(_projectId, $"Saved clip S{_detail.SceneNumber:D2}C{_clipEditor.Clip:D2}"); } catch { }
            await RefreshUncommittedStatusAsync();
            _clipEditor = null;
            await LoadDetailAsync(_detail.SceneNumber);
            var scenesDto = await Engine.GetScenesAsync(_projectId);
            if (scenesDto?.Scenes is not null)
            {
                _scenes = scenesDto.Scenes;
            }
            if (_selectedClip is int sel)
                _clip = _detail.Clips.FirstOrDefault(c => c.ClipNumber == sel);
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private void RequestDeleteClip(int scene, int clip) => _deleteClipTarget = (scene, clip);

    private void CancelDeleteClip() => _deleteClipTarget = null;

    private async Task ConfirmDeleteClipAsync()
    {
        if (_deleteClipTarget is not { } target) return;
        _busy = true;
        _error = null;
        try
        {
            await Engine.DeleteClipAsync(_projectId, target.Scene, target.Clip);
            _deleteClipTarget = null;
            if (_selectedClip == target.Clip)
            {
                _selectedClip = null;
                _clip = null;
            }
            _message = $"Deleted S{target.Scene:D2}C{target.Clip:D2} — Play scene / Play WIP to refresh the assembled cut";
            await ReloadListAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private void ToggleSelect(int sn, bool on)
    {
        if (on) _selected.Add(sn);
        else _selected.Remove(sn);
        _selectionMode = "";
    }

    private void SelectAll()
    {
        _selected = VisibleScenes.Select(s => s.SceneNumber).ToHashSet();
        _selectionMode = "all";
    }

    private void ClearSelection()
    {
        _selected.Clear();
        _selectionMode = "";
    }

    private bool AllShownScenesSelected =>
        VisibleScenes.Count > 0 && VisibleScenes.All(s => _selected.Contains(s.SceneNumber));

    private void ToggleSelectAllShown(bool on)
    {
        if (on) SelectAll();
        else ClearSelection();
    }

    private int EstimateSelectedClips()
    {
        if (_scenes is null) return 0;
        // Generate always fills missing only — estimate remaining work on selected scenes.
        return _scenes
            .Where(x => _selected.Contains(x.SceneNumber))
            .Sum(s => Math.Max(0, s.ClipCount - s.ClipsOnDisk));
    }

    private async Task GenOneSceneAsync(int sn)
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

    private async Task StartBatchAsync()
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

    private static (int W, int H) ResolutionDims(string? res) => (res ?? "").Trim().ToLowerInvariant() switch
    {
        "1080p" => (1920, 1080),
        "480p" => (854, 480),
        _ => (1280, 720),
    };

    private bool IsCreditsSceneNum(int sn) =>
        _scenes?.FirstOrDefault(s => s.SceneNumber == sn)?.IsCredits == true;

    /// <summary>
    /// Single entry point every generation path funnels a credits scene through: render the deterministic
    /// card client-side instead of calling the video model, so no path (batch, per-clip regen, single
    /// scene) can ever produce a hallucinated credits clip. No cast gate — a credits card has no cast.
    /// </summary>
    private async Task GenerateCreditsEntryAsync(int sn)
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
    private async Task RenderCreditsSceneClientSideAsync(int sn)
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

    private async Task RenderOneCreditsClipAsync(int sn, int clip, double durationSeconds, int width, int height)
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

    private async Task CancelAsync()
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

    private async Task EnsureHubAsync()
    {
        await Hub.EnsureStartedAsync();
        try { await MediaFolder.EnsureHubHookAsync(); } catch { /* optional */ }
    }

    private static string StatusBadge(string status) => status switch
    {
        "complete" => "bg-success",
        "partial" => "bg-warning text-dark",
        _ => "bg-secondary",
    };

    private static string Trunc(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    private static string ShortChar(string key) => KeyFormatting.ShortChar(key);

    private static string ShortLoc(string key) => KeyFormatting.ShortLoc(key);

    private static string ShortDelivery(string? key) => KeyFormatting.ShortDelivery(key);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024):0.#} MB";
    }

    private static string CacheBust(string url) => KeyFormatting.CacheBust(url);

    /// <summary>Format seconds as m:ss or plain seconds when under a minute.</summary>
    private static string FormatClock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var whole = (int)Math.Round(seconds);
        if (whole < 60) return $"{whole}s";
        var m = whole / 60;
        var s = whole % 60;
        return $"{m}:{s:D2}";
    }

    private static MarkupString RenderDiffHtml(string? expected, string? heard)
    {
        var expStr = expected ?? "";
        var heardStr = heard ?? "";
        if (string.IsNullOrWhiteSpace(expStr) && string.IsNullOrWhiteSpace(heardStr))
            return new MarkupString("—");

        var expWords = System.Text.RegularExpressions.Regex.Split(expStr.Trim(), @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        var heardWords = System.Text.RegularExpressions.Regex.Split(heardStr.Trim(), @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToList();

        static string Clean(string w) => System.Text.RegularExpressions.Regex.Replace(w.ToLowerInvariant(), @"[^\w]", "");

        var expClean = expWords.Select(Clean).ToList();
        var heardClean = heardWords.Select(Clean).ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append("<div class=\"small\">");

        // Expected line: missing words highlighted in strikethrough red
        sb.Append("<div><strong>Expected:</strong> ");
        for (int i = 0; i < expWords.Count; i++)
        {
            var word = System.Net.WebUtility.HtmlEncode(expWords[i]);
            var c = expClean[i];
            if (!string.IsNullOrEmpty(c) && !heardClean.Contains(c))
            {
                sb.Append($"<span class=\"badge bg-danger-subtle text-danger text-decoration-line-through me-1\" title=\"Missing from spoken clip audio\">{word}</span> ");
            }
            else
            {
                sb.Append($"{word} ");
            }
        }
        sb.Append("</div>");

        // Heard line: extra words highlighted in soft yellow
        sb.Append("<div><strong>Heard:</strong> ");
        for (int i = 0; i < heardWords.Count; i++)
        {
            var word = System.Net.WebUtility.HtmlEncode(heardWords[i]);
            var c = heardClean[i];
            if (!string.IsNullOrEmpty(c) && !expClean.Contains(c))
            {
                sb.Append($"<span class=\"badge bg-warning-subtle text-warning border border-warning-subtle me-1\" title=\"Extra/changed word heard in clip\">{word}</span> ");
            }
            else
            {
                sb.Append($"{word} ");
            }
        }
        sb.Append("</div></div>");

        return new MarkupString(sb.ToString());
    }

    private bool _verifyingClip;
    private int _verifyingClipNumber;
    private int _verifyCurrent;
    private int _verifyTotal;
    private string _verifyStatusLabel = "Verifying dialogue...";

    private async Task VerifyClipDialogueManualAsync(ClipSummary clip)
    {
        if (string.IsNullOrWhiteSpace(_projectId) || _detail is null || clip is null) return;
        try
        {
            _verifyingClip = true;
            _verifyingClipNumber = clip.ClipNumber;
            _verifyCurrent = 1;
            _verifyTotal = 1;
            _verifyStatusLabel = $"Verifying dialogue for S{_detail.SceneNumber:D2} C{clip.ClipNumber:D2}...";
            StateHasChanged();

            var expectedSize = await ResolveExpectedClipSizeAsync(_detail.SceneNumber, clip.ClipNumber);
            var videoBytes = await MediaFolder.GetClipBytesAsync(_projectId, _detail.SceneNumber, clip.ClipNumber, expectedSize);
            var ver = await Engine.VerifyClipDialogueAsync(_projectId, _detail.SceneNumber, clip.ClipNumber, videoBytes: videoBytes, force: true);
            if (ver is not null)
            {
                clip.DialogueVerification = ver;
                if (_showVerificationModal && _verifModalClipNumber == clip.ClipNumber && _verifModalSceneNumber == _detail.SceneNumber)
                {
                    _verifModalResult = ver;
                }
                if (string.Equals(ver.Status, "unverified", StringComparison.OrdinalIgnoreCase))
                {
                    _error = ver.SummaryNote ?? "Clip Dialogue Verification requires Google Gemini (GEMINI_API_KEY). Gemini is the only provider that supports native video & audio dialogue analysis. Please add your Gemini key in Configuration.";
                }

                _detail = (await Engine.GetSceneDetailAsync(_projectId, _detail.SceneNumber))?.Scene;
                var scenesDto = await Engine.GetScenesAsync(_projectId);
                if (scenesDto?.Scenes is not null)
                {
                    _scenes = scenesDto.Scenes;
                }
            }
        }
        catch (Exception ex)
        {
            _error = $"Dialogue verification failed: {ex.Message}";
        }
        finally
        {
            _verifyingClip = false;
            _verifyingClipNumber = 0;
            _verifyCurrent = 0;
            _verifyTotal = 0;
            StateHasChanged();
        }
    }

    /// <summary>
    /// From the all-scenes view: at least one selected scene has a finished clip on disk to check.
    /// Dialogue verification reads the clip's video, so scenes with nothing on disk have nothing to check.
    /// Gates the list-view "Verify Scene Dialogue" button so it never reads as a dead click.
    /// </summary>
    private bool SelectedScenesHaveClipsToVerify =>
        _scenes is not null &&
        _selected.Count > 0 &&
        _scenes.Any(s => _selected.Contains(s.SceneNumber) && s.ClipsOnDisk > 0);

    private async Task VerifySelectedScenesDialogueAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;

        // Build the clip work list from context. Detail view: the open scene's checked clips (or its
        // unverified on-disk clips). All-scenes view: every on-disk clip across the selected scenes.
        // Only clips with video on disk can be checked — there's nothing to analyse otherwise.
        var targets = new List<(int Scene, int Clip)>();

        if (_detail is not null)
        {
            if (_selectedClips.Count > 0)
            {
                foreach (var cn in _selectedClips.OrderBy(c => c))
                    targets.Add((_detail.SceneNumber, cn));
            }
            else
            {
                foreach (var c in _detail.Clips
                    .Where(c => c.OnDisk && (c.DialogueVerification is null || !string.Equals(c.DialogueVerification.Status, "verified", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(c => c.ClipNumber))
                    targets.Add((_detail.SceneNumber, c.ClipNumber));
            }
        }
        else if (_selected.Count > 0)
        {
            // All-scenes view: gather each selected scene's on-disk clips (the button is gated so this
            // path only runs when at least one selected scene actually has finished clips).
            foreach (var sn in _selected.OrderBy(x => x))
            {
                var det = (await Engine.GetSceneDetailAsync(_projectId, sn))?.Scene;
                if (det?.Clips is null) continue;
                foreach (var c in det.Clips.Where(c => c.OnDisk).OrderBy(c => c.ClipNumber))
                    targets.Add((sn, c.ClipNumber));
            }
        }

        if (targets.Count == 0)
        {
            // Never a silent dead click — say why there's nothing to do.
            _message = _detail is not null
                ? "All clips verified. Tick specific clip boxes in the first column to force a re-check."
                : _selected.Count == 0
                    ? "Select one or more scenes with finished clips to verify."
                    : "Selected scenes have no finished clips to verify yet.";
            return;
        }

        try
        {
            _verifyingClip = true;
            _verifyCurrent = 0;
            _verifyTotal = targets.Count;
            _verifyStatusLabel = $"Verifying dialogue for {targets.Count} clip(s)...";
            StateHasChanged();

            foreach (var (sceneNum, cn) in targets)
            {
                _verifyCurrent++;
                _verifyingClipNumber = cn;
                var clip = _detail?.SceneNumber == sceneNum
                    ? _detail?.Clips?.FirstOrDefault(c => c.ClipNumber == cn)
                    : null;
                _verifyStatusLabel = $"Verifying dialogue for S{sceneNum:D2} C{cn:D2} (Speaker: {clip?.Speaker ?? "Unknown"})...";
                StateHasChanged();

                var expectedSize = await ResolveExpectedClipSizeAsync(sceneNum, cn);
                var videoBytes = await MediaFolder.GetClipBytesAsync(_projectId, sceneNum, cn, expectedSize);
                var ver = await Engine.VerifyClipDialogueAsync(_projectId, sceneNum, cn, videoBytes: videoBytes, force: true);
                if (ver is not null)
                {
                    if (clip is not null)
                    {
                        clip.DialogueVerification = ver;
                        if (_showVerificationModal && _verifModalClipNumber == cn && _verifModalSceneNumber == sceneNum)
                        {
                            _verifModalResult = ver;
                        }
                    }
                    if (string.Equals(ver.Status, "unverified", StringComparison.OrdinalIgnoreCase))
                    {
                        _error = ver.SummaryNote ?? "Clip Dialogue Verification requires Google Gemini (GEMINI_API_KEY). Gemini is the only provider that supports native video & audio dialogue analysis. Please add your Gemini key in Configuration.";
                    }
                    StateHasChanged();
                }
            }

            if (_detail is not null)
            {
                _detail = (await Engine.GetSceneDetailAsync(_projectId, _detail.SceneNumber))?.Scene;
            }
            var scenesDto = await Engine.GetScenesAsync(_projectId);
            if (scenesDto?.Scenes is not null)
            {
                _scenes = scenesDto.Scenes;
            }
        }
        catch (Exception ex)
        {
            _error = $"Dialogue verification failed: {ex.Message}";
        }
        finally
        {
            _verifyingClip = false;
            _verifyingClipNumber = 0;
            _verifyCurrent = 0;
            _verifyTotal = 0;
            StateHasChanged();
        }
    }

    private bool _scoringMusic;
    private List<SupportedModelDto> _audioModels = new();
    private string _selectedAudioModel = "fal-ai/stable-audio";
    private bool _wantVocal;

    // Which scene's Score chooser is open (null = closed). The model/Sing picks it edits are the
    // shared _selectedAudioModel/_wantVocal, so they persist as the defaults for the next scene.
    private int? _scoreMenuScene;

    private void OpenScoreMenu(int sceneNum) => _scoreMenuScene = sceneNum;

    private void CloseScoreMenu() => _scoreMenuScene = null;

    private async Task ScoreFromMenuAsync(int sceneNum)
    {
        _scoreMenuScene = null;
        await ScoreSceneBackgroundMusicAsync(sceneNum);
    }

    // Batch-generate confirm modal: resolution + cost decided at the moment of spend.
    private bool _showGenerateConfirm;
    private bool _showVideoEditPrompt;
    private string _videoEditPromptText = "";
    /// <summary>
    /// xAI's /v1/videos/edits input cap (grok-imagine-video-edit's maxEditInputDurationSeconds).
    /// A client-side UX hint only — RunVideoEditAsync re-checks the real catalog value
    /// server-side and is the authoritative gate; this just disables the button early.
    /// </summary>
    private const double MaxVideoEditInputSeconds = 8.7;

    private async Task OpenGenerateConfirmAsync()
    {
        if (_selected.Count == 0) return;
        if (!CastReady) { _error = CastBlockedTitle; return; }
        _showGenerateConfirm = true;
        await RefreshCostEstimateAsync();
    }

    private void CloseGenerateConfirm() => _showGenerateConfirm = false;

    private async Task ConfirmGenerateAsync()
    {
        _showGenerateConfirm = false;
        await StartBatchAsync();
    }

    /// <summary>Catalog <c>supportsVocals</c> on the selected audio model — not provider id.</summary>
    private bool SelectedAudioModelCanSing =>
        _audioModels.FirstOrDefault(m => string.Equals(m.Id, _selectedAudioModel, StringComparison.OrdinalIgnoreCase))
            ?.SupportsVocals == true;

    private async Task LoadAudioModelsAsync()
    {
        try
        {
            var models = await Engine.GetSupportedModelsAsync();
            _audioModels = models.Where(m => m.Capability == "audio" &&
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)).ToList();
            if (_audioModels.Count == 0)
                _audioModels.Add(new SupportedModelDto { Id = "fal-ai/stable-audio", DisplayName = "Stable Audio (Fal.ai)", Provider = "fal", Capability = "audio" });
        }
        catch { /* keep default single-entry list */ }
    }

    // Admin-only: video models offered as a one-off per-batch override in the Generate modal, so an
    // admin can A/B different generators without editing project Configuration. "" = project default.
    private List<SupportedModelDto> _videoModels = new();
    private string _selectedVideoModel = "";

    private async Task LoadVideoModelsAsync()
    {
        try
        {
            var models = await Engine.GetSupportedModelsAsync();
            _videoModels = models.Where(m => m.Capability == "video" &&
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch { /* leave empty — modal then offers project default only */ }
    }

    private async Task ScoreSceneBackgroundMusicAsync(int sceneNum)
    {
        
        if (!Caps.MusicReady)
        {
            _error = Caps.MusicBlockedReason;
            return;
        }
if (string.IsNullOrWhiteSpace(_projectId)) return;

        var isVocal = _wantVocal && SelectedAudioModelCanSing;
        _busy = true;
        _scoringMusic = true;
        _error = null;
        _message = isVocal
            ? $"Queuing singing for Scene {sceneNum:D2}…"
            : $"Queuing background music for Scene {sceneNum:D2}…";
        StateHasChanged();

        try
        {
            await EnsureHubAsync();
            var started = await Engine.StartSceneMusicGenAsync(_projectId, sceneNum, _selectedAudioModel, isVocal);
            // Live progress card only — no duplicate "started" banner (same as scene gen).
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job ?? started;
            if (!string.IsNullOrWhiteSpace(started?.JobId))
                _ = CompleteSceneMusicDownloadAsync(started.JobId, sceneNum);
        }
        catch (Exception ex)
        {
            _error = $"Music scoring failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            _scoringMusic = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// SignalR normally delivers each generated asset to the media-folder service. Waiting for the
    /// terminal job here closes the gap when that transient event is missed, and gives the operator
    /// a normal browser download even when no folder has been connected.
    /// </summary>
    private async Task CompleteSceneMusicDownloadAsync(string jobId, int sceneNum)
    {
        try
        {
            var final = await Engine.WaitForJobTerminalAsync(jobId, timeout: TimeSpan.FromMinutes(20));
            if (final is null ||
                !string.Equals(final.Status, "done", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(final.ClientMediaUrl) ||
                string.IsNullOrWhiteSpace(final.ClientRelativePath))
                return;

            var clientUrl = final.ClientMediaUrl;
            var clientRelativePath = final.ClientRelativePath;

            // Keep the project-owned copy when a local media folder is available. This is safe if
            // the regular JobUpdated handler already saved it because the service de-duplicates paths.
            await MediaFolder.SaveJobMediaAsync(final);

            var fileName = Path.GetFileName(clientRelativePath);
            await JS.InvokeVoidAsync("PageToMovieMedia.downloadFromUrlAsync", clientUrl, fileName);
            await InvokeAsync(() =>
            {
                _message = $"Background music for Scene {sceneNum:D2} downloaded.";
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            await InvokeAsync(() =>
            {
                _error = $"Music download failed: {ex.Message}";
                StateHasChanged();
            });
        }
    }

    private bool _showMusicCompare;
    private int _compareMusicSceneNumber;
    private List<MusicVersionItem>? _musicVersions;
    private List<MusicVersionItem>? _musicTrashVersions;
    private bool _loadingMusicVersions;
    private string? _musicCompareMessage;
    private bool _promotingMusicVersion;
    private bool _showMusicTrash;
    private Dictionary<string, List<string>> _musicCompareUrls = new(StringComparer.OrdinalIgnoreCase);

    private async Task OpenMusicCompareAsync(int sceneNumber)
    {
        _compareMusicSceneNumber = sceneNumber;
        _showMusicCompare = true;
        _loadingMusicVersions = true;
        _musicCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
            _musicVersions = res?.Versions;
            var trash = await Engine.GetTrashMusicVersionsAsync(_projectId, sceneNumber);
            _musicTrashVersions = trash?.Versions;
            _showMusicTrash = false;
            await RefreshMusicCompareUrlsAsync();
        }
        catch (Exception ex)
        {
            _error = $"Failed to load audio takes: {ex.Message}";
        }
        finally
        {
            _loadingMusicVersions = false;
            StateHasChanged();
        }
    }

    private void CloseMusicCompare()
    {
        _showMusicCompare = false;
        _musicVersions = null;
        _musicTrashVersions = null;
        _showMusicTrash = false;
        _musicCompareUrls.Clear();
        _musicCompareMessage = null;
    }

    private async Task RefreshMusicCompareUrlsAsync()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_musicVersions is { Count: > 0 })
        {
            foreach (var v in _musicVersions)
            {
                var urls = new List<string>();
                foreach (var relPath in v.RelativePaths)
                {
                    var url = await MediaFolder.GetLocalBlobUrlAsync(_projectId, relPath);
                    if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
                }
                map[v.TakeId] = urls;
            }
        }
        _musicCompareUrls = map;
        StateHasChanged();
    }

    private async Task PromoteMusicVersionAsync(int sceneNumber, string takeId)
    {
        _promotingMusicVersion = true;
        _musicCompareMessage = null;
        StateHasChanged();

        try
        {
            var target = _musicVersions?.FirstOrDefault(v => string.Equals(v.TakeId, takeId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                _musicCompareMessage = "Take not found.";
                return;
            }

            // Copy the chosen take's bytes to the active path first (archives whatever's currently
            // active under its own take id in the process — same mechanism a fresh generation uses),
            // then flip which sidecar the server considers active.
            var current = _musicVersions?.FirstOrDefault(v => v.IsCurrent);
            var archiveTakeId = current?.TakeId is { Length: > 0 } cid ? cid : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

            var copied = await MediaFolder.PromoteMusicTakeAsync(_projectId, target, archiveTakeId);
            if (!copied)
            {
                _musicCompareMessage = "Failed to copy audio locally — is your media folder connected?";
                return;
            }

            var res = await Engine.PromoteMusicVersionAsync(_projectId, sceneNumber, takeId);
            if (res.Ok)
            {
                _musicCompareMessage = $"Promoted take {takeId} to active.";
                var resV = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
                _musicVersions = resV?.Versions;
                await RefreshMusicCompareUrlsAsync();
                if (_detail is not null && _detail.SceneNumber == sceneNumber)
                {
                    _detail = (await Engine.GetSceneDetailAsync(_projectId, sceneNumber))?.Scene;
                }
            }
            else
            {
                _musicCompareMessage = res.Error ?? "Failed to promote audio take.";
            }
        }
        catch (Exception ex)
        {
            _musicCompareMessage = $"Promote failed: {ex.Message}";
        }
        finally
        {
            _promotingMusicVersion = false;
            StateHasChanged();
        }
    }

    private async Task SoftDeleteMusicVersionAsync(int sceneNumber, string takeId)
    {
        _musicCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.SoftDeleteMusicVersionAsync(_projectId, sceneNumber, takeId);
            if (res.Ok)
            {
                _musicCompareMessage = $"Deleted take {takeId}.";
                var resV = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
                _musicVersions = resV?.Versions;
                var resT = await Engine.GetTrashMusicVersionsAsync(_projectId, sceneNumber);
                _musicTrashVersions = resT?.Versions;
                await RefreshMusicCompareUrlsAsync();
            }
            else
            {
                _musicCompareMessage = res.Error ?? "Failed to delete audio take.";
            }
        }
        catch (Exception ex)
        {
            _musicCompareMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            StateHasChanged();
        }
    }

    private async Task RestoreMusicVersionAsync(int sceneNumber, string takeId)
    {
        _promotingMusicVersion = true;
        _musicCompareMessage = null;
        StateHasChanged();
        try
        {
            var res = await Engine.RestoreMusicVersionAsync(_projectId, sceneNumber, takeId);
            if (res.Ok)
            {
                _musicCompareMessage = "Take restored.";
                var versions = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
                _musicVersions = versions?.Versions;
                var trash = await Engine.GetTrashMusicVersionsAsync(_projectId, sceneNumber);
                _musicTrashVersions = trash?.Versions;
                await RefreshMusicCompareUrlsAsync();
            }
            else
            {
                _musicCompareMessage = res.Error ?? "Failed to restore audio take.";
            }
        }
        catch (Exception ex)
        {
            _musicCompareMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            _promotingMusicVersion = false;
            StateHasChanged();
        }
    }

    private bool _showSceneHistory;
    private int _historySceneNumber;
    private bool _loadingHistory;
    private bool _revertingScene;
    private string? _sceneRevertMessage;
    private List<SceneCommitHistoryItem>? _sceneHistory;

    private async Task OpenSceneHistoryAsync(int sceneNumber)
    {
        _historySceneNumber = sceneNumber;
        _showSceneHistory = true;
        _loadingHistory = true;
        _sceneRevertMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.GetSceneGitHistoryAsync(_projectId, sceneNumber);
            _sceneHistory = res?.History;
        }
        catch (Exception ex)
        {
            _error = $"Failed to load scene history: {ex.Message}";
        }
        finally
        {
            _loadingHistory = false;
            StateHasChanged();
        }
    }

    private void CloseSceneHistory()
    {
        _showSceneHistory = false;
        _sceneHistory = null;
        _sceneRevertMessage = null;
    }

    // ---- Inline scene VERSION history panel (SceneVersionHistory component, P3) — separate from
    // the git-commit history modal above; distinct state so the two panels never collide. ----
    private bool _showInlineSceneHistory;

    private void HideSceneHistory() => _showInlineSceneHistory = false;

    private async Task OnSceneHistoryRestored()
    {
        // A snapshot was restored server-side — refresh the scene list/detail to reflect it.
        _showInlineSceneHistory = false;
        await SoftReloadAsync();
    }

    private async Task RevertSceneToVersionAsync(int sceneNumber, string commitHash)
    {
        _revertingScene = true;
        _sceneRevertMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.RevertSceneToCommitAsync(_projectId, sceneNumber, commitHash);
            if (res.Ok)
            {
                _sceneRevertMessage = $"Successfully reverted Scene {sceneNumber:D2} to version {commitHash[..Math.Min(8, commitHash.Length)]}.";
                if (_detail is not null && _detail.SceneNumber == sceneNumber)
                {
                    _detail = (await Engine.GetSceneDetailAsync(_projectId, sceneNumber))?.Scene;
                }
                var scenesDto = await Engine.GetScenesAsync(_projectId);
                if (scenesDto?.Scenes is not null)
                {
                    _scenes = scenesDto.Scenes;
                }
            }
            else
            {
                _sceneRevertMessage = res.Error ?? "Failed to revert scene.";
            }
        }
        catch (Exception ex)
        {
            _sceneRevertMessage = $"Revert failed: {ex.Message}";
        }
        finally
        {
            _revertingScene = false;
            StateHasChanged();
        }
    }

    private UncommittedStatusDto? _uncommittedStatus;
    private bool _showClipCompare;
    private int _compareSceneNumber;
    private int _compareClipNumber;
    private bool _loadingClipVersions;
    private bool _promotingVersion;
    private string? _clipCompareMessage;
    private List<ClipVersionItem>? _clipVersions;
    private List<ClipVersionItem>? _trashVersions;
    private string? _selectedCompareVersionId;
    private bool _compareGridView = true;
    private bool _showTrashBin;
    private bool _showEmptyTrashConfirm;

    private ClipVersionItem? _selectedCompareVersion =>
        _clipVersions?.FirstOrDefault(v => string.Equals(v.VersionId, _selectedCompareVersionId, StringComparison.OrdinalIgnoreCase));

    private Dictionary<string, string?> _compareVideoUrls = new(StringComparer.OrdinalIgnoreCase);

    private string? CompareVideoUrl(ClipVersionItem v) => _compareVideoUrls.GetValueOrDefault(v.VersionId);

    /// <summary>
    /// Resolves a playable URL for every take in _clipVersions, once, instead of computing it
    /// inline per-render (both the grid and split-view markup need this, and a take flagged
    /// ClientOnly has no server bytes to stream — it has to go through the local media folder
    /// instead of the server URL the "normal" server-backed case uses).
    /// </summary>
    private async Task RefreshCompareVideoUrlsAsync()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (_clipVersions is { Count: > 0 })
        {
            foreach (var v in _clipVersions)
            {
                map[v.VersionId] = v.ClientOnly && !string.IsNullOrEmpty(v.RelativePath)
                    ? await MediaFolder.GetLocalBlobUrlAsync(_projectId, v.RelativePath)
                    : v.IsCurrent
                        ? Engine.ClipVideoUrl(_projectId, _compareSceneNumber, _compareClipNumber)
                        : Engine.BrowserMediaPath($"/api/projects/{Uri.EscapeDataString(_projectId)}/assets/video/history/{v.Mp4FileName}");
            }
        }
        _compareVideoUrls = map;
        StateHasChanged();
    }

    private async Task RefreshUncommittedStatusAsync()
    {
        try
        {
            var res = await Engine.GetProjectUncommittedStatusAsync(_projectId);
            _uncommittedStatus = res?.Status;
        }
        catch { /* best effort */ }
    }

    private async Task CommitCurrentChangesAsync()
    {
        _busy = true;
        _message = null;
        _error = null;
        StateHasChanged();

        try
        {
            var res = await Engine.CommitProjectChangesAsync(_projectId, "Manual scene/clip commit");
            if (res.Ok)
            {
                _message = "Successfully committed project changes.";
                await RefreshUncommittedStatusAsync();
            }
            else
            {
                _error = res.Error ?? "Failed to commit changes.";
            }
        }
        catch (Exception ex)
        {
            _error = $"Commit failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task OpenClipCompareAsync(int sceneNumber, int clipNumber)
    {
        _compareSceneNumber = sceneNumber;
        _compareClipNumber = clipNumber;
        _showClipCompare = true;
        _loadingClipVersions = true;
        _clipCompareMessage = null;
        _selectedCompareVersionId = null;
        _showTrashBin = false;
        _showEmptyTrashConfirm = false;
        StateHasChanged();

        try
        {
            var res = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
            _clipVersions = res?.Versions;
            _selectedCompareVersionId = _clipVersions?.FirstOrDefault(v => !v.IsCurrent)?.VersionId ?? _clipVersions?.FirstOrDefault()?.VersionId;
            await RefreshCompareVideoUrlsAsync();

            var trashRes = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
            _trashVersions = trashRes?.Versions;
        }
        catch (Exception ex)
        {
            _error = $"Failed to load clip versions: {ex.Message}";
        }
        finally
        {
            _loadingClipVersions = false;
            StateHasChanged();
        }
    }

    private void CloseClipCompare()
    {
        _showClipCompare = false;
        _clipVersions = null;
        _trashVersions = null;
        _clipCompareMessage = null;
        _showEmptyTrashConfirm = false;
    }

    private async Task PromoteClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
    {
        _promotingVersion = true;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.PromoteClipVersionAsync(_projectId, sceneNumber, clipNumber, versionId);
            if (res.Ok)
            {
                _clipCompareMessage = $"Successfully promoted version {versionId} to active clip.";
                var resV = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _clipVersions = resV?.Versions;
                _selectedCompareVersionId = _clipVersions?.FirstOrDefault(v => !v.IsCurrent)?.VersionId ?? _clipVersions?.FirstOrDefault()?.VersionId;
                await RefreshCompareVideoUrlsAsync();
                if (_detail is not null && _detail.SceneNumber == sceneNumber)
                {
                    _detail = (await Engine.GetSceneDetailAsync(_projectId, sceneNumber))?.Scene;
                }
                await RefreshUncommittedStatusAsync();
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to promote clip version.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Promote failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }

    private async Task SoftDeleteClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
    {
        _promotingVersion = true;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.SoftDeleteClipVersionAsync(_projectId, sceneNumber, clipNumber, versionId);
            if (res.Ok)
            {
                _clipCompareMessage = "Take deleted. You can restore it from the Trash Bin below.";
                var resV = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _clipVersions = resV?.Versions;
                var resT = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _trashVersions = resT?.Versions;
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to delete take.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }

    private async Task RestoreClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
    {
        _promotingVersion = true;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.RestoreClipVersionAsync(_projectId, sceneNumber, clipNumber, versionId);
            if (res.Ok)
            {
                _clipCompareMessage = "Take restored from Trash Bin.";
                var resV = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _clipVersions = resV?.Versions;
                var resT = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _trashVersions = resT?.Versions;
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to restore take.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }

    private async Task EmptyClipTrashAsync(int sceneNumber, int clipNumber)
    {
        _promotingVersion = true;
        _showEmptyTrashConfirm = false;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.EmptyClipTrashAsync(_projectId, sceneNumber, clipNumber);
            if (res.Ok)
            {
                _clipCompareMessage = "Purged deleted take(s).";
                var resT = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _trashVersions = resT?.Versions;
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to empty trash.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Empty trash failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }

    private bool _showVerificationModal;
    private int _verifModalSceneNumber;
    private int _verifModalClipNumber;
    private ClipDialogueVerificationResult? _verifModalResult;

    private void OpenVerificationModal(int sceneNumber, int clipNumber, ClipDialogueVerificationResult ver)
    {
        _verifModalSceneNumber = sceneNumber;
        _verifModalClipNumber = clipNumber;
        _verifModalResult = ver;
        _showVerificationModal = true;
    }

    private void CloseVerificationModal()
    {
        _showVerificationModal = false;
        _verifModalResult = null;
    }

    public async ValueTask DisposeAsync()
    {
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        MediaFolder.Changed -= OnMediaFolderChanged;
        _clientPreviewUrl = null;
        _clientSceneUrl = null;
        await Stitch.RevokePreviewUrlAsync();
    }
}

