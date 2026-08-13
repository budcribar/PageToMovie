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
    /// <summary>Voice domain for the Characters page. Owns related UI state and behavior.</summary>
    public sealed class CharactersVoice
    {
        private readonly Characters S;
        public CharactersVoice(Characters host) => S = host;

        internal string _editVoiceLabel = "";

        internal string _editVoiceProfile = "";

        internal bool _forceShowVoice;

        internal bool _loadingMediaAudio;

        internal List<ClientMediaFolderService.LocalAudioFile> _mediaAudioFiles = new();

        internal bool _showKidsFullScript;

        internal bool _showMediaAudioPicker;

        internal bool _useKidsScript;

        internal long _voiceAudioBust;

        internal long _voiceCloneBust;

        internal bool _voiceCloneBusy;

        internal string? _voiceCloneError;

        internal string? _voiceCloneHint;

        internal string? _voiceClonePlayUrl;

        internal bool _voicePreviewBusy;

        internal string? _voicePreviewError;

        internal string? _voicePreviewHint;

        internal bool _voicePreviewStale;

        internal string? _voicePreviewUrl;

        internal bool _voiceRecRecording;

        internal CancellationTokenSource? _voiceSaveCts;

        internal string? _voiceSaveHint;


        internal static bool HasVoiceProfile(CharacterSummary c) =>
            !string.IsNullOrWhiteSpace(c.VoiceProfile);


        internal bool ShowVoiceFields(CharacterSummary c)
        {
            if (_forceShowVoice) return true;               // explicit "Add voice…" opt-in
            if (c.HasVoiceCloneSample) return true;         // user recorded a clone sample
            // A SILENT non-human (background animal, e.g. a lamb) gets no voice prompt — and a cast-extraction
            // auto-fill (e.g. "soft lamb bleat") must not force it to show. A TALKING animal speaks, so it gets a
            // voice like any speaker and falls through. Keyed on whether the character speaks, not species alone.
            var species = c.SpeciesKind;
            var isNonHuman = !string.IsNullOrWhiteSpace(species)
                && !species.Trim().Equals("human", StringComparison.OrdinalIgnoreCase);
            if (isNonHuman && !c.Speaks) return false;
            if (c.Speaks) return true;                      // any speaking role offers a voice
            if (HasVoiceProfile(c)) return true;
            if (!string.IsNullOrWhiteSpace(c.VoiceLabel)) return true;
            if (!string.IsNullOrWhiteSpace(_editVoiceProfile) || !string.IsNullOrWhiteSpace(_editVoiceLabel))
                return true;
            return false;
        }


        /// <summary>Kids short script for simple path / children's books; general film otherwise.</summary>
        internal string VoiceCloneReadScript =>
            _useKidsScript ? VoiceCloneScripts.KidsShort : VoiceCloneScripts.GeneralFilm;


        internal async Task SaveVoiceAsync(bool silent = false)
        {
            if (S.List._selected is null) return;
            if (!silent)
            {
                S._busy = true;
                S._error = null;
            }
            try
            {
                await S.Engine.UpdateCharacterVoiceAsync(
                    S._projectId,
                    S.List._selected.Key,
                    voiceProfile: _editVoiceProfile,
                    voiceLabel: _editVoiceLabel);
                if (!silent)
                    S._message = $"Saved voice for {S.List._selected.DisplayName}";
                await S.List.SoftReloadAsync();
                if (S.List._selected is not null)
                {
                    _editVoiceLabel = S.List._selected.VoiceLabel ?? "";
                    _editVoiceProfile = S.List._selected.VoiceProfile ?? "";
                }
                try { await S.ActiveProject.RefreshReadinessAsync(S.Engine); } catch { /* nav */ }
                if (S.List.IsCastComplete && !silent)
                    S._message = null;
            }
            catch (Exception ex)
            {
                if (!silent) S._error = ex.Message;
                else throw;
            }
            finally
            {
                if (!silent) S._busy = false;
            }
        }


        internal void OnVoiceLabelInput(ChangeEventArgs e)
        {
            _editVoiceLabel = e.Value?.ToString() ?? "";
            MarkVoiceStaleIfPlaying();
            ScheduleAutoSaveVoice();
        }


        internal void OnVoiceProfileInput(ChangeEventArgs e)
        {
            _editVoiceProfile = e.Value?.ToString() ?? "";
            MarkVoiceStaleIfPlaying();
            ScheduleAutoSaveVoice();
        }


        internal Task OnVoiceLabelChanged(string value)
        {
            _editVoiceLabel = value ?? "";
            MarkVoiceStaleIfPlaying();
            ScheduleAutoSaveVoice();
            return Task.CompletedTask;
        }


        internal Task OnVoiceProfileChanged(string value)
        {
            _editVoiceProfile = value ?? "";
            MarkVoiceStaleIfPlaying();
            ScheduleAutoSaveVoice();
            return Task.CompletedTask;
        }


        internal void ScheduleAutoSaveVoice()
        {
            _voiceSaveCts?.Cancel();
            _voiceSaveCts?.Dispose();
            S.LookEdit._lookSaveCts?.Cancel();
            S.LookEdit._lookSaveCts?.Dispose();
            _voiceSaveCts = new CancellationTokenSource();
            var token = _voiceSaveCts.Token;
            _ = AutoSaveVoiceDebouncedAsync(token);
        }


        internal async Task AutoSaveVoiceDebouncedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(700, token);
                if (token.IsCancellationRequested || S.List._selected is null) return;
                _voiceSaveHint = "Saving…";
                await S.InvokeAsync(S.StateHasChanged);
                await SaveVoiceAsync(silent: true);
                if (!token.IsCancellationRequested)
                {
                    _voiceSaveHint = "Saved";
                    await S.InvokeAsync(S.StateHasChanged);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _voiceSaveHint = "Save failed";
                S._error = ex.Message;
                await S.InvokeAsync(S.StateHasChanged);
            }
        }


        internal void MarkVoiceStaleIfPlaying()
        {
            if (!string.IsNullOrEmpty(_voicePreviewUrl))
                _voicePreviewStale = true;
        }


        internal async Task TryLoadCachedVoiceAsync()
        {
            if (S.List._selected is null) return;
            try
            {
                var st = await S.Engine.GetVoicePreviewStatusAsync(
                    S._projectId,
                    S.List._selected.Key,
                    voiceProfile: _editVoiceProfile,
                    voiceLabel: _editVoiceLabel);
                if (st is { Exists: true, Matches: true })
                {
                    _voiceAudioBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _voicePreviewUrl = S.Engine.CharacterVoiceAudioUrl(
                        S._projectId, S.List._selected.Key, _voiceAudioBust);
                    _voicePreviewStale = false;
                    _voicePreviewHint = "Cached film voice sample.";
                }
                else if (st is { Exists: true, Matches: false })
                {
                    _voicePreviewStale = true;
                    _voicePreviewHint = "Saved sample is out of date — Regenerate after edits.";
                }
                S.StateHasChanged();
            }
            catch { /* optional */ }
        }


        /// <param name="force">true = always regenerate (after editing profile).</param>
        internal async Task PlayVoicePreviewAsync(bool force)
        {
            if (S.List._selected is null) return;
            _voicePreviewError = null;
            _voicePreviewHint = null;
            S.StateHasChanged();

            try
            {
                if (!force)
                {
                    var st = await S.Engine.GetVoicePreviewStatusAsync(
                        S._projectId,
                        S.List._selected.Key,
                        voiceProfile: _editVoiceProfile,
                        voiceLabel: _editVoiceLabel);
                    if (st is { Exists: true, Matches: true })
                    {
                        _voiceAudioBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _voicePreviewUrl = S.Engine.CharacterVoiceAudioUrl(
                            S._projectId, S.List._selected.Key, _voiceAudioBust);
                        _voicePreviewStale = false;
                        _voicePreviewHint = "Cached film voice sample.";
                        return;
                    }
                }

                _voicePreviewBusy = true;
                _voicePreviewUrl = null;
                _voicePreviewHint = force
                    ? "Regenerating film voice sample…"
                    : "Generating film voice sample (short clip)…";
                S.StateHasChanged();

                await S.Engine.StartVoicePreviewAsync(new StartVoicePreviewRequest
                {
                    ProjectId = S._projectId,
                    CharKey = S.List._selected.Key,
                    VoiceProfile = _editVoiceProfile,
                    VoiceLabel = _editVoiceLabel,
                    DisplayName = S.List._selected.DisplayName,
                    // force: always regen; cache miss also generates (service skips only matching cache)
                    Force = force,
                });
                // Job progress via SignalR; keep busy until done handler clears it
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs._job = jobs?.Job;
            }
            catch (Exception ex)
            {
                _voicePreviewError = ex.Message;
                _voicePreviewBusy = false;
            }
        }


        internal void RefreshVoiceClonePlayUrl()
        {
            if (S.List._selected?.HasVoiceCloneSample == true && !string.IsNullOrEmpty(S._projectId) && !string.IsNullOrEmpty(S.List._selectedKey))
            {
                _voiceCloneBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _voiceClonePlayUrl = S.Engine.CharacterVoiceCloneSampleUrl(S._projectId, S.List._selectedKey, _voiceCloneBust);
            }
            else
                _voiceClonePlayUrl = null;
        }



        /// <summary>
        /// Simple path: pick Instant voice clone model so apply-ready status is clear once the key is set.
        /// Does not block recording if the key is still missing.
        /// </summary>

        internal async Task ApplyVoiceCloneToProviderAsync()
        {
            if (S.List._selected is null || string.IsNullOrEmpty(S.List._selectedKey) || string.IsNullOrEmpty(S._projectId))
                return;
            _voiceCloneBusy = true;
            _voiceCloneError = null;
            _voiceCloneHint = "Applying voice…";
            try
            {
                var result = await S.Engine.ApplyVoiceCloneAsync(S._projectId, S.List._selectedKey);
                if (!result.Ok)
                {
                    _voiceCloneError = result.Error ?? "Apply failed";
                    return;
                }
                _voiceCloneHint = result.Message
                    ?? (result.UsedMock
                        ? "Demo voice applied. Preview saved."
                        : $"Voice applied ({result.ProviderId ?? "provider"}) — id saved on this character.");
                await S.List.LoadAsync();
                if (!string.IsNullOrEmpty(S.List._selectedKey))
                    await S.List.SelectCoreAsync(S.List._selectedKey, resetMode: false, flushPending: false);
            }
            catch (Exception ex)
            {
                _voiceCloneError = ex.Message;
            }
            finally
            {
                _voiceCloneBusy = false;
            }
        }


        internal async Task EnsureSimpleVoiceModelAsync()
        {
            try
            {
                var cfgDto = await S.Engine.GetConfigAsync(S._projectId);
                var map = cfgDto?.Config;
                string voice = "none";
                if (map is not null && map.TryGetValue("voice_model_name", out var el)
                    && el.ValueKind == System.Text.Json.JsonValueKind.String)
                    voice = el.GetString() ?? "none";

                if (!string.IsNullOrWhiteSpace(voice)
                    && !voice.Equals("none", StringComparison.OrdinalIgnoreCase)
                    && !voice.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    await S.Caps.RefreshAsync(S.Engine);
                    return;
                }

                // First clone-step model from the catalog only (never invent an id).
                var defaultClone = SupportedModelCatalog.FirstEnabledVoiceCloneModelId();
                if (string.IsNullOrWhiteSpace(defaultClone))
                    return;
                await S.Engine.SaveConfigAsync(S._projectId, new Dictionary<string, object?>
                {
                    ["voice_model_name"] = defaultClone,
                });
                await S.Caps.RefreshAsync(S.Engine);
            }
            catch
            {
                // Non-fatal — recording still works without a selected model
            }
        }


        internal async Task StartVoiceCloneMicAsync()
        {
            _voiceCloneError = null;
            _voiceCloneHint = null;
            try
            {
                // Prefer already-authorized media folder; never open a picker just to start recording.
                if (!S.MediaFolder.IsConnected)
                    await S.MediaFolder.TryReconnectAsync();

                var result = await S.Js.InvokeAsync<VoiceCaptureStartResult>("PageToMovieVoiceCapture.start");
                if (result is null || !result.Ok)
                {
                    _voiceCloneError = string.IsNullOrWhiteSpace(result?.Error)
                        ? "Could not access the microphone. Check browser permissions and try again."
                        : result.Error;
                    return;
                }
                _voiceRecRecording = true;
                _voiceCloneHint = "Recording — read the script, then tap Done.";
                await S.InvokeAsync(S.StateHasChanged);
            }
            catch (JSException jex)
            {
                _voiceCloneError = "Microphone failed: " + jex.Message;
                _voiceRecRecording = false;
            }
            catch (Exception ex)
            {
                _voiceCloneError = ex.Message;
                _voiceRecRecording = false;
            }
        }


        internal async Task StopVoiceCloneMicAsync()
        {
            if (S.List._selected is null || string.IsNullOrEmpty(S.List._selectedKey)) return;
            _voiceCloneBusy = true;
            _voiceCloneError = null;
            _voiceCloneHint = "Saving…";
            S.StateHasChanged();
            try
            {
                var result = await S.Js.InvokeAsync<VoiceCaptureStopResult>("PageToMovieVoiceCapture.stop");
                _voiceRecRecording = false;
                if (result is null || !result.Ok || string.IsNullOrEmpty(result.Base64))
                {
                    _voiceCloneError = result?.Error ?? "No audio captured";
                    return;
                }
                var raw = Convert.FromBase64String(result.Base64);
                var name = string.IsNullOrWhiteSpace(result.FileName) ? "voice_clone_sample.webm" : result.FileName;
                await PersistVoiceCloneSampleAsync(raw, name);
            }
            catch (Exception ex)
            {
                _voiceCloneError = ex.Message;
                _voiceRecRecording = false;
            }
            finally
            {
                _voiceCloneBusy = false;
                S.StateHasChanged();
            }
        }


        internal async Task CancelVoiceCloneMicAsync()
        {
            try { await S.Js.InvokeVoidAsync("PageToMovieVoiceCapture.cancel"); } catch { }
            _voiceRecRecording = false;
            _voiceCloneHint = "Recording cancelled.";
        }


        internal async Task OpenMediaFolderAudioPickerAsync()
        {
            _voiceCloneError = null;
            _showMediaAudioPicker = true;
            _loadingMediaAudio = true;
            S.StateHasChanged();
            try
            {
                if (!S.MediaFolder.IsConnected)
                {
                    var ok = await S.MediaFolder.ConnectFolderAsync();
                    if (!ok)
                    {
                        _voiceCloneError = S.MediaFolder.LastStatus ?? "Connect a folder first to pick existing audio.";
                        _showMediaAudioPicker = false;
                        return;
                    }
                }
                var files = await S.MediaFolder.ListAudioFilesAsync(S._projectId);
                // Prefer project-scoped files; if empty, list whole folder
                if (files.Count == 0)
                    files = await S.MediaFolder.ListAudioFilesAsync(null);
                _mediaAudioFiles = files.ToList();
                _voiceCloneHint = _mediaAudioFiles.Count > 0
                    ? $"Found {_mediaAudioFiles.Count} file(s) — choose one."
                    : "No audio files found yet.";
            }
            catch (Exception ex)
            {
                _voiceCloneError = ex.Message;
            }
            finally
            {
                _loadingMediaAudio = false;
                S.StateHasChanged();
            }
        }


        internal async Task PickMediaFolderAudioAsync(ClientMediaFolderService.LocalAudioFile file)
        {
            if (S.List._selected is null || string.IsNullOrEmpty(S.List._selectedKey)) return;
            _voiceCloneBusy = true;
            _voiceCloneError = null;
            _voiceCloneHint = $"Loading {file.Name}…";
            S.StateHasChanged();
            try
            {
                var bytes = await S.MediaFolder.ReadLocalBytesAsync(file.RelativePath);
                if (bytes is null || bytes.Length == 0)
                {
                    _voiceCloneError = "Could not read that file.";
                    return;
                }
                await PersistVoiceCloneSampleAsync(bytes, file.Name);
                _showMediaAudioPicker = false;
            }
            catch (Exception ex)
            {
                _voiceCloneError = ex.Message;
            }
            finally
            {
                _voiceCloneBusy = false;
                S.StateHasChanged();
            }
        }


        /// <summary>
        /// Write clone sample to the client media folder (source of truth for large media) and
        /// mirror metadata/bytes to the server so previews still work.
        /// </summary>
        internal async Task PersistVoiceCloneSampleAsync(byte[] bytes, string fileName)
        {
            if (S.List._selected is null || string.IsNullOrEmpty(S.List._selectedKey)) return;
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext is not (".webm" or ".mp3" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".mp4"))
                ext = ".webm";
            if (ext == ".mp4") ext = ".webm";
            var safeKey = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeFileName(S.List._selectedKey ?? "character");
            var rel = $"assets/characters/{safeKey}/voice_clone_sample{ext}";

            // Client media folder when already connected (no folder picker mid-recording).
            // Always mirror to the server so previews still work.
            if (!S.MediaFolder.IsConnected)
                await S.MediaFolder.TryReconnectAsync();

            await S.MediaFolder.SaveBytesAsync(
                S._projectId, rel, bytes, promptToConnectFolder: false);
            _voiceCloneHint = "Saving…";

            await using var ms = new MemoryStream(bytes);
            var selectedKey = S.List._selectedKey;
            if (string.IsNullOrWhiteSpace(selectedKey))
                throw new InvalidOperationException("No character selected for voice sample.");
            await S.Engine.UploadVoiceCloneSampleAsync(S._projectId, selectedKey, ms, "voice_clone_sample" + ext, _voiceSaveCts?.Token ?? CancellationToken.None);
            await S.List.SoftReloadAsync();
            RefreshVoiceClonePlayUrl();
            _voiceCloneBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RefreshVoiceClonePlayUrl();
            _voiceCloneHint = "Sample saved.";
        }


        internal async Task OnVoiceCloneUploadAsync(InputFileChangeEventArgs e)
        {
            // Legacy OS file picker path — prefer media folder; still supported if invoked.
            if (S.List._selected is null || S.List._selected.VoiceOnly || S.List._selected.IsGroup) return;
            var file = e.File;
            if (file is null) return;
            _voiceCloneBusy = true;
            _voiceCloneError = null;
            try
            {
                const long max = 15 * 1024 * 1024;
                await using var stream = file.OpenReadStream(max, cancellationToken: _voiceSaveCts?.Token ?? CancellationToken.None);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, _voiceSaveCts?.Token ?? CancellationToken.None);
                await PersistVoiceCloneSampleAsync(ms.ToArray(), file.Name);
            }
            catch (Exception ex) { _voiceCloneError = ex.Message; }
            finally { _voiceCloneBusy = false; }
        }


        internal async Task DeleteVoiceCloneSampleAsync()
        {
            if (string.IsNullOrEmpty(S._projectId) || string.IsNullOrEmpty(S.List._selectedKey)) return;
            _voiceCloneBusy = true;
            try
            {
                await S.Engine.DeleteVoiceCloneSampleAsync(S._projectId, S.List._selectedKey, _voiceSaveCts?.Token ?? CancellationToken.None);
                _voiceCloneHint = "Sample removed.";
                _voiceClonePlayUrl = null;
                await S.List.ReloadSelectedCharacterAsync();
            }
            catch (Exception ex) { _voiceCloneError = ex.Message; }
            finally { _voiceCloneBusy = false; }
        }

    }
}
