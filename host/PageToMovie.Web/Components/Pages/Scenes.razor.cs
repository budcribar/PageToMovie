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
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private ScenesHistory? _history;
    internal ScenesHistory History => _history ??= new ScenesHistory(this);
    private ScenesMusic? _music;
    internal ScenesMusic Music => _music ??= new ScenesMusic(this);
    private ScenesDialogueVerify? _dialogue;
    internal ScenesDialogueVerify Dialogue => _dialogue ??= new ScenesDialogueVerify(this);
    private ScenesPlayback? _playback;
    internal ScenesPlayback Playback => _playback ??= new ScenesPlayback(this);
    private ScenesGeneration? _gen;
    internal ScenesGeneration Gen => _gen ??= new ScenesGeneration(this);
    private ScenesListState? _list;
    internal ScenesListState List => _list ??= new ScenesListState(this);
    private ScenesClipEditor? _clipEd;
    internal ScenesClipEditor ClipEd => _clipEd ??= new ScenesClipEditor(this);

    /// <summary>Eagerly construct all domain modules (optional; lazy props also work).</summary>
    internal void EnsureDomains()
    {
        _ = List; _ = ClipEd; _ = Gen; _ = Playback; _ = Dialogue; _ = Music; _ = History;
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


    /// <summary>
    /// xAI's /v1/videos/edits input cap (grok-imagine-video-edit's maxEditInputDurationSeconds).
    /// A client-side UX hint only — RunVideoEditAsync re-checks the real catalog value
    /// server-side and is the authoritative gate; this just disables the button early.
    /// </summary>
    internal const double MaxVideoEditInputSeconds = 8.7;


    internal Dictionary<string, List<string>> _musicCompareUrls = new(StringComparer.OrdinalIgnoreCase);



    internal UncommittedStatusDto? _uncommittedStatus;



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


    // ── Field forwarders (Host._x still works for markup children) ──
    internal bool _showSceneHistory
    {
        get => History._showSceneHistory;
        set => History._showSceneHistory = value;
    }
    internal int _historySceneNumber
    {
        get => History._historySceneNumber;
        set => History._historySceneNumber = value;
    }
    internal bool _loadingHistory
    {
        get => History._loadingHistory;
        set => History._loadingHistory = value;
    }
    internal bool _revertingScene
    {
        get => History._revertingScene;
        set => History._revertingScene = value;
    }
    internal string? _sceneRevertMessage
    {
        get => History._sceneRevertMessage;
        set => History._sceneRevertMessage = value;
    }
    internal List<SceneCommitHistoryItem>? _sceneHistory
    {
        get => History._sceneHistory;
        set => History._sceneHistory = value;
    }
    internal bool _showInlineSceneHistory
    {
        get => History._showInlineSceneHistory;
        set => History._showInlineSceneHistory = value;
    }
    internal bool _scoringMusic
    {
        get => Music._scoringMusic;
        set => Music._scoringMusic = value;
    }
    internal List<SupportedModelDto> _audioModels
    {
        get => Music._audioModels;
        set => Music._audioModels = value;
    }
    internal string _selectedAudioModel
    {
        get => Music._selectedAudioModel;
        set => Music._selectedAudioModel = value;
    }
    internal bool _wantVocal
    {
        get => Music._wantVocal;
        set => Music._wantVocal = value;
    }
    internal int? _scoreMenuScene
    {
        get => Music._scoreMenuScene;
        set => Music._scoreMenuScene = value;
    }
    internal bool _showMusicCompare
    {
        get => Music._showMusicCompare;
        set => Music._showMusicCompare = value;
    }
    internal int _compareMusicSceneNumber
    {
        get => Music._compareMusicSceneNumber;
        set => Music._compareMusicSceneNumber = value;
    }
    internal List<MusicVersionItem>? _musicVersions
    {
        get => Music._musicVersions;
        set => Music._musicVersions = value;
    }
    internal List<MusicVersionItem>? _musicTrashVersions
    {
        get => Music._musicTrashVersions;
        set => Music._musicTrashVersions = value;
    }
    internal bool _loadingMusicVersions
    {
        get => Music._loadingMusicVersions;
        set => Music._loadingMusicVersions = value;
    }
    internal string? _musicCompareMessage
    {
        get => Music._musicCompareMessage;
        set => Music._musicCompareMessage = value;
    }
    internal bool _promotingMusicVersion
    {
        get => Music._promotingMusicVersion;
        set => Music._promotingMusicVersion = value;
    }
    internal bool _showMusicTrash
    {
        get => Music._showMusicTrash;
        set => Music._showMusicTrash = value;
    }
    internal bool _verifyingClip
    {
        get => Dialogue._verifyingClip;
        set => Dialogue._verifyingClip = value;
    }
    internal int _verifyingClipNumber
    {
        get => Dialogue._verifyingClipNumber;
        set => Dialogue._verifyingClipNumber = value;
    }
    internal int _verifyCurrent
    {
        get => Dialogue._verifyCurrent;
        set => Dialogue._verifyCurrent = value;
    }
    internal int _verifyTotal
    {
        get => Dialogue._verifyTotal;
        set => Dialogue._verifyTotal = value;
    }
    internal string _verifyStatusLabel
    {
        get => Dialogue._verifyStatusLabel;
        set => Dialogue._verifyStatusLabel = value;
    }
    internal bool _showVerificationModal
    {
        get => Dialogue._showVerificationModal;
        set => Dialogue._showVerificationModal = value;
    }
    internal int _verifModalSceneNumber
    {
        get => Dialogue._verifModalSceneNumber;
        set => Dialogue._verifModalSceneNumber = value;
    }
    internal int _verifModalClipNumber
    {
        get => Dialogue._verifModalClipNumber;
        set => Dialogue._verifModalClipNumber = value;
    }
    internal ClipDialogueVerificationResult? _verifModalResult
    {
        get => Dialogue._verifModalResult;
        set => Dialogue._verifModalResult = value;
    }
    internal int? _playSceneAfterRemux
    {
        get => Playback._playSceneAfterRemux;
        set => Playback._playSceneAfterRemux = value;
    }
    internal bool _showScenePlayer
    {
        get => Playback._showScenePlayer;
        set => Playback._showScenePlayer = value;
    }
    internal int? _playingScene
    {
        get => Playback._playingScene;
        set => Playback._playingScene = value;
    }
    internal long _sceneVideoKey
    {
        get => Playback._sceneVideoKey;
        set => Playback._sceneVideoKey = value;
    }
    internal long _inlineCompositeKey
    {
        get => Playback._inlineCompositeKey;
        set => Playback._inlineCompositeKey = value;
    }
    internal long _clipVideoKey
    {
        get => Playback._clipVideoKey;
        set => Playback._clipVideoKey = value;
    }
    internal bool _showPreviewPlayer
    {
        get => Playback._showPreviewPlayer;
        set => Playback._showPreviewPlayer = value;
    }
    internal long _previewVideoKey
    {
        get => Playback._previewVideoKey;
        set => Playback._previewVideoKey = value;
    }
    internal List<int> _previewScenes
    {
        get => Playback._previewScenes;
        set => Playback._previewScenes = value;
    }
    internal string? _clientPreviewUrl
    {
        get => Playback._clientPreviewUrl;
        set => Playback._clientPreviewUrl = value;
    }
    internal string? _clientSceneUrl
    {
        get => Playback._clientSceneUrl;
        set => Playback._clientSceneUrl = value;
    }
    internal bool _clientStitching
    {
        get => Playback._clientStitching;
        set => Playback._clientStitching = value;
    }
    internal string? _clientStitchStatus
    {
        get => Playback._clientStitchStatus;
        set => Playback._clientStitchStatus = value;
    }
    internal string? _clipVideoUrl
    {
        get => Playback._clipVideoUrl;
        set => Playback._clipVideoUrl = value;
    }
    internal string? _clipServerVideoUrl
    {
        get => Playback._clipServerVideoUrl;
        set => Playback._clipServerVideoUrl = value;
    }
    internal bool _clipVideoLoading
    {
        get => Playback._clipVideoLoading;
        set => Playback._clipVideoLoading = value;
    }
    internal string? _sceneCompositeVideoUrl
    {
        get => Playback._sceneCompositeVideoUrl;
        set => Playback._sceneCompositeVideoUrl = value;
    }
    internal string? _sceneCompositeServerUrl
    {
        get => Playback._sceneCompositeServerUrl;
        set => Playback._sceneCompositeServerUrl = value;
    }
    internal int? _scenePlayerServerSrcScene
    {
        get => Playback._scenePlayerServerSrcScene;
        set => Playback._scenePlayerServerSrcScene = value;
    }
    internal string? _scenePlayerServerSrcCached
    {
        get => Playback._scenePlayerServerSrcCached;
        set => Playback._scenePlayerServerSrcCached = value;
    }
    internal Dictionary<string, string?> _compareVideoUrls
    {
        get => Playback._compareVideoUrls;
        set => Playback._compareVideoUrls = value;
    }
    internal JobSnapshot? _job
    {
        get => Gen._job;
        set => Gen._job = value;
    }
    internal List<JobSnapshot> _myJobs
    {
        get => Gen._myJobs;
        set => Gen._myJobs = value;
    }
    internal bool _showAdminJobLog
    {
        get => Gen._showAdminJobLog;
        set => Gen._showAdminJobLog = value;
    }
    internal int _progressFloor
    {
        get => Gen._progressFloor;
        set => Gen._progressFloor = value;
    }
    internal string? _progressFloorJobId
    {
        get => Gen._progressFloorJobId;
        set => Gen._progressFloorJobId = value;
    }
    internal int _lastListRefreshIndex
    {
        get => Gen._lastListRefreshIndex;
        set => Gen._lastListRefreshIndex = value;
    }
    internal int? _lastListRefreshScene
    {
        get => Gen._lastListRefreshScene;
        set => Gen._lastListRefreshScene = value;
    }
    internal string? _lastListRefreshMessage
    {
        get => Gen._lastListRefreshMessage;
        set => Gen._lastListRefreshMessage = value;
    }
    internal DateTimeOffset _lastListRefreshAt
    {
        get => Gen._lastListRefreshAt;
        set => Gen._lastListRefreshAt = value;
    }
    internal bool _listRefreshInFlight
    {
        get => Gen._listRefreshInFlight;
        set => Gen._listRefreshInFlight = value;
    }
    internal int? _pendingRegenScene
    {
        get => Gen._pendingRegenScene;
        set => Gen._pendingRegenScene = value;
    }
    internal bool _showGenerateConfirm
    {
        get => Gen._showGenerateConfirm;
        set => Gen._showGenerateConfirm = value;
    }
    internal List<SupportedModelDto> _videoModels
    {
        get => Gen._videoModels;
        set => Gen._videoModels = value;
    }
    internal string _selectedVideoModel
    {
        get => Gen._selectedVideoModel;
        set => Gen._selectedVideoModel = value;
    }
    internal string _genResolution
    {
        get => Gen._genResolution;
        set => Gen._genResolution = value;
    }
    internal string _pickSetting
    {
        get => List._pickSetting;
        set => List._pickSetting = value;
    }
    internal string _pickCharacter
    {
        get => List._pickCharacter;
        set => List._pickCharacter = value;
    }
    internal string _pickLocation
    {
        get => List._pickLocation;
        set => List._pickLocation = value;
    }
    internal bool _showFilters
    {
        get => List._showFilters;
        set => List._showFilters = value;
    }
    internal string? _resolutionLock
    {
        get => List._resolutionLock;
        set => List._resolutionLock = value;
    }
    internal string _sortBy
    {
        get => List._sortBy;
        set => List._sortBy = value;
    }
    internal bool _sortAscending
    {
        get => List._sortAscending;
        set => List._sortAscending = value;
    }
    internal CostReport? _costReport
    {
        get => List._costReport;
        set => List._costReport = value;
    }
    internal bool _castChecked
    {
        get => List._castChecked;
        set => List._castChecked = value;
    }
    internal bool _castReady
    {
        get => List._castReady;
        set => List._castReady = value;
    }
    internal int? _castReadyCount
    {
        get => List._castReadyCount;
        set => List._castReadyCount = value;
    }
    internal int? _castTotal
    {
        get => List._castTotal;
        set => List._castTotal = value;
    }
    internal List<string> _castMissing
    {
        get => List._castMissing;
        set => List._castMissing = value;
    }
    internal List<SceneSummary>? _scenes
    {
        get => List._scenes;
        set => List._scenes = value;
    }
    internal HashSet<int> _selected
    {
        get => List._selected;
        set => List._selected = value;
    }
    internal string _selectionMode
    {
        get => List._selectionMode;
        set => List._selectionMode = value;
    }
    internal int? _selectedScene
    {
        get => List._selectedScene;
        set => List._selectedScene = value;
    }
    internal SceneDetail? _detail
    {
        get => List._detail;
        set => List._detail = value;
    }
    internal int? _deleteSceneTarget
    {
        get => List._deleteSceneTarget;
        set => List._deleteSceneTarget = value;
    }
    internal bool _clipSortByDuration
    {
        get => ClipEd._clipSortByDuration;
        set => ClipEd._clipSortByDuration = value;
    }
    internal bool _clipSortAscending
    {
        get => ClipEd._clipSortAscending;
        set => ClipEd._clipSortAscending = value;
    }
    internal int? _selectedClip
    {
        get => ClipEd._selectedClip;
        set => ClipEd._selectedClip = value;
    }
    internal ClipSummary? _clip
    {
        get => ClipEd._clip;
        set => ClipEd._clip = value;
    }
    internal ClipEditRequest? _clipEditor
    {
        get => ClipEd._clipEditor;
        set => ClipEd._clipEditor = value;
    }
    internal bool _clipEditorIsNew
    {
        get => ClipEd._clipEditorIsNew;
        set => ClipEd._clipEditorIsNew = value;
    }
    internal HashSet<string> _clipEditorCast
    {
        get => ClipEd._clipEditorCast;
        set => ClipEd._clipEditorCast = value;
    }
    internal HashSet<int> _selectedClips => ClipEd._selectedClips;
    internal bool _showVideoEditPrompt
    {
        get => ClipEd._showVideoEditPrompt;
        set => ClipEd._showVideoEditPrompt = value;
    }
    internal string _videoEditPromptText
    {
        get => ClipEd._videoEditPromptText;
        set => ClipEd._videoEditPromptText = value;
    }
    internal string _preferredVideoEditor
    {
        get => ClipEd._preferredVideoEditor;
        set => ClipEd._preferredVideoEditor = value;
    }
    internal bool _showClipCompare
    {
        get => ClipEd._showClipCompare;
        set => ClipEd._showClipCompare = value;
    }
    internal int _compareSceneNumber
    {
        get => ClipEd._compareSceneNumber;
        set => ClipEd._compareSceneNumber = value;
    }
    internal int _compareClipNumber
    {
        get => ClipEd._compareClipNumber;
        set => ClipEd._compareClipNumber = value;
    }
    internal bool _loadingClipVersions
    {
        get => ClipEd._loadingClipVersions;
        set => ClipEd._loadingClipVersions = value;
    }
    internal bool _promotingVersion
    {
        get => ClipEd._promotingVersion;
        set => ClipEd._promotingVersion = value;
    }
    internal string? _clipCompareMessage
    {
        get => ClipEd._clipCompareMessage;
        set => ClipEd._clipCompareMessage = value;
    }
    internal List<ClipVersionItem>? _clipVersions
    {
        get => ClipEd._clipVersions;
        set => ClipEd._clipVersions = value;
    }
    internal List<ClipVersionItem>? _trashVersions
    {
        get => ClipEd._trashVersions;
        set => ClipEd._trashVersions = value;
    }
    internal string? _selectedCompareVersionId
    {
        get => ClipEd._selectedCompareVersionId;
        set => ClipEd._selectedCompareVersionId = value;
    }
}
