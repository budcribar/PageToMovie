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
    // ── Domain facades (typed modules; behavior in Scenes.*.cs partials) ──
    internal ScenesListState List { get; private set; } = null!;
    internal ScenesClipEditor ClipEd { get; private set; } = null!;
    internal ScenesGeneration Gen { get; private set; } = null!;
    internal ScenesPlayback Playback { get; private set; } = null!;
    internal ScenesDialogueVerify Dialogue { get; private set; } = null!;
    internal ScenesMusic Music { get; private set; } = null!;
    internal ScenesHistory History { get; private set; } = null!;

    /// <summary>Construct domain facades. Idempotent.</summary>
    internal void EnsureDomains()
    {
        List ??= new ScenesListState(this);
        ClipEd ??= new ScenesClipEditor(this);
        Gen ??= new ScenesGeneration(this);
        Playback ??= new ScenesPlayback(this);
        Dialogue ??= new ScenesDialogueVerify(this);
        Music ??= new ScenesMusic(this);
        History ??= new ScenesHistory(this);
    }


    internal bool IsSimpleFilm =>
        ActiveProject.IsSimpleVoice
        || (Nav.ToAbsoluteUri(Nav.Uri).Query?.Contains("simple=1", StringComparison.OrdinalIgnoreCase) ?? false);


    internal bool _busy;

    internal bool _gateChecked;

    internal string? _error;

    internal string? _message;

    internal string _projectId = "";

    internal List<string> _projectIds = new();

    internal string _pickSetting = "";

    internal string _pickCharacter = "";

    internal string _pickLocation = "";

    internal bool _showFilters;


    /// <summary>Video gen resolution (defaults from Configuration).</summary>
    internal string _genResolution = "480p";

    /// <summary>Resolution already used by this project's on-disk clips, if consistent — null when unset.</summary>
    internal string? _resolutionLock;

    internal string _sortBy = "number";

    internal bool _sortAscending = true;

    /// <summary>Clip table: when true, sort by duration; else keep plan order (clip number).</summary>
    internal bool _clipSortByDuration;

    internal bool _clipSortAscending = true;

    /// <summary>Cost estimate at the current resolution, refreshed on load and resolution change.</summary>
    internal CostReport? _costReport;

    /// <summary>Project-wide cast gate: every character voice + locked image before video spend.</summary>
    internal bool _castChecked;

    internal bool _castReady;

    internal int? _castReadyCount;

    internal int? _castTotal;

    internal List<string> _castMissing = new();

    internal List<SceneSummary>? _scenes;

    internal HashSet<int> _selected = new();

    internal string _selectionMode = "";

    internal int? _selectedScene;

    internal SceneDetail? _detail;

    internal int? _selectedClip;

    internal ClipSummary? _clip;

    internal (int Scene, int Clip)? _deleteClipTarget;

    internal int? _deleteSceneTarget;

    internal ClipEditRequest? _clipEditor;

    internal bool _clipEditorIsNew;

    internal HashSet<string> _clipEditorCast = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Multi-select clip numbers within the currently open scene's clip table, for batch regen.</summary>
    internal readonly HashSet<int> _selectedClips = new();

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

    internal int? _playSceneAfterRemux;

    internal bool _showScenePlayer;

    internal int? _playingScene;

    internal long _sceneVideoKey;

    internal long _inlineCompositeKey;

    internal long _clipVideoKey;

    /// <summary>"Play selected" — multi-scene (possibly non-contiguous) client-stitched preview.</summary>
    internal bool _showPreviewPlayer;

    internal long _previewVideoKey;

    internal List<int> _previewScenes = new();

    internal string? _clientPreviewUrl;

    internal string? _clientSceneUrl;

    internal bool _clientStitching;

    internal string? _clientStitchStatus;


    /// <summary>
    /// Set for the brief window between kicking off a regen and the job snapshot round-trip
    /// confirming it server-side — closes the gap where <see cref="IsSceneGenBusy"/> would
    /// otherwise still see the previous (already-finished) job and let a stale composite show.
    /// </summary>
    internal int? _pendingRegenScene;


    internal bool DetailLockedByOther =>
        _detail is not null &&
        (_scenes?.FirstOrDefault(s => s.SceneNumber == _detail.SceneNumber)?.LockedByOther ?? false);


    internal string? _detailLockOwner =>
        _detail is null
            ? null
            : _scenes?.FirstOrDefault(s => s.SceneNumber == _detail.SceneNumber)?.LockOwnerUserId;


    internal List<string> CharacterOptions
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


    internal List<string> LocationOptions =>
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


    internal Task AdjustFitLengthAsync() => ConfirmScreenplayAdjustAndNavigateAsync("adaptation/trim");


    internal Task AdjustEmbellishAsync() => ConfirmScreenplayAdjustAndNavigateAsync("adaptation/embellish");


    // Read-only: just open the screenplay view. Unlike Fit length / Embellish it does not re-open
    // (un-approve) the screenplay, so no confirm — the user asked to "go back and see" it from Film.
    internal void ViewScreenplay() => Nav.NavigateTo("adaptation/screenplay");


    /// <summary>
    /// Navigate to a screenplay-shaping step (Fit length / Enrich). Those edit the screenplay, which
    /// un-approves it and re-gates cast, so confirm first rather than surprising the user mid-Film.
    /// </summary>
    internal async Task ConfirmScreenplayAdjustAndNavigateAsync(string route)
    {
        if (JobRunning) return;
        var ok = await JS.InvokeAsync<bool>(
            "confirm",
            "This opens the screenplay to change it. You'll re-approve the screenplay afterward, " +
            "and the cast will re-check against the updated script. Continue?");
        if (ok)
            Nav.NavigateTo(route);
    }


    internal void ResetPickers()
    {
        _pickSetting = "";
        _pickCharacter = "";
        _pickLocation = "";
    }


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
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


    internal void OnMediaFolderChanged() => _ = InvokeAsync(async () =>
    {
        if (_showScenePlayer && _playingScene is int sn && !MediaFolder.IsSyncing && string.IsNullOrEmpty(_clientSceneUrl))
        {
            await PlaySceneCompositeAsync(sn);
        }
        StateHasChanged();
    });


    internal void DismissLocalSaveWarning() => MediaFolder.DismissLocalSaveWarning();


    internal async Task ConnectMediaFolderFromWarningAsync()
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


    internal string _preferredVideoEditor = "ClipChamp";


    internal async Task OnProjectChangedAsync()
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


    internal string? _clipVideoUrl;

    internal string? _clipServerVideoUrl;

    internal bool _clipVideoLoading;

    internal string? _sceneCompositeVideoUrl;

    internal string? _sceneCompositeServerUrl;


    internal int? _scenePlayerServerSrcScene;

    internal string? _scenePlayerServerSrcCached;


    internal static string StatusBadge(string status) => status switch
    {
        "complete" => "bg-success",
        "partial" => "bg-warning text-dark",
        _ => "bg-secondary",
    };


    internal static string Trunc(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }


    internal static string ShortChar(string key) => KeyFormatting.ShortChar(key);


    internal static string ShortLoc(string key) => KeyFormatting.ShortLoc(key);


    internal static string ShortDelivery(string? key) => KeyFormatting.ShortDelivery(key);


    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024):0.#} MB";
    }


    internal static string CacheBust(string url) => KeyFormatting.CacheBust(url);


    /// <summary>Format seconds as m:ss or plain seconds when under a minute.</summary>
    internal static string FormatClock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var whole = (int)Math.Round(seconds);
        if (whole < 60) return $"{whole}s";
        var m = whole / 60;
        var s = whole % 60;
        return $"{m}:{s:D2}";
    }


    internal bool _verifyingClip;

    internal int _verifyingClipNumber;

    internal int _verifyCurrent;

    internal int _verifyTotal;

    internal string _verifyStatusLabel = "Verifying dialogue...";


    internal bool _scoringMusic;

    internal List<SupportedModelDto> _audioModels = new();

    internal string _selectedAudioModel = "fal-ai/stable-audio";

    internal bool _wantVocal;


    // Which scene's Score chooser is open (null = closed). The model/Sing picks it edits are the
    // shared _selectedAudioModel/_wantVocal, so they persist as the defaults for the next scene.
    internal int? _scoreMenuScene;


    // Batch-generate confirm modal: resolution + cost decided at the moment of spend.
    internal bool _showGenerateConfirm;

    internal bool _showVideoEditPrompt;

    internal string _videoEditPromptText = "";

    /// <summary>
    /// xAI's /v1/videos/edits input cap (grok-imagine-video-edit's maxEditInputDurationSeconds).
    /// A client-side UX hint only — RunVideoEditAsync re-checks the real catalog value
    /// server-side and is the authoritative gate; this just disables the button early.
    /// </summary>
    internal const double MaxVideoEditInputSeconds = 8.7;


    // Admin-only: video models offered as a one-off per-batch override in the Generate modal, so an
    // admin can A/B different generators without editing project Configuration. "" = project default.
    internal List<SupportedModelDto> _videoModels = new();

    internal string _selectedVideoModel = "";


    internal bool _showMusicCompare;

    internal int _compareMusicSceneNumber;

    internal List<MusicVersionItem>? _musicVersions;

    internal List<MusicVersionItem>? _musicTrashVersions;

    internal bool _loadingMusicVersions;

    internal string? _musicCompareMessage;

    internal bool _promotingMusicVersion;

    internal bool _showMusicTrash;

    internal Dictionary<string, List<string>> _musicCompareUrls = new(StringComparer.OrdinalIgnoreCase);


    internal bool _showSceneHistory;

    internal int _historySceneNumber;

    internal bool _loadingHistory;

    internal bool _revertingScene;

    internal string? _sceneRevertMessage;

    internal List<SceneCommitHistoryItem>? _sceneHistory;


    // ---- Inline scene VERSION history panel (SceneVersionHistory component, P3) — separate from
    // the git-commit history modal above; distinct state so the two panels never collide. ----
    internal bool _showInlineSceneHistory;


    internal UncommittedStatusDto? _uncommittedStatus;

    internal bool _showClipCompare;

    internal int _compareSceneNumber;

    internal int _compareClipNumber;

    internal bool _loadingClipVersions;

    internal bool _promotingVersion;

    internal string? _clipCompareMessage;

    internal List<ClipVersionItem>? _clipVersions;

    internal List<ClipVersionItem>? _trashVersions;

    internal string? _selectedCompareVersionId;


    internal Dictionary<string, string?> _compareVideoUrls = new(StringComparer.OrdinalIgnoreCase);


    internal async Task RefreshUncommittedStatusAsync()
    {
        try
        {
            var res = await Engine.GetProjectUncommittedStatusAsync(_projectId);
            _uncommittedStatus = res?.Status;
        }
        catch { /* best effort */ }
    }


    internal async Task CommitCurrentChangesAsync()
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


    internal bool _showVerificationModal;

    internal int _verifModalSceneNumber;

    internal int _verifModalClipNumber;

    internal ClipDialogueVerificationResult? _verifModalResult;


    internal void OpenVerificationModal(int sceneNumber, int clipNumber, ClipDialogueVerificationResult ver)
    {
        _verifModalSceneNumber = sceneNumber;
        _verifModalClipNumber = clipNumber;
        _verifModalResult = ver;
        _showVerificationModal = true;
    }


    internal void CloseVerificationModal()
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
