using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    /// <summary>Structured screenplay editor + fountain text bridge for the screenplay page.</summary>
    public sealed class ScreenplayEditor
    {
        private readonly AdaptationScreenplay S;
        public ScreenplayEditor(AdaptationScreenplay host) => S = host;

        internal const string EditorId = "screenplay-main";

        internal string _text = "";
        internal string _loadedText = "";
        internal bool _editorReady;
        internal bool _jsInitStarted;
        internal bool _editorDataLoaded;
        internal bool _copied;
        internal int _sceneCount;

        /// <summary>Structured model driving the Screenplay Editor workbench.</summary>
        internal ScreenplayModel _model = new();

        // Kept for book modal / residual host references.
        internal ElementReference _editorHost;
        internal ElementReference _previewEl;
        internal ElementReference _scenesEl;
        internal DotNetObjectReference<AdaptationScreenplay>? _dotNetRef;

        internal bool CanEdit => _editorReady && !S.Busy && !S.Jobs.JobRunning;

        internal async Task CopyScreenplayAsync()
        {
            await SyncTextFromEditorAsync();
            var text = _text ?? "";
            try
            {
                await S.Js.InvokeVoidAsync("PageToMovieExport.copyTextAsync", text);
                _copied = true;
                S.StateHasChanged();
                await Task.Delay(1500);
                _copied = false;
                S.StateHasChanged();
            }
            catch { /* clipboard may be blocked */ }
        }

        internal async Task LoadEditorDataAsync()
        {
            try
            {
                var doc = await S.Engine.GetScreenplayAsync(S.ProjectId);
                _text = doc?.Text ?? "";
                _loadedText = _text;
                S.Save._dirtyLocal = false;
                S.SignOff._screenplayStatus = doc?.Screenplay ?? S.Status?.Screenplay;
                if (doc?.Adaptation is not null)
                    S.Status = doc.Adaptation;

                HydrateModelFromText();
                await SeedCastProfilesAsync();
                _editorReady = true;
                _editorDataLoaded = true;
                S.SignOff.UpdateWarningsFromText(_text);
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
                HydrateModelFromText();
                _editorReady = true;
                _editorDataLoaded = true;
            }
        }

        /// <summary>
        /// Fill character dropdowns from the cast classifier / Characters API — not from ALL CAPS
        /// action scraping (which invents fake characters like SOUND).
        /// </summary>
        internal async Task SeedCastProfilesAsync()
        {
            if (string.IsNullOrWhiteSpace(S.ProjectId)) return;
            try
            {
                var cast = await S.Engine.GetCharactersAsync(S.ProjectId);
                if (cast?.Characters is not { Count: > 0 }) return;
                foreach (var c in cast.Characters)
                {
                    var name = !string.IsNullOrWhiteSpace(c.DisplayName) ? c.DisplayName : c.Key;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    name = name.Replace('_', ' ').Trim();
                    if (name.Length < 1) continue;
                    // Skip synthetic keys that aren't display names
                    if (name.StartsWith("Character ", StringComparison.OrdinalIgnoreCase)
                        && name.Skip("Character ".Length).All(ch => char.IsDigit(ch) || ch == ' '))
                        continue;
                    var profile = _model.GetOrCreateCharacterProfile(name.ToUpperInvariant());
                    if (string.IsNullOrWhiteSpace(profile.VisualLockPrompt) && !string.IsNullOrWhiteSpace(c.VisualLock))
                        profile.VisualLockPrompt = c.VisualLock!;
                    if (string.IsNullOrWhiteSpace(profile.VoiceId) && !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId))
                        profile.VoiceId = c.VoiceProviderVoiceId!;
                    if (!string.IsNullOrWhiteSpace(c.VoiceProvider))
                        profile.VoiceProvider = c.VoiceProvider!;
                }
            }
            catch
            {
                /* cast optional until Characters step has run */
            }
        }

        internal void HydrateModelFromText()
        {
            // Preserve profiles already seeded from cast while re-parsing fountain text.
            var priorProfiles = _model.CharacterProfiles.ToList();
            var priorLocations = _model.LocationProfiles.ToList();
            _model = FountainFormatter.Parse(_text ?? "");
            foreach (var p in priorProfiles)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                if (_model.CharacterProfiles.All(x => !x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                    _model.CharacterProfiles.Add(p);
            }
            foreach (var loc in priorLocations)
            {
                if (string.IsNullOrWhiteSpace(loc.Name)) continue;
                if (_model.LocationProfiles.All(x => !x.Name.Equals(loc.Name, StringComparison.OrdinalIgnoreCase)))
                    _model.LocationProfiles.Add(loc);
            }
            if (_model.Scenes.Count == 0)
            {
                _model.Scenes.Add(new ScreenplayScene
                {
                    SceneNumber = 1,
                    Environment = "INT.",
                    Location = "LOCATION",
                    TimeOfDay = "DAY",
                    IsSelected = true,
                    Beats =
                    {
                        new ScreenplayBeat { Type = BeatType.Action, Text = "" }
                    }
                });
            }
            else if (!_model.Scenes.Any(s => s.IsSelected))
            {
                _model.Scenes[0].IsSelected = true;
            }
            _sceneCount = _model.Scenes.Count;
        }

        internal Task OnStructuredModelChanged(ScreenplayModel model)
        {
            _model = model ?? new ScreenplayModel();
            _text = FountainFormatter.ToFountain(_model);
            _sceneCount = _model.Scenes.Count;
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.SignOff.UpdateWarningsFromText(_text);
            S.Save.ScheduleAutosave();
            return S.InvokeAsync(S.StateHasChanged);
        }

        internal Task OnHostLoadAsync()
        {
            HydrateModelFromText();
            _editorReady = true;
            S.StateHasChanged();
            return Task.CompletedTask;
        }

        internal Task TryInitEditorAsync()
        {
            if (!_editorDataLoaded) return Task.CompletedTask;
            _editorReady = true;
            _jsInitStarted = true;
            return Task.CompletedTask;
        }

        internal Task OnEditorChanged(string text, string[] warnings, int sceneCount)
        {
            _text = text ?? "";
            _sceneCount = sceneCount;
            HydrateModelFromText();
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.SignOff.MapWarnings(warnings);
            S.Save.ScheduleAutosave();
            return S.InvokeAsync(S.StateHasChanged);
        }

        internal Task InsertAsync(string snippet)
        {
            if (!CanEdit) return Task.CompletedTask;
            _text = (_text ?? "").TrimEnd() + snippet;
            HydrateModelFromText();
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.Save.ScheduleAutosave();
            S.StateHasChanged();
            return Task.CompletedTask;
        }

        internal Task InsertTitleFieldAsync(string key, string? afterKey = null)
        {
            if (!CanEdit) return Task.CompletedTask;
            switch (key.Trim().ToLowerInvariant())
            {
                case "title":
                    if (string.IsNullOrWhiteSpace(_model.Metadata.Title))
                        _model.Metadata.Title = "Untitled";
                    break;
                case "author":
                case "authors":
                    if (string.IsNullOrWhiteSpace(_model.Metadata.Author))
                        _model.Metadata.Author = "Unknown";
                    break;
            }
            _ = afterKey;
            _text = FountainFormatter.ToFountain(_model);
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.Save.ScheduleAutosave();
            S.StateHasChanged();
            return Task.CompletedTask;
        }

        internal Task SyncTextFromEditorAsync()
        {
            _text = FountainFormatter.ToFountain(_model);
            _sceneCount = _model.Scenes.Count;
            return Task.CompletedTask;
        }

        internal Task DisposeEditorAsync()
        {
            _dotNetRef?.Dispose();
            _dotNetRef = null;
            return Task.CompletedTask;
        }
    }
}
