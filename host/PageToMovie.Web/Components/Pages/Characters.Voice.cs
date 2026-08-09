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

// Forwarders: CharactersVoice → Host.*
public partial class Characters
{
    internal static bool HasVoiceProfile(CharacterSummary c) => CharactersVoice.HasVoiceProfile(c);

    internal bool ShowVoiceFields(CharacterSummary c) => Voice.ShowVoiceFields(c);

    internal Task SaveVoiceAsync(bool silent = false) => Voice.SaveVoiceAsync(silent);

    internal void OnVoiceLabelInput(ChangeEventArgs e) => Voice.OnVoiceLabelInput(e);

    internal void OnVoiceProfileInput(ChangeEventArgs e) => Voice.OnVoiceProfileInput(e);

    internal Task OnVoiceLabelChanged(string value) => Voice.OnVoiceLabelChanged(value);

    internal Task OnVoiceProfileChanged(string value) => Voice.OnVoiceProfileChanged(value);

    internal void ScheduleAutoSaveVoice() => Voice.ScheduleAutoSaveVoice();

    internal Task AutoSaveVoiceDebouncedAsync(CancellationToken token) => Voice.AutoSaveVoiceDebouncedAsync(token);

    internal void MarkVoiceStaleIfPlaying() => Voice.MarkVoiceStaleIfPlaying();

    internal Task TryLoadCachedVoiceAsync() => Voice.TryLoadCachedVoiceAsync();

    internal Task PlayVoicePreviewAsync(bool force) => Voice.PlayVoicePreviewAsync(force);

    internal void RefreshVoiceClonePlayUrl() => Voice.RefreshVoiceClonePlayUrl();

    internal Task ApplyVoiceCloneToProviderAsync() => Voice.ApplyVoiceCloneToProviderAsync();

    internal Task EnsureSimpleVoiceModelAsync() => Voice.EnsureSimpleVoiceModelAsync();

    internal Task StartVoiceCloneMicAsync() => Voice.StartVoiceCloneMicAsync();

    internal Task StopVoiceCloneMicAsync() => Voice.StopVoiceCloneMicAsync();

    internal Task CancelVoiceCloneMicAsync() => Voice.CancelVoiceCloneMicAsync();

    internal Task OpenMediaFolderAudioPickerAsync() => Voice.OpenMediaFolderAudioPickerAsync();

    internal Task PickMediaFolderAudioAsync(ClientMediaFolderService.LocalAudioFile file) => Voice.PickMediaFolderAudioAsync(file);

    internal Task PersistVoiceCloneSampleAsync(byte[] bytes, string fileName) => Voice.PersistVoiceCloneSampleAsync(bytes, fileName);

    internal Task OnVoiceCloneUploadAsync(InputFileChangeEventArgs e) => Voice.OnVoiceCloneUploadAsync(e);

    internal Task DeleteVoiceCloneSampleAsync() => Voice.DeleteVoiceCloneSampleAsync();


    internal string VoiceCloneReadScript => Voice.VoiceCloneReadScript;
}
