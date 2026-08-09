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

public partial class Characters
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private CharactersListState? _list;
    internal CharactersListState List => _list ??= new CharactersListState(this);
    private CharactersLook? _look;
    internal CharactersLook Look => _look ??= new CharactersLook(this);
    private CharactersVoice? _voice;
    internal CharactersVoice Voice => _voice ??= new CharactersVoice(this);
    private CharactersJobs? _jobs;
    internal CharactersJobs Jobs => _jobs ??= new CharactersJobs(this);

    internal void EnsureDomains()
    {
        _ = List; _ = Look; _ = Voice; _ = Jobs;
    }


    internal enum Mode { PickSource, WaitingGenerate, Compare }


    /// <summary>First choose how to set the look — only that path's UI is shown.</summary>
    internal enum PictureRoute { Choose, Generate, Upload, Book }


    internal sealed class Candidate
    {
        public string Kind { get; init; } = ""; // book | variant | locked | preferred
        public int Index { get; init; }
        public string Label { get; init; } = "";
        public string Url { get; init; } = "";
    }


    internal sealed class PendingDelete
    {
        public string Kind { get; init; } = "";
        public int Index { get; init; }
    }


    internal bool _busy;

    internal bool _gateChecked;

    internal string? _error;

    internal string? _message;

    internal string _projectId = "";

    internal List<string> _projectIds = new();

    internal const int LookAutosaveDebounceMs = 800;


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        await ActiveProject.EnsureLoadedAsync(Engine);
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
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

            ApplySimpleModeFromUri();
            // Easy-start lives entirely on /simple-voice (story + record). No cast list.
            if (_simpleMode)
            {
                Nav.NavigateTo("simple-voice");
                return;
            }


            if (string.IsNullOrEmpty(_projectId) || !ActiveProject.CanCharacters)
                return;

            try { await Hub.StartAsync(); } catch { /* optional */ }

            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }


    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Optional: could wire JS keyboard; L/R handled via buttons for now
        await Task.CompletedTask;
    }


    /// <summary>
    /// Server messages sometimes append " · Character_X, Character_Y…". Drop that list
    /// from the green banner (admin panel still shows full keys).
    /// </summary>
    internal static string StripTrailingKeyDump(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        var idx = message.IndexOf(" · Character_", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return message[..idx].TrimEnd();
        idx = message.IndexOf(" · Character_", StringComparison.Ordinal);
        if (idx > 0) return message[..idx].TrimEnd();
        // Also " — Character_Foo, Character_Bar"
        idx = message.IndexOf(" — Character_", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return message[..idx].TrimEnd();
        return message.Trim();
    }


    internal async Task RefreshNavReadinessAsync()
    {
        try { await ActiveProject.RefreshReadinessAsync(Engine); }
        catch { /* nav gates */ }
    }


    internal sealed class VoiceCaptureStartResult
    {
        public bool Ok { get; set; }
        public string? MimeType { get; set; }
        public string? Error { get; set; }
    }


    internal sealed class VoiceCaptureStopResult
    {
        public bool Ok { get; set; }
        public string? MimeType { get; set; }
        public string? Base64 { get; set; }
        public string? FileName { get; set; }
        public string? Error { get; set; }
        public long ByteLength { get; set; }
    }


    internal string CacheBust(string url) =>
        url + (url.Contains('?') ? "&" : "?") + "v=" + _imgBust;


    internal static string Trunc(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }


    public async ValueTask DisposeAsync()
    {
        _voiceSaveCts?.Cancel();
        _voiceSaveCts?.Dispose();
        _voiceSaveCts = null;
        _lookSaveCts?.Cancel();
        _lookSaveCts?.Dispose();
        _lookSaveCts = null;
        await DisposeAsyncCore();
    }


    /// <summary>Shared voice editor instance for simple-mode, cast list, and detail panel.</summary>
    internal RenderFragment VoiceEditorUI() => builder =>
    {
        builder.OpenComponent<Characters_VoiceEditor>(0);
        builder.AddAttribute(1, "SimpleMode", _simpleMode);
        builder.AddAttribute(2, "EditVoiceLabel", _editVoiceLabel);
        builder.AddAttribute(3, "VoiceLabelChanged", EventCallback.Factory.Create<string>(this, OnVoiceLabelChanged));
        builder.AddAttribute(4, "EditVoiceProfile", _editVoiceProfile);
        builder.AddAttribute(5, "VoiceProfileChanged", EventCallback.Factory.Create<string>(this, OnVoiceProfileChanged));
        builder.AddAttribute(6, "Busy", _busy);
        builder.AddAttribute(7, "VoicePreviewBusy", _voicePreviewBusy);
        builder.AddAttribute(8, "VoiceJobRunning", VoiceJobRunning);
        builder.AddAttribute(9, "VoiceSaveHint", _voiceSaveHint);
        builder.AddAttribute(10, "VoicePreviewError", _voicePreviewError);
        builder.AddAttribute(11, "VoicePreviewHint", _voicePreviewHint);
        builder.AddAttribute(12, "VoicePreviewStale", _voicePreviewStale);
        builder.AddAttribute(13, "VoicePreviewUrl", _voicePreviewUrl);
        builder.AddAttribute(14, "Job", _job);
        builder.AddAttribute(15, "Selected", _selected);
        builder.AddAttribute(16, "VoiceCloneBusy", _voiceCloneBusy);
        builder.AddAttribute(17, "VoiceCloneError", _voiceCloneError);
        builder.AddAttribute(18, "VoiceCloneHint", _voiceCloneHint);
        builder.AddAttribute(19, "VoiceClonePlayUrl", _voiceClonePlayUrl);
        builder.AddAttribute(20, "VoiceRecRecording", _voiceRecRecording);
        builder.AddAttribute(21, "ShowKidsFullScript", _showKidsFullScript);
        builder.AddAttribute(22, "ShowKidsFullScriptChanged", EventCallback.Factory.Create<bool>(this, v => _showKidsFullScript = v));
        builder.AddAttribute(23, "UseKidsScript", _useKidsScript);
        builder.AddAttribute(24, "UseKidsScriptChanged", EventCallback.Factory.Create<bool>(this, v => _useKidsScript = v));
        builder.AddAttribute(25, "ShowMediaAudioPicker", _showMediaAudioPicker);
        builder.AddAttribute(26, "ShowMediaAudioPickerChanged", EventCallback.Factory.Create<bool>(this, v => _showMediaAudioPicker = v));
        builder.AddAttribute(27, "LoadingMediaAudio", _loadingMediaAudio);
        builder.AddAttribute(28, "MediaAudioFiles", _mediaAudioFiles);
        builder.AddAttribute(29, "VoiceCloneReadScript", VoiceCloneReadScript);
        builder.AddAttribute(30, "OnPlayPreview", EventCallback.Factory.Create<bool>(this, PlayVoicePreviewAsync));
        builder.AddAttribute(31, "OnStartMic", EventCallback.Factory.Create(this, StartVoiceCloneMicAsync));
        builder.AddAttribute(32, "OnStopMic", EventCallback.Factory.Create(this, StopVoiceCloneMicAsync));
        builder.AddAttribute(33, "OnCancelMic", EventCallback.Factory.Create(this, CancelVoiceCloneMicAsync));
        builder.AddAttribute(34, "OnOpenMediaPicker", EventCallback.Factory.Create(this, OpenMediaFolderAudioPickerAsync));
        builder.AddAttribute(35, "OnApplyClone", EventCallback.Factory.Create(this, ApplyVoiceCloneToProviderAsync));
        builder.AddAttribute(36, "OnDeleteClone", EventCallback.Factory.Create(this, DeleteVoiceCloneSampleAsync));
        builder.AddAttribute(37, "OnPickMediaAudio", EventCallback.Factory.Create<ClientMediaFolderService.LocalAudioFile>(this, PickMediaFolderAudioAsync));
        builder.CloseComponent();
    };


    // ── Field forwarders (Host._x for markup children) ──
    internal List<CharacterSummary>? _chars
    {
        get => List._chars;
        set => List._chars = value;
    }
    internal bool _extractingCast
    {
        get => List._extractingCast;
        set => List._extractingCast = value;
    }
    internal string? _focusHint
    {
        get => List._focusHint;
        set => List._focusHint = value;
    }
    internal List<string>? _lastCastExtractKeys
    {
        get => List._lastCastExtractKeys;
        set => List._lastCastExtractKeys = value;
    }
    internal CharacterPlatesState? _plates
    {
        get => List._plates;
        set => List._plates = value;
    }
    internal bool _rebuildCastHadExisting
    {
        get => List._rebuildCastHadExisting;
        set => List._rebuildCastHadExisting = value;
    }
    internal CharacterSummary? _selected
    {
        get => List._selected;
        set => List._selected = value;
    }
    internal string? _selectedKey
    {
        get => List._selectedKey;
        set => List._selectedKey = value;
    }
    internal bool _simpleMode
    {
        get => List._simpleMode;
        set => List._simpleMode = value;
    }
    internal List<Candidate> _allCandidates
    {
        get => Look._allCandidates;
        set => Look._allCandidates = value;
    }
    internal string? _chosenCandidateKey
    {
        get => Look._chosenCandidateKey;
        set => Look._chosenCandidateKey = value;
    }
    internal PendingDelete? _deleteConfirm
    {
        get => Look._deleteConfirm;
        set => Look._deleteConfirm = value;
    }
    internal string _editDescription
    {
        get => Look._editDescription;
        set => Look._editDescription = value;
    }
    internal string _editVisualLock
    {
        get => Look._editVisualLock;
        set => Look._editVisualLock = value;
    }
    internal ImageSeedLimits? _imageSeedLimits
    {
        get => Look._imageSeedLimits;
        set => Look._imageSeedLimits = value;
    }
    internal long _imgBust
    {
        get => Look._imgBust;
        set => Look._imgBust = value;
    }
    internal bool _loadingBookCandidates
    {
        get => Look._loadingBookCandidates;
        set => Look._loadingBookCandidates = value;
    }
    internal CancellationTokenSource? _lookSaveCts
    {
        get => Look._lookSaveCts;
        set => Look._lookSaveCts = value;
    }
    internal string? _lookSaveHint
    {
        get => Look._lookSaveHint;
        set => Look._lookSaveHint = value;
    }
    internal Mode _mode
    {
        get => Look._mode;
        set => Look._mode = value;
    }
    internal bool _panelPictureOpen
    {
        get => Look._panelPictureOpen;
        set => Look._panelPictureOpen = value;
    }
    internal Candidate? _pendingLockCandidate
    {
        get => Look._pendingLockCandidate;
        set => Look._pendingLockCandidate = value;
    }
    internal PictureRoute _pictureRoute
    {
        get => Look._pictureRoute;
        set => Look._pictureRoute = value;
    }
    internal List<RankedBookCandidateDto>? _rankedBookCandidates
    {
        get => Look._rankedBookCandidates;
        set => Look._rankedBookCandidates = value;
    }
    internal string _savedLookDescription
    {
        get => Look._savedLookDescription;
        set => Look._savedLookDescription = value;
    }
    internal string _savedLookVisualLock
    {
        get => Look._savedLookVisualLock;
        set => Look._savedLookVisualLock = value;
    }
    internal bool _savingBookRefs
    {
        get => Look._savingBookRefs;
        set => Look._savingBookRefs = value;
    }
    internal bool _savingLook
    {
        get => Look._savingLook;
        set => Look._savingLook = value;
    }
    internal List<string> _seedOrder => Look._seedOrder;
    internal List<string> _selectedBookCandidatePaths => Look._selectedBookCandidatePaths;
    internal bool _showBookCandidateGallery
    {
        get => Look._showBookCandidateGallery;
        set => Look._showBookCandidateGallery = value;
    }
    internal Candidate? _styleRejectCandidate
    {
        get => Look._styleRejectCandidate;
        set => Look._styleRejectCandidate = value;
    }
    internal string? _styleRejectMessage
    {
        get => Look._styleRejectMessage;
        set => Look._styleRejectMessage = value;
    }
    internal Candidate? _zoomCandidate
    {
        get => Look._zoomCandidate;
        set => Look._zoomCandidate = value;
    }
    internal double _zoomScale
    {
        get => Look._zoomScale;
        set => Look._zoomScale = value;
    }
    internal string _editVoiceLabel
    {
        get => Voice._editVoiceLabel;
        set => Voice._editVoiceLabel = value;
    }
    internal string _editVoiceProfile
    {
        get => Voice._editVoiceProfile;
        set => Voice._editVoiceProfile = value;
    }
    internal bool _forceShowVoice
    {
        get => Voice._forceShowVoice;
        set => Voice._forceShowVoice = value;
    }
    internal bool _loadingMediaAudio
    {
        get => Voice._loadingMediaAudio;
        set => Voice._loadingMediaAudio = value;
    }
    internal List<ClientMediaFolderService.LocalAudioFile> _mediaAudioFiles
    {
        get => Voice._mediaAudioFiles;
        set => Voice._mediaAudioFiles = value;
    }
    internal bool _showKidsFullScript
    {
        get => Voice._showKidsFullScript;
        set => Voice._showKidsFullScript = value;
    }
    internal bool _showMediaAudioPicker
    {
        get => Voice._showMediaAudioPicker;
        set => Voice._showMediaAudioPicker = value;
    }
    internal bool _useKidsScript
    {
        get => Voice._useKidsScript;
        set => Voice._useKidsScript = value;
    }
    internal long _voiceAudioBust
    {
        get => Voice._voiceAudioBust;
        set => Voice._voiceAudioBust = value;
    }
    internal long _voiceCloneBust
    {
        get => Voice._voiceCloneBust;
        set => Voice._voiceCloneBust = value;
    }
    internal bool _voiceCloneBusy
    {
        get => Voice._voiceCloneBusy;
        set => Voice._voiceCloneBusy = value;
    }
    internal string? _voiceCloneError
    {
        get => Voice._voiceCloneError;
        set => Voice._voiceCloneError = value;
    }
    internal string? _voiceCloneHint
    {
        get => Voice._voiceCloneHint;
        set => Voice._voiceCloneHint = value;
    }
    internal string? _voiceClonePlayUrl
    {
        get => Voice._voiceClonePlayUrl;
        set => Voice._voiceClonePlayUrl = value;
    }
    internal bool _voicePreviewBusy
    {
        get => Voice._voicePreviewBusy;
        set => Voice._voicePreviewBusy = value;
    }
    internal string? _voicePreviewError
    {
        get => Voice._voicePreviewError;
        set => Voice._voicePreviewError = value;
    }
    internal string? _voicePreviewHint
    {
        get => Voice._voicePreviewHint;
        set => Voice._voicePreviewHint = value;
    }
    internal bool _voicePreviewStale
    {
        get => Voice._voicePreviewStale;
        set => Voice._voicePreviewStale = value;
    }
    internal string? _voicePreviewUrl
    {
        get => Voice._voicePreviewUrl;
        set => Voice._voicePreviewUrl = value;
    }
    internal bool _voiceRecRecording
    {
        get => Voice._voiceRecRecording;
        set => Voice._voiceRecRecording = value;
    }
    internal CancellationTokenSource? _voiceSaveCts
    {
        get => Voice._voiceSaveCts;
        set => Voice._voiceSaveCts = value;
    }
    internal string? _voiceSaveHint
    {
        get => Voice._voiceSaveHint;
        set => Voice._voiceSaveHint = value;
    }
    internal JobSnapshot? _job
    {
        get => Jobs._job;
        set => Jobs._job = value;
    }
}
