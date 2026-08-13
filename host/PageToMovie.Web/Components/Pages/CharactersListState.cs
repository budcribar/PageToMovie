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
    /// <summary>List domain for the Characters page. Owns related UI state and behavior.</summary>
    public sealed class CharactersListState
    {
        private readonly Characters S;
        public CharactersListState(Characters host) => S = host;

        internal List<CharacterSummary>? _chars;

        internal bool _extractingCast;

        internal string? _focusHint;

        internal List<string>? _lastCastExtractKeys;

        internal CharacterPlatesState? _plates;

        /// <summary>True when extract started with an existing cast (rebuild vs first build).</summary>
        internal bool _rebuildCastHadExisting;

        internal CharacterSummary? _selected;

        internal string? _selectedKey;

        internal bool _simpleMode;


        internal string? PreferredImageUrl
        {
            get
            {
                if (_selected is null || !_selected.HasPreferred) return null;
                return ListThumbUrl(_selected);
            }
        }


        /// <summary>List thumbnail: preferred/lock/variant1, else first book pic, else null (empty placeholder).</summary>
        internal string? ListThumbUrl(CharacterSummary c)
        {
            if (c.VoiceOnly) return null;
            // Groups may optionally have a ref; do not invent one for list thumbs.
            // Locked / preferred ref always wins over old generate variants (uploads used to look
            // "gone" after reload when variant_01 still existed and was preferred in the list).
            if (c.Locked || !string.IsNullOrWhiteSpace(c.RefUrl))
                return S.CacheBust(S.Engine.CharacterRefUrl(S._projectId, c.Key));
            if (c.HasPreferred)
            {
                if (c.PreferredUrl is { Length: > 0 } u)
                    return S.CacheBust(S.Engine.AbsolutizeMediaUrl(u) ?? u);
                if (c.RefUrl is { Length: > 0 } r)
                    return S.CacheBust(S.Engine.AbsolutizeMediaUrl(r) ?? r);
                if (c.Variants.Any(v => v.Index == 1 && v.Exists))
                    return S.CacheBust(S.Engine.CharacterVariantUrl(S._projectId, c.Key, 1));
            }
            var book = c.BookRefs.FirstOrDefault(b => b.Exists);
            if (book is not null)
            {
                if (!string.IsNullOrEmpty(book.Url))
                    return S.CacheBust(S.Engine.AbsolutizeMediaUrl(book.Url) ?? book.Url);
                if (book.Index is int bi)
                    return S.CacheBust(S.Engine.CharacterBookRefUrl(S._projectId, c.Key, bi));
            }
            return null;
        }


        internal string PreferredImageLabel =>
            _selected is null ? ""
            : _selected.HasPreferred
                ? $"Preferred · {_selected.PreferredLabel}"
                : "No preferred image";


        internal bool HasCast => _chars is { Count: > 0 };


        /// <summary>
        /// Other age-variant seeds of the same identity as <paramref name="c"/> — siblings share
        /// a base character, found either via <c>c</c> itself being a variant (<see cref="CharacterSummary.VariantOf"/>
        /// points at the base) or via <c>c</c> being the base that other variants point back at.
        /// Most characters appear at one life stage and have no siblings.
        /// </summary>
        internal IReadOnlyList<CharacterSummary> VariantSiblingsOf(CharacterSummary? c)
        {
            if (c is null || _chars is null) return Array.Empty<CharacterSummary>();
            var baseKey = string.IsNullOrWhiteSpace(c.VariantOf) ? c.Key : c.VariantOf;
            return _chars
                .Where(x => !string.Equals(x.Key, c.Key, StringComparison.OrdinalIgnoreCase))
                .Where(x =>
                    string.Equals(x.VariantOf, baseKey, StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrWhiteSpace(x.VariantOf) && string.Equals(x.Key, baseKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }


        /// <summary>Gate for the Characters.LookPanel "Age variants" card — progressive disclosure.</summary>
        internal bool HasAgeVariants(CharacterSummary? c) => VariantSiblingsOf(c).Count > 0;


        /// <summary>
        /// <see cref="CharactersForUi"/> reordered so each age-variant seed sits directly after its
        /// base character (indented in the roster) instead of scattered alphabetically. A variant
        /// whose base isn't in the UI list (e.g. base seed missing) still appears, un-indented, so
        /// nothing silently disappears from the roster.
        /// </summary>
        internal IEnumerable<CharacterSummary> CharactersForUiGrouped()
        {
            var all = CharactersForUi.ToList();
            var byBase = all
                .Where(c => !string.IsNullOrWhiteSpace(c.VariantOf))
                .GroupBy(c => c.VariantOf!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<CharacterSummary>)g.ToList(), StringComparer.OrdinalIgnoreCase);
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in all)
            {
                if (emitted.Contains(c.Key)) continue;
                if (!string.IsNullOrWhiteSpace(c.VariantOf) && all.Any(b => string.Equals(b.Key, c.VariantOf, StringComparison.OrdinalIgnoreCase)))
                    continue; // emitted right after its base, below

                emitted.Add(c.Key);
                yield return c;
                if (byBase.TryGetValue(c.Key, out var sibs))
                    foreach (var s in sibs)
                    {
                        emitted.Add(s.Key);
                        yield return s;
                    }
            }
        }


        /// <summary>Readable label for an age_band value, e.g. "child_8_9" → "Child (8-9)". Falls back to the display name.</summary>
        internal static string AgeVariantLabel(CharacterSummary c)
        {
            var band = c.AgeBand.HasValue ? c.AgeBand.Value.ToString().Trim() : null;
            if (string.IsNullOrEmpty(band)) return c.DisplayName;
            var parts = band.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return c.DisplayName;
            var stage = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
            var numbers = parts.Skip(1).Where(p => p.Length > 0 && char.IsDigit(p[0])).ToList();
            return numbers.Count > 0 ? $"{stage} ({string.Join("-", numbers)})" : stage;
        }


        /// <summary>When false (default), hide cast seeds that never appear in the shot plan.</summary>
        internal bool _showUnusedInPlan;

        /// <summary>Operator-facing cast: hide group/chorus seeds (too abstract for average users)
        /// and unused-in-plan seeds unless the operator opts in.</summary>
        internal IEnumerable<CharacterSummary> CharactersForUi =>
            _chars?.Where(c =>
                !c.IsGroup
                && (_showUnusedInPlan || c.UsedInPlan))
            ?? Enumerable.Empty<CharacterSummary>();

        internal int UnusedInPlanCount =>
            _chars?.Count(c => !c.IsGroup && !c.UsedInPlan) ?? 0;

        /// <summary>Faces in the current plan (operator list, groups excluded).</summary>
        internal int UsedInPlanCount =>
            _chars?.Count(c => !c.IsGroup && c.UsedInPlan) ?? 0;

        /// <summary>Used faces with a locked or preferred look.</summary>
        internal int LockedLookCount =>
            CharactersForUi.Count(c => !c.VoiceOnly && (c.Locked || c.HasPreferred));

        /// <summary>Used individual faces still missing a preferred look.</summary>
        internal int NeedLookCount =>
            CharactersForUi.Count(c => !c.VoiceOnly && !c.Locked && !c.HasPreferred);


        internal int OperatorCastCount => CharactersForUi.Count();


        /// <summary>Every cast member has look (if needed) + voice — next is shot plan or scenes.</summary>
        internal bool IsCastComplete =>
            OperatorCastCount > 0 &&
            CharactersForUi.All(c =>
                Characters.CharactersVoice.HasVoiceProfile(c) &&
                (c.VoiceOnly || c.HasPreferred || c.Locked));


        /// <summary>Show primary Build cast only when there is no cast yet.</summary>
        internal bool NeedsCastBuild =>
            _chars is not null && OperatorCastCount == 0 && !_extractingCast;


        /// <summary>Book picture matching is now automated on cast extract.</summary>
        internal bool NeedsFindCharacters => false;


        /// <summary>
        /// Start AI cast extract as a background job (cast + looks + locations).
        /// Completion is handled in <see cref="CharactersJobs.OnJobUpdated"/>.
        /// </summary>
        internal async Task ExtractCastAsync()
        {
            if (string.IsNullOrWhiteSpace(S._projectId) || _extractingCast) return;
            _rebuildCastHadExisting = _chars is { Count: > 0 };
            _extractingCast = true;
            S._busy = true;
            S._error = null;
            S._message = null;
            _selectedKey = null;
            _selected = null;
            S.StateHasChanged();
            try
            {
                try { await S.Hub.StartAsync(); } catch (Exception hex) { S._error = $"SignalR: {hex.Message}"; }
                var result = await S.Engine.ExtractCastFromScreenplayAsync(S._projectId, force: true);
                if (result is null || result.Ok != true)
                {
                    S._error = result?.Error ?? "Could not start cast extract.";
                    _extractingCast = false;
                    _rebuildCastHadExisting = false;
                    S._busy = false;
                    return;
                }

                // Async job — keep _extractingCast until Jobs.OnJobUpdated finishes.
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs._job = jobs?.Job;
                S._busy = false;
                S.StateHasChanged();
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                S._message = null;
                _extractingCast = false;
                _rebuildCastHadExisting = false;
                S._busy = false;
                S.StateHasChanged();
            }
        }


        internal async Task OnProjectChangedAsync()
        {
            S.LookPipe.ResetCompare();
            S.LookPipe._mode = Mode.PickSource;
            await LoadAsync();
        }


        internal async Task LoadAsync()
        {
            S._busy = true;
            S._error = null;
            try
            {
                var dto = await S.Engine.GetCharactersAsync(S._projectId);
                _chars = dto?.Characters ?? new List<CharacterSummary>();
                _plates = dto?.CharacterPlates;
                S.LookBook._imageSeedLimits = dto?.ImageSeedLimits;
                S.LookPipe._imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Keep selection if still in the list; otherwise open the first used face
                // so the index never looks like a broken empty strip of thumbnails.
                if (_selectedKey is not null && CharactersForUi.Any(c => c.Key == _selectedKey))
                    await SelectCoreAsync(_selectedKey, resetMode: false, flushPending: false);
                else
                {
                    var first = CharactersForUiGrouped().FirstOrDefault();
                    if (first is not null)
                        await SelectCoreAsync(first.Key, resetMode: true, flushPending: false);
                    else
                    {
                        _selectedKey = null;
                        _selected = null;
                        S.LookPipe.ResetCompare();
                        S.LookPipe._mode = Mode.PickSource;
                    }
                }

                FocusNarratorIfNeeded();

                var jobs = await S.Engine.GetJobAsync();
                S.Jobs._job = jobs?.Job;
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                _chars = null;
                _plates = null;
                S.LookBook._imageSeedLimits = null;
            }
            finally { S._busy = false; }
        }


        internal async Task SoftReloadAsync()
        {
            try
            {
                var dto = await S.Engine.GetCharactersAsync(S._projectId);
                _chars = dto?.Characters ?? new List<CharacterSummary>();
                _plates = dto?.CharacterPlates;
                S.LookBook._imageSeedLimits = dto?.ImageSeedLimits;
                S.LookPipe._imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_selectedKey is not null)
                    await SelectCoreAsync(_selectedKey, resetMode: false, flushPending: false);
            }
            catch { /* ignore */ }
        }


        /// <summary>
        /// Block cast-list switches while save/generate/etc. runs so async completion
        /// cannot paste one character's look into another's editors.
        /// </summary>
        internal bool CastListLocked => S._busy || S.LookEdit._savingLook || S.Jobs.JobRunning || _extractingCast;


        internal Task SelectAsync(string key) => SelectCoreAsync(key, resetMode: true, flushPending: true);

        /// <summary>Deep link from Film/Script: /characters?char=KeyOrName</summary>
        internal async Task TrySelectFromQueryAsync()
        {
            var q = StudioDeepLinks.QueryValue(S.Nav, "char");
            if (string.IsNullOrWhiteSpace(q)) return;
            var match = StudioDeepLinks.MatchCharacter(CharactersForUi, q)
                        ?? StudioDeepLinks.MatchCharacter(_chars, q);
            if (match is null) return;
            await SelectCoreAsync(match.Key, resetMode: true, flushPending: false);
        }


        /// <summary>
        /// Switch cast member. When the operator leaves a character with a pending chosen look
        /// (or mid-compare selection), we lock it first so pictures do not vanish on switch.
        /// </summary>
        internal async Task SelectCoreAsync(string key, bool resetMode, bool flushPending)
        {
            var switched = !string.Equals(_selectedKey, key, StringComparison.OrdinalIgnoreCase);
            // SoftReload re-selects the same key while _busy — allow that.
            if (switched && CastListLocked && !flushPending)
                return;

            if (switched && flushPending && _selected is not null && S.LookPipe._pendingLockCandidate is not null)
            {
                try
                {
                    await S.LookPipe.LockCandidateAsync(S.LookPipe._pendingLockCandidate);
                }
                catch
                {
                    // LockCandidateAsync already sets _error
                }
            }

            // Block switch only while another action still runs after flush.
            if (switched && CastListLocked)
                return;

            _selectedKey = key;
            _selected = CharactersForUi.FirstOrDefault(c =>
                string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            S.LookPipe._pendingLockCandidate = null;
            S.LookPipe._chosenCandidateKey = null;
            if (_selected is not null)
            {
                S.LookEdit._editDescription = _selected.Description ?? "";
                S.LookEdit._editVisualLock = _selected.VisualLock ?? "";
                S.LookEdit._savedLookDescription = S.LookEdit._editDescription;
                S.LookEdit._savedLookVisualLock = S.LookEdit._editVisualLock;
                S.LookEdit._lookSaveHint = null;
                if (S.LookEdit._lookSaveCts is { } oldSaveCts)
                {
                    await oldSaveCts.CancelAsync();
                    oldSaveCts.Dispose();
                }
                S.LookEdit._lookSaveCts = null;
                S.Voice._editVoiceLabel = _selected.VoiceLabel ?? "";
                S.Voice._editVoiceProfile = _selected.VoiceProfile ?? "";
                S.Voice._forceShowVoice = false;
                S.Voice.RefreshVoiceClonePlayUrl();
                S.Voice._voiceCloneHint = null;
                S.Voice._voiceCloneError = null;
                S.Voice._voicePreviewUrl = null;
                S.Voice._voicePreviewError = null;
                S.Voice._voicePreviewHint = null;
                S.Voice._voicePreviewStale = false;
                _ = S.InvokeAsync(() => S.Voice.TryLoadCachedVoiceAsync());
            }
            if (switched)
            {
                S.LookPipe._deleteConfirm = null;
                ApplyPanelsForSelected();
            }
            if (resetMode)
            {
                S.LookPipe.ResetCompare();
                S.LookPipe._mode = Mode.PickSource;
                if (switched)
                    S.LookPipe._pictureRoute = PictureRoute.Choose;
                S._error = null;
                S._message = null;
                S.LookBook.ResetSeedSelection();
                ApplyPanelsForSelected();
            }

            // Cast list is a child component; parent Look/Voice panel must re-render after switch.
            S.StateHasChanged();
        }


        /// <summary>
        /// Look & voice live on one card — keep it open so neither section is buried.
        /// </summary>
        internal void ApplyPanelsForSelected()
        {
            // Single card for picture + voice; always expanded when a character is selected.
            S.LookEdit._panelPictureOpen = true;
        }




        internal void ApplySimpleModeFromUri()
        {
            var querySimple = false;
            try
            {
                var uri = S.Nav.ToAbsoluteUri(S.Nav.Uri);
                var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                querySimple = q.TryGetValue("simple", out var sVals) &&
                              sVals.Any(v => v is "1" or "true" or "yes");
                if (q.TryGetValue("focus", out var fVals))
                    _focusHint = fVals.FirstOrDefault();
            }
            catch
            {
                /* ignore */
            }

            // project.json studioPath is source of truth; ?simple=1 still works as override entry
            _simpleMode = S.ActiveProject.IsSimpleVoice || querySimple;

            S.Voice._useKidsScript = _simpleMode ||
                VoiceCloneScripts.LooksLikeChildrensStory(
                    S.ActiveProject.Label,
                    genre: null,
                    projectId: S._projectId);
        }


        internal async Task ExitSimplePathAsync()
        {
            if (string.IsNullOrEmpty(S._projectId)) return;
            try
            {
                await S.Engine.SetStudioPathAsync(S._projectId, ProjectStudioPaths.Full);
                S.ActiveProject.Set(S.ActiveProject.ProjectId, S.ActiveProject.Label, S.ActiveProject.ParentProjectId, ProjectStudioPaths.Full);
                _simpleMode = false;
                S.Voice._useKidsScript = VoiceCloneScripts.LooksLikeChildrensStory(
                    S.ActiveProject.Label, null, S._projectId);
                S.Nav.NavigateTo("characters", forceLoad: false);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
        }


        internal void FocusNarratorIfNeeded()
        {
            if (_chars is null || _chars.Count == 0) return;
            var wantNarrator = _simpleMode
                || string.Equals(_focusHint, "narrator", StringComparison.OrdinalIgnoreCase);
            if (!wantNarrator) return;

            var ui = CharactersForUi.ToList();
            if (ui.Count == 0) return;
            var pick = ui.FirstOrDefault(c =>
                           (c.Key?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false)
                           || (c.DisplayName?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false))
                       ?? ui.FirstOrDefault(c => c.VoiceOnly)
                       ?? ui[0];
            if (pick is null) return;
            if (!string.Equals(_selectedKey, pick.Key, StringComparison.OrdinalIgnoreCase))
                _ = SelectAsync(pick.Key);
        }


        internal async Task ReloadSelectedCharacterAsync()
        {
            if (string.IsNullOrEmpty(S._projectId)) return;
            try
            {
                var dto = await S.Engine.GetCharactersAsync(S._projectId);
                _chars = dto?.Characters ?? new List<CharacterSummary>();
                _selected = CharactersForUi.FirstOrDefault(c =>
                    string.Equals(c.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
                if (_selected is not null)
                {
                    S.Voice._editVoiceLabel = _selected.VoiceLabel ?? "";
                    S.Voice._editVoiceProfile = _selected.VoiceProfile ?? "";
                    S.Voice.RefreshVoiceClonePlayUrl();
                }
            }
            catch (Exception)
            {
                return;
            }
        }

    }
}
