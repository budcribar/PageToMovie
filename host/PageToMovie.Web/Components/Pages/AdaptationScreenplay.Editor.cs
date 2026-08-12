using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.ScreenplayEditor.Models;

using PageToMovie.Core.Utils;
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
        /// Fill character/location profiles from Stage‑1 classifier seeds (cast + location_seed_tokens).
        /// Not from ALL CAPS action scraping (which invents fake characters like SOUND).
        /// </summary>
        internal async Task SeedCastProfilesAsync()
        {
            if (string.IsNullOrWhiteSpace(S.ProjectId)) return;
            try
            {
                var cast = await S.Engine.GetCharactersAsync(S.ProjectId);
                if (cast?.Characters is { Count: > 0 })
                {
                    // Rebuild cast list from classifier only (preserve field edits for matching names).
                    var prior = _model.CharacterProfiles
                        .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                        .ToDictionary(p => p.Name.Trim().ToUpperInvariant(), p => p, StringComparer.OrdinalIgnoreCase);
                    _model.CharacterProfiles.Clear();

                    foreach (var c in cast.Characters)
                    {
                        var name = !string.IsNullOrWhiteSpace(c.DisplayName) ? c.DisplayName : c.Key;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        name = name.Replace('_', ' ').Trim();
                        if (name.Length < 1) continue;
                        if (name.StartsWith("Character ", StringComparison.OrdinalIgnoreCase)
                            && name.Skip("Character ".Length).All(ch => char.IsDigit(ch) || ch == ' '))
                            continue;

                        var key = name.ToUpperInvariant();
                        prior.TryGetValue(key, out var existing);
                        var profile = existing ?? new ScreenplayCharacterProfile { Name = key };
                        profile.Name = key;
                        profile.FromClassifier = true;
                        // Prefer live cast API text; clear stub values that are just the Character_ key.
                        if (!string.IsNullOrWhiteSpace(c.Description) && !LooksLikeSeedKey(c.Description, c.Key, name))
                            profile.Description = c.Description.Trim();
                        else if (LooksLikeSeedKey(profile.Description, c.Key, name))
                            profile.Description = "";
                        if (!string.IsNullOrWhiteSpace(c.VisualLock) && !LooksLikeSeedKey(c.VisualLock, c.Key, name))
                            profile.VisualLockPrompt = c.VisualLock.Trim();
                        else if (LooksLikeSeedKey(profile.VisualLockPrompt, c.Key, name))
                            profile.VisualLockPrompt = "";
                        if (string.IsNullOrWhiteSpace(profile.WardrobeAlways) && c.WardrobeAlways is { Count: > 0 })
                            profile.WardrobeAlways = string.Join("; ", c.WardrobeAlways);
                        if (string.IsNullOrWhiteSpace(profile.VoiceId) && !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId))
                            profile.VoiceId = c.VoiceProviderVoiceId;
                        if (string.IsNullOrWhiteSpace(profile.VoiceProvider) && !string.IsNullOrWhiteSpace(c.VoiceProvider))
                            profile.VoiceProvider = c.VoiceProvider;
                        if (string.IsNullOrWhiteSpace(profile.VoiceLabel) && !string.IsNullOrWhiteSpace(c.VoiceLabel))
                            profile.VoiceLabel = c.VoiceLabel;
                        if (string.IsNullOrWhiteSpace(profile.VoiceProfile) && !string.IsNullOrWhiteSpace(c.VoiceProfile))
                            profile.VoiceProfile = c.VoiceProfile;
                        profile.Speaks = c.Speaks;
                        profile.SpeciesKind = c.SpeciesKind;
                        profile.IsImageLocked = c.Locked || c.HasPreferred;
                        _model.CharacterProfiles.Add(profile);
                    }
                }
            }
            catch
            {
                /* cast optional until Characters step has run */
            }

            try
            {
                var locs = await S.Engine.GetLocationsAsync(S.ProjectId);
                if (locs?.Locations is not { Count: > 0 }) return;
                foreach (var loc in locs.Locations)
                {
                    var name = !string.IsNullOrWhiteSpace(loc.DisplayName) ? loc.DisplayName : loc.Key;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    name = name.Replace('_', ' ').Trim().ToUpperInvariant();
                    // Drop env junk the model put in display names ("AND INT. PALACE").
                    name = CommonRegex.Replace(
                        name, @"^(AND\s+)?(INT\.?|EXT\.?)\s+", "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var profile = _model.GetOrCreateLocationProfile(name);
                    // Prefer API text when profile is empty OR still a heading/name stub.
                    var stubDesc = string.IsNullOrWhiteSpace(profile.Description)
                        || profile.Description.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)
                        || profile.Description.StartsWith("ext ", StringComparison.OrdinalIgnoreCase)
                        || profile.Description.StartsWith("int ", StringComparison.OrdinalIgnoreCase)
                        || profile.Description.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)
                        || profile.Description.StartsWith("int.", StringComparison.OrdinalIgnoreCase);
                    if (stubDesc && !string.IsNullOrWhiteSpace(loc.Description))
                        profile.Description = loc.Description;
                    var stubLock = string.IsNullOrWhiteSpace(profile.VisualLock)
                        || profile.VisualLock.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)
                        || profile.VisualLock.StartsWith("ext ", StringComparison.OrdinalIgnoreCase)
                        || profile.VisualLock.StartsWith("int ", StringComparison.OrdinalIgnoreCase);
                    if (stubLock && !string.IsNullOrWhiteSpace(loc.VisualLock))
                        profile.VisualLock = loc.VisualLock;
                    else if (stubLock && !string.IsNullOrWhiteSpace(loc.Description))
                        profile.VisualLock = loc.Description;
                }

                // Also attach descriptions to scene headings that match a seed under a different key
                foreach (var scene in _model.Scenes)
                {
                    if (string.IsNullOrWhiteSpace(scene.Location)) continue;
                    // Normalize broken scene location names in-memory for display/edit.
                    var cleaned = CommonRegex.Replace(
                        scene.Location, @"^(AND\s+)?(INT\.?|EXT\.?)\s+", "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    if (!string.IsNullOrWhiteSpace(cleaned)
                        && !cleaned.Equals(scene.Location, StringComparison.OrdinalIgnoreCase))
                        scene.Location = cleaned.ToUpperInvariant();

                    var p = _model.GetOrCreateLocationProfile(scene.Location);
                    if (!string.IsNullOrWhiteSpace(p.Description)
                        && !p.Description.StartsWith("ext ", StringComparison.OrdinalIgnoreCase)
                        && !p.Description.StartsWith("int ", StringComparison.OrdinalIgnoreCase)
                        && !p.Description.StartsWith("ext.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var match = locs.Locations.FirstOrDefault(l =>
                        string.Equals(l.DisplayName, scene.Location, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(l.Key.Replace('_', ' '), scene.Location, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(l.Key, scene.Location.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase)
                        || (l.DisplayName is { Length: > 0 }
                            && scene.Location.Contains(l.DisplayName, StringComparison.OrdinalIgnoreCase)));
                    if (match is null) continue;
                    if (!string.IsNullOrWhiteSpace(match.Description))
                        p.Description = match.Description;
                    if (!string.IsNullOrWhiteSpace(match.VisualLock))
                        p.VisualLock = match.VisualLock;
                }
            }
            catch
            {
                /* locations optional until Stage‑1 seeds exist */
            }
        }

        internal void HydrateModelFromText()
        {
            // Preserve profiles already seeded from cast/locations while re-parsing fountain text.
            var priorProfiles = _model.CharacterProfiles.ToList();
            var priorLocations = _model.LocationProfiles.ToList();
            _model = FountainFormatter.Parse(_text ?? "");
            foreach (var p in priorProfiles)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var existing = _model.CharacterProfiles.FirstOrDefault(x =>
                    x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    _model.CharacterProfiles.Add(p);
                else
                {
                    if (string.IsNullOrWhiteSpace(existing.Description)) existing.Description = p.Description;
                    if (string.IsNullOrWhiteSpace(existing.VisualLockPrompt)) existing.VisualLockPrompt = p.VisualLockPrompt;
                    if (string.IsNullOrWhiteSpace(existing.WardrobeAlways)) existing.WardrobeAlways = p.WardrobeAlways;
                    if (string.IsNullOrWhiteSpace(existing.VoiceProfile)) existing.VoiceProfile = p.VoiceProfile;
                    if (string.IsNullOrWhiteSpace(existing.VoiceLabel)) existing.VoiceLabel = p.VoiceLabel;
                    existing.Speaks = existing.Speaks || p.Speaks;
                    existing.FromClassifier = existing.FromClassifier || p.FromClassifier;
                }
            }
            foreach (var loc in priorLocations)
            {
                if (string.IsNullOrWhiteSpace(loc.Name)) continue;
                var existing = _model.LocationProfiles.FirstOrDefault(x =>
                    x.Name.Equals(loc.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    _model.LocationProfiles.Add(loc);
                else
                {
                    if (string.IsNullOrWhiteSpace(existing.Description)) existing.Description = loc.Description;
                    if (string.IsNullOrWhiteSpace(existing.VisualLock)) existing.VisualLock = loc.VisualLock;
                }
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

        /// <summary>
        /// True when text is just a Character_/Loc_ seed key (or bare display name), not filmable prose.
        /// </summary>
        private static bool LooksLikeSeedKey(string? text, string? key, string? displayName)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var t = text.Trim();
            if (t.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Loc_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(key)
                && t.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(displayName)
                && t.Equals(displayName.Trim(), StringComparison.OrdinalIgnoreCase)
                && t.Length < 40
                && !t.Contains(' '))
                return true;
            // "Character Antinous" after underscore→space strip of the key
            if (!string.IsNullOrWhiteSpace(key))
            {
                var keyAsWords = key.Replace('_', ' ').Trim();
                if (t.Equals(keyAsWords, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
