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

public partial class Review
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private ReviewJobs? _jobs;
    internal ReviewJobs Jobs => _jobs ??= new ReviewJobs(this);
    private ReviewShare? _share;
    internal ReviewShare Share => _share ??= new ReviewShare(this);
    private ReviewAutoReview? _autoReview;
    internal ReviewAutoReview AutoReview => _autoReview ??= new ReviewAutoReview(this);
    private ReviewPlayback? _playback;
    internal ReviewPlayback Playback => _playback ??= new ReviewPlayback(this);
    private ReviewListState? _list;
    internal ReviewListState List => _list ??= new ReviewListState(this);

    internal void EnsureDomains()
    {
        _ = List; _ = Playback; _ = AutoReview; _ = Share; _ = Jobs;
    }


    internal bool _busy;

    internal bool _gateChecked;

    internal string? _error;

    internal string? _message;

    internal string _projectId = "";


    internal static string FormatClock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "—";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    internal const string passStatus = "pass";

    internal const string failStatus = "fail";


    internal sealed class EditRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Layer { get; set; } = "clip";
        public string Field { get; set; } = "";
        public string? CharKey { get; set; }
        public string Label { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public string Value { get; set; } = "";
        public string? Rationale { get; set; }
        public bool Include { get; set; } = true;
    }


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        try
        {
            try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
            if (!ActiveProject.HasProject)
                await ActiveProject.RefreshFromServerAsync(Engine);
            await ActiveProject.RefreshReadinessAsync(Engine);
            await Caps.RefreshAsync(Engine);
            _projectId = ActiveProject.ProjectId ?? "";
            _gateChecked = true;
            if (string.IsNullOrEmpty(_projectId) || !ActiveProject.CanReview)
            {
                HandleYouTubeOAuthRedirect();
                return;
            }

            try { await Hub.StartAsync(); } catch { /* optional */ }
            await LoadAsync();

            // Contextual sync: Review plays this project's media, so pull it to the local folder now
            // (replaces the old sync-on-every-page-load behaviour).
            try
            {
                await MediaFolder.EnsureHubHookAsync();
                if (!MediaFolder.IsConnected) await MediaFolder.TryReconnectAsync();
                MediaFolder.TriggerAutoSyncIfConnected();
            }
            catch { /* media folder optional for browse */ }

            HandleYouTubeOAuthRedirect();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }


    internal (int Scene, int Clip)? _clipServerSrcKey;


    internal static string FormatBytes(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#} MB" :
        n >= 1_000 ? $"{n / 1_000.0:0.#} KB" : $"{n} B";


    internal async Task ConfirmSaveAsync()
    {
        CheckIncompleteMovieState();
        if ((_incompleteScenesCount > 0 || _missingClipsCount > 0) && !_confirmedIncompletePublish)
        {
            _showIncompleteWarning = true;
            StateHasChanged();
            return;
        }

        _showIncompleteWarning = false;
        await PublishDemoAsync();
    }


    public async ValueTask DisposeAsync()
    {
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        _clientWipUrl = null;
        _clientSceneUrl = null;
        await Stitch.RevokePreviewUrlAsync();
        _dotNetRef?.Dispose();
    }


    // ── Field forwarders (Host._x for markup children) ──
    internal JobSnapshot? _job
    {
        get => Jobs._job;
        set => Jobs._job = value;
    }
    internal bool _confirmedIncompletePublish
    {
        get => Share._confirmedIncompletePublish;
        set => Share._confirmedIncompletePublish = value;
    }
    internal bool _demoAcceptedGuidelines
    {
        get => Share._demoAcceptedGuidelines;
        set => Share._demoAcceptedGuidelines = value;
    }
    internal string _demoDescription
    {
        get => Share._demoDescription;
        set => Share._demoDescription = value;
    }
    internal bool _demoIsAiSynthetic
    {
        get => Share._demoIsAiSynthetic;
        set => Share._demoIsAiSynthetic = value;
    }
    internal bool _demoMadeForKids
    {
        get => Share._demoMadeForKids;
        set => Share._demoMadeForKids = value;
    }
    internal string _demoTitle
    {
        get => Share._demoTitle;
        set => Share._demoTitle = value;
    }
    internal DotNetObjectReference<Review>? _dotNetRef
    {
        get => Share._dotNetRef;
        set => Share._dotNetRef = value;
    }
    internal int _incompleteScenesCount
    {
        get => Share._incompleteScenesCount;
        set => Share._incompleteScenesCount = value;
    }
    internal bool _isPublishing
    {
        get => Share._isPublishing;
        set => Share._isPublishing = value;
    }
    internal bool _lastExportMissingMusic
    {
        get => Share._lastExportMissingMusic;
        set => Share._lastExportMissingMusic = value;
    }
    internal int _missingClipsCount
    {
        get => Share._missingClipsCount;
        set => Share._missingClipsCount = value;
    }
    internal int _publishProgressPct
    {
        get => Share._publishProgressPct;
        set => Share._publishProgressPct = value;
    }
    internal string _publishProgressStatus
    {
        get => Share._publishProgressStatus;
        set => Share._publishProgressStatus = value;
    }
    internal bool _showIncompleteWarning
    {
        get => Share._showIncompleteWarning;
        set => Share._showIncompleteWarning = value;
    }
    internal string _youTubeDescription
    {
        get => Share._youTubeDescription;
        set => Share._youTubeDescription = value;
    }
    internal string _youTubePrivacy
    {
        get => Share._youTubePrivacy;
        set => Share._youTubePrivacy = value;
    }
    internal YouTubeStatusDto? _youTubeStatus
    {
        get => Share._youTubeStatus;
        set => Share._youTubeStatus = value;
    }
    internal string _youTubeTitle
    {
        get => Share._youTubeTitle;
        set => Share._youTubeTitle = value;
    }
    internal YouTubeUploadInfo? _youTubeUpload
    {
        get => Share._youTubeUpload;
        set => Share._youTubeUpload = value;
    }
    internal Dictionary<string, ClipAutoReviewDraft> _drafts => AutoReview._drafts;
    internal string? _editKey
    {
        get => AutoReview._editKey;
        set => AutoReview._editKey = value;
    }
    internal List<EditRow>? _editRows
    {
        get => AutoReview._editRows;
        set => AutoReview._editRows = value;
    }
    internal List<EditLogEntry> _entries
    {
        get => AutoReview._entries;
        set => AutoReview._entries = value;
    }
    internal bool _isReviewing
    {
        get => AutoReview._isReviewing;
        set => AutoReview._isReviewing = value;
    }
    internal MovieAutoReviewReport? _movieReport
    {
        get => AutoReview._movieReport;
        set => AutoReview._movieReport = value;
    }
    internal string _note
    {
        get => AutoReview._note;
        set => AutoReview._note = value;
    }
    internal int _reviewProgressPct
    {
        get => AutoReview._reviewProgressPct;
        set => AutoReview._reviewProgressPct = value;
    }
    internal string _reviewProgressStatus
    {
        get => AutoReview._reviewProgressStatus;
        set => AutoReview._reviewProgressStatus = value;
    }
    internal Dictionary<string, string> _reviews
    {
        get => AutoReview._reviews;
        set => AutoReview._reviews = value;
    }
    internal string? _clientSceneUrl
    {
        get => Playback._clientSceneUrl;
        set => Playback._clientSceneUrl = value;
    }
    internal string? _clientStitchStatus
    {
        get => Playback._clientStitchStatus;
        set => Playback._clientStitchStatus = value;
    }
    internal bool _clientStitching
    {
        get => Playback._clientStitching;
        set => Playback._clientStitching = value;
    }
    internal string? _clientWipUrl
    {
        get => Playback._clientWipUrl;
        set => Playback._clientWipUrl = value;
    }
    internal string? _clipServerSrcCached
    {
        get => Playback._clipServerSrcCached;
        set => Playback._clipServerSrcCached = value;
    }
    internal long _clipVideoKey
    {
        get => Playback._clipVideoKey;
        set => Playback._clipVideoKey = value;
    }
    internal string? _dubStatus
    {
        get => Playback._dubStatus;
        set => Playback._dubStatus = value;
    }
    internal bool _dubbing
    {
        get => Playback._dubbing;
        set => Playback._dubbing = value;
    }
    internal int? _playSceneAfterRemux
    {
        get => Playback._playSceneAfterRemux;
        set => Playback._playSceneAfterRemux = value;
    }
    internal bool _playWipAfterRemux
    {
        get => Playback._playWipAfterRemux;
        set => Playback._playWipAfterRemux = value;
    }
    internal int? _playingClipNum
    {
        get => Playback._playingClipNum;
        set => Playback._playingClipNum = value;
    }
    internal int? _playingClipScene
    {
        get => Playback._playingClipScene;
        set => Playback._playingClipScene = value;
    }
    internal int? _playingScene
    {
        get => Playback._playingScene;
        set => Playback._playingScene = value;
    }
    internal string _preferredVideoEditor
    {
        get => Playback._preferredVideoEditor;
        set => Playback._preferredVideoEditor = value;
    }
    internal string? _sceneServerSrcCached
    {
        get => Playback._sceneServerSrcCached;
        set => Playback._sceneServerSrcCached = value;
    }
    internal int? _sceneServerSrcScene
    {
        get => Playback._sceneServerSrcScene;
        set => Playback._sceneServerSrcScene = value;
    }
    internal long _sceneVideoKey
    {
        get => Playback._sceneVideoKey;
        set => Playback._sceneVideoKey = value;
    }
    internal bool _showClipPlayer
    {
        get => Playback._showClipPlayer;
        set => Playback._showClipPlayer = value;
    }
    internal bool _showScenePlayer
    {
        get => Playback._showScenePlayer;
        set => Playback._showScenePlayer = value;
    }
    internal bool _showWipPlayer
    {
        get => Playback._showWipPlayer;
        set => Playback._showWipPlayer = value;
    }
    internal long _wipBytes
    {
        get => Playback._wipBytes;
        set => Playback._wipBytes = value;
    }
    internal bool _wipCanBuild
    {
        get => Playback._wipCanBuild;
        set => Playback._wipCanBuild = value;
    }
    internal bool _wipExists
    {
        get => Playback._wipExists;
        set => Playback._wipExists = value;
    }
    internal string? _wipPath
    {
        get => Playback._wipPath;
        set => Playback._wipPath = value;
    }
    internal string? _wipReason
    {
        get => Playback._wipReason;
        set => Playback._wipReason = value;
    }
    internal string? _wipServerSrcCached
    {
        get => Playback._wipServerSrcCached;
        set => Playback._wipServerSrcCached = value;
    }
    internal string? _wipServerSrcForProject
    {
        get => Playback._wipServerSrcForProject;
        set => Playback._wipServerSrcForProject = value;
    }
    internal bool _wipStale
    {
        get => Playback._wipStale;
        set => Playback._wipStale = value;
    }
    internal string? _wipUpdatedAt
    {
        get => Playback._wipUpdatedAt;
        set => Playback._wipUpdatedAt = value;
    }
    internal long _wipVideoKey
    {
        get => Playback._wipVideoKey;
        set => Playback._wipVideoKey = value;
    }
    internal string? _activeTab
    {
        get => List._activeTab;
        set => List._activeTab = value;
    }
    internal HashSet<string> _expandedSceneGroups => List._expandedSceneGroups;
    internal bool _sceneSortAsc
    {
        get => List._sceneSortAsc;
        set => List._sceneSortAsc = value;
    }
    internal string _sceneSortBy
    {
        get => List._sceneSortBy;
        set => List._sceneSortBy = value;
    }
    internal List<SceneSummary> _scenes
    {
        get => List._scenes;
        set => List._scenes = value;
    }
    internal SceneDetail? _selectedDetail
    {
        get => List._selectedDetail;
        set => List._selectedDetail = value;
    }
    internal int? _selectedScene
    {
        get => List._selectedScene;
        set => List._selectedScene = value;
    }
    internal bool _showActivity
    {
        get => List._showActivity;
        set => List._showActivity = value;
    }
}
