using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Characters : IAsyncDisposable, IPageSliceHost
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private CharactersListState? _list;
    internal CharactersListState List => _list ??= new CharactersListState(this);
    private CharactersLookBook? _lookBook;
    internal CharactersLookBook LookBook => _lookBook ??= new CharactersLookBook(this);
    private CharactersLookPipeline? _lookPipe;
    internal CharactersLookPipeline LookPipe => _lookPipe ??= new CharactersLookPipeline(this);
    private CharactersLookEditors? _lookEdit;
    internal CharactersLookEditors LookEdit => _lookEdit ??= new CharactersLookEditors(this);
    private CharactersVoice? _voice;
    internal CharactersVoice Voice => _voice ??= new CharactersVoice(this);
    private CharactersJobs? _jobs;
    internal CharactersJobs Jobs => _jobs ??= new CharactersJobs(this);

    internal void EnsureDomains()
    {
        _ = List; _ = LookBook; _ = LookPipe; _ = LookEdit; _ = Voice; _ = Jobs;
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

    /// <summary>True when the Easy Start catalog has a timing-complete title (same forkable list).</summary>
    internal bool _easyStartAvailable;

    internal const int LookAutosaveDebounceMs = 800;


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        await ActiveProject.EnsureLoadedAsync(Engine);
        Hub.JobUpdated += Jobs.OnJobUpdated;
        Hub.JobLog += Jobs.OnJobLog;
        try
        {
            var loaded = await StudioPageBootstrap.LoadActiveProjectAsync(
                Engine, Session, ActiveProject, Caps, () => _gateChecked = true);
            _projectId = loaded.ProjectId;
            _projectIds = loaded.ProjectIds;

            List.ApplySimpleModeFromUri();
            // Easy-start lives entirely on /simple-voice (story + record). No cast list.
            if (List._simpleMode)
            {
                Nav.NavigateTo("simple-voice");
                return;
            }

            try { _easyStartAvailable = await Engine.HasEasyStartStoriesAsync(); }
            catch { _easyStartAvailable = false; }


            if (string.IsNullOrEmpty(_projectId) || !ActiveProject.CanCharacters)
                return;

            try { await Hub.StartAsync(); } catch { /* optional */ }

            var jobs = await Engine.GetJobAsync();
            Jobs._job = jobs?.Job;

            await List.LoadAsync();
            await List.TrySelectFromQueryAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }


    /// <summary>Slice host (see <see cref="IPageSliceHost"/>): CastList and LookPanel are slices.</summary>
    public event Action? Rendered;

    public void RenderRequestedBySlice() => StateHasChanged();

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        Rendered?.Invoke();
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
        url + (url.Contains('?') ? "&" : "?") + "v=" + LookPipe._imgBust;


    internal static string Trunc(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }


    public async ValueTask DisposeAsync()
    {
        if (Voice._voiceSaveCts is { } vCts)
        {
            await vCts.CancelAsync();
            vCts.Dispose();
        }
        Voice._voiceSaveCts = null;
        if (LookEdit._lookSaveCts is { } lCts)
        {
            await lCts.CancelAsync();
            lCts.Dispose();
        }
        LookEdit._lookSaveCts = null;
        await Jobs.DisposeAsyncCore();
    }


    /// <summary>Shared voice editor instance for simple-mode, cast list, and detail panel.</summary>
    internal RenderFragment VoiceEditorUI() => builder =>
    {
        builder.OpenComponent<Characters_VoiceEditor>(0);
        builder.AddAttribute(1, "SimpleMode", List._simpleMode);
        builder.AddAttribute(2, "EditVoiceLabel", Voice._editVoiceLabel);
        builder.AddAttribute(3, "VoiceLabelChanged", EventCallback.Factory.Create<string>(this, Voice.OnVoiceLabelChanged));
        builder.AddAttribute(4, "EditVoiceProfile", Voice._editVoiceProfile);
        builder.AddAttribute(5, "VoiceProfileChanged", EventCallback.Factory.Create<string>(this, Voice.OnVoiceProfileChanged));
        builder.AddAttribute(38, "EditImagineVoiceId", Voice._editImagineVoiceId);
        builder.AddAttribute(39, "ImagineVoiceIdChanged", EventCallback.Factory.Create<string?>(this, Voice.OnImagineVoiceChanged));
        builder.AddAttribute(40, "PresetVoices", Voice._presetVoices);
        builder.AddAttribute(6, "Busy", _busy);
        builder.AddAttribute(7, "VoicePreviewBusy", Voice._voicePreviewBusy);
        builder.AddAttribute(8, "VoiceJobRunning", Jobs.VoiceJobRunning);
        builder.AddAttribute(9, "VoiceSaveHint", Voice._voiceSaveHint);
        builder.AddAttribute(10, "VoicePreviewError", Voice._voicePreviewError);
        builder.AddAttribute(11, "VoicePreviewHint", Voice._voicePreviewHint);
        builder.AddAttribute(12, "VoicePreviewStale", Voice._voicePreviewStale);
        builder.AddAttribute(13, "VoicePreviewUrl", Voice._voicePreviewUrl);
        builder.AddAttribute(14, "Job", Jobs._job);
        builder.AddAttribute(15, "Selected", List._selected);
        builder.AddAttribute(16, "VoiceCloneBusy", Voice._voiceCloneBusy);
        builder.AddAttribute(17, "VoiceCloneError", Voice._voiceCloneError);
        builder.AddAttribute(18, "VoiceCloneHint", Voice._voiceCloneHint);
        builder.AddAttribute(19, "VoiceClonePlayUrl", Voice._voiceClonePlayUrl);
        builder.AddAttribute(20, "VoiceRecRecording", Voice._voiceRecRecording);
        builder.AddAttribute(21, "ShowKidsFullScript", Voice._showKidsFullScript);
        builder.AddAttribute(22, "ShowKidsFullScriptChanged", EventCallback.Factory.Create<bool>(this, v => Voice._showKidsFullScript = v));
        builder.AddAttribute(23, "UseKidsScript", Voice._useKidsScript);
        builder.AddAttribute(24, "UseKidsScriptChanged", EventCallback.Factory.Create<bool>(this, v => Voice._useKidsScript = v));
        builder.AddAttribute(25, "ShowMediaAudioPicker", Voice._showMediaAudioPicker);
        builder.AddAttribute(26, "ShowMediaAudioPickerChanged", EventCallback.Factory.Create<bool>(this, v => Voice._showMediaAudioPicker = v));
        builder.AddAttribute(27, "LoadingMediaAudio", Voice._loadingMediaAudio);
        builder.AddAttribute(28, "MediaAudioFiles", Voice._mediaAudioFiles);
        builder.AddAttribute(29, "VoiceCloneReadScript", Voice.VoiceCloneReadScript);
        builder.AddAttribute(30, "OnPlayPreview", EventCallback.Factory.Create<bool>(this, Voice.PlayVoicePreviewAsync));
        builder.AddAttribute(31, "OnStartMic", EventCallback.Factory.Create(this, Voice.StartVoiceCloneMicAsync));
        builder.AddAttribute(32, "OnStopMic", EventCallback.Factory.Create(this, Voice.StopVoiceCloneMicAsync));
        builder.AddAttribute(33, "OnCancelMic", EventCallback.Factory.Create(this, Voice.CancelVoiceCloneMicAsync));
        builder.AddAttribute(34, "OnOpenMediaPicker", EventCallback.Factory.Create(this, Voice.OpenMediaFolderAudioPickerAsync));
        builder.AddAttribute(35, "OnApplyClone", EventCallback.Factory.Create(this, Voice.ApplyVoiceCloneToProviderAsync));
        builder.AddAttribute(36, "OnDeleteClone", EventCallback.Factory.Create(this, Voice.DeleteVoiceCloneSampleAsync));
        builder.AddAttribute(37, "OnPickMediaAudio", EventCallback.Factory.Create<ClientMediaFolderService.LocalAudioFile>(this, Voice.PickMediaFolderAudioAsync));
        builder.CloseComponent();
    };
}
