using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using LocalAudioFile = PageToMovie.Web.Services.ClientMediaFolderService.LocalAudioFile;

namespace PageToMovie.Web.Components.Pages;

public partial class Characters_VoiceEditor
{
    [Parameter] public bool SimpleMode { get; set; }
    [Parameter] public string EditVoiceLabel { get; set; } = "";
    [Parameter] public EventCallback<string> VoiceLabelChanged { get; set; }
    [Parameter] public string EditVoiceProfile { get; set; } = "";
    [Parameter] public EventCallback<string> VoiceProfileChanged { get; set; }
    [Parameter] public string? EditImagineVoiceId { get; set; }
    [Parameter] public EventCallback<string?> ImagineVoiceIdChanged { get; set; }
    [Parameter] public IReadOnlyList<PresetVoiceEntry> PresetVoices { get; set; } = Array.Empty<PresetVoiceEntry>();
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool VoicePreviewBusy { get; set; }
    [Parameter] public bool VoiceJobRunning { get; set; }
    [Parameter] public string? VoiceSaveHint { get; set; }
    [Parameter] public string? VoicePreviewError { get; set; }
    [Parameter] public string? VoicePreviewHint { get; set; }
    [Parameter] public bool VoicePreviewStale { get; set; }
    [Parameter] public string? VoicePreviewUrl { get; set; }
    [Parameter] public JobSnapshot? Job { get; set; }
    [Parameter] public CharacterSummary? Selected { get; set; }
    [Parameter] public bool VoiceCloneBusy { get; set; }
    [Parameter] public string? VoiceCloneError { get; set; }
    [Parameter] public string? VoiceCloneHint { get; set; }
    [Parameter] public string? VoiceClonePlayUrl { get; set; }
    [Parameter] public bool VoiceRecRecording { get; set; }
    [Parameter] public bool ShowKidsFullScript { get; set; }
    [Parameter] public EventCallback<bool> ShowKidsFullScriptChanged { get; set; }
    [Parameter] public bool UseKidsScript { get; set; }
    [Parameter] public EventCallback<bool> UseKidsScriptChanged { get; set; }
    [Parameter] public bool ShowMediaAudioPicker { get; set; }
    [Parameter] public EventCallback<bool> ShowMediaAudioPickerChanged { get; set; }
    [Parameter] public bool LoadingMediaAudio { get; set; }
    [Parameter] public IReadOnlyList<LocalAudioFile>? MediaAudioFiles { get; set; }
    [Parameter] public string VoiceCloneReadScript { get; set; } = "";
    [Parameter] public EventCallback<bool> OnPlayPreview { get; set; }
    [Parameter] public EventCallback OnStartMic { get; set; }
    [Parameter] public EventCallback OnStopMic { get; set; }
    [Parameter] public EventCallback OnCancelMic { get; set; }
    [Parameter] public EventCallback OnOpenMediaPicker { get; set; }
    [Parameter] public EventCallback OnApplyClone { get; set; }
    [Parameter] public EventCallback OnDeleteClone { get; set; }
    [Parameter] public EventCallback<LocalAudioFile> OnPickMediaAudio { get; set; }

    private async Task ToggleKidsScript() =>
        await ShowKidsFullScriptChanged.InvokeAsync(!ShowKidsFullScript);

    private async Task CloseMediaPicker() =>
        await ShowMediaAudioPickerChanged.InvokeAsync(false);
}
