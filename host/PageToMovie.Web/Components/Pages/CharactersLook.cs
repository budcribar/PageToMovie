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
    /// <summary>Look domain for the Characters page. Owns related UI state and behavior.</summary>
    internal sealed class CharactersLook
    {
        private readonly Characters S;
        public CharactersLook(Characters host) => S = host;

        internal List<Candidate> _allCandidates = new();

        internal string? _chosenCandidateKey;

        internal PendingDelete? _deleteConfirm;

        internal string _editDescription = "";

        internal string _editVisualLock = "";

        internal ImageSeedLimits? _imageSeedLimits;

        internal long _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal bool _loadingBookCandidates;

        internal CancellationTokenSource? _lookSaveCts;

        internal string? _lookSaveHint;

        internal Mode _mode = Mode.PickSource;

        internal bool _panelPictureOpen = true;

        /// <summary>Look chosen in Compare mode, not yet confirmed locked (auto-flushed on cast switch).</summary>
        internal Candidate? _pendingLockCandidate;

        internal PictureRoute _pictureRoute = PictureRoute.Choose;

        internal List<RankedBookCandidateDto>? _rankedBookCandidates;

        /// <summary>Last loaded/saved look text — skip scrub API when editors match.</summary>
        internal string _savedLookDescription = "";

        internal string _savedLookVisualLock = "";

        internal bool _savingBookRefs;

        internal bool _savingLook;

        internal readonly List<string> _seedOrder = new();

        internal readonly List<string> _selectedBookCandidatePaths = new();

        internal bool _showBookCandidateGallery;

        internal Candidate? _styleRejectCandidate;

        internal string? _styleRejectMessage;

        internal Candidate? _zoomCandidate;

        internal double _zoomScale = 1;


        internal async Task ToggleBookCandidateGalleryAsync()
        {
            _showBookCandidateGallery = !_showBookCandidateGallery;
            if (_showBookCandidateGallery && S._selected is not null)
            {
                await LoadBookCandidatesAsync();
            }
        }


        internal async Task LoadBookCandidatesAsync()
        {
            if (S._selected is null || string.IsNullOrWhiteSpace(S._projectId)) return;
            _loadingBookCandidates = true;
            S._error = null;
            S.StateHasChanged();
            try
            {
                _rankedBookCandidates = await S.Engine.GetRankedBookCandidatesAsync(S._projectId, S._selected.Key);
                _selectedBookCandidatePaths.Clear();
                if (_rankedBookCandidates is not null)
                {
                    foreach (var c in _rankedBookCandidates)
                    {
                        if (c.IsSelected)
                            _selectedBookCandidatePaths.Add(c.PathRel);
                    }
                }
            }
            catch (Exception ex)
            {
                S._error = "Could not load book image candidates: " + ex.Message;
            }
            finally
            {
                _loadingBookCandidates = false;
                S.StateHasChanged();
            }
        }


        internal void ToggleBookCandidateSelection(string pathRel)
        {
            var idx = _selectedBookCandidatePaths.FindIndex(p => string.Equals(p, pathRel, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _selectedBookCandidatePaths.RemoveAt(idx);
            }
            else
            {
                if (_selectedBookCandidatePaths.Count >= 3)
                {
                    S._error = "Select up to 3 candidate pictures maximum.";
                    return;
                }
                _selectedBookCandidatePaths.Add(pathRel);
            }
            S._error = null;
        }


        internal async Task ApplySelectedBookCandidatesAsync()
        {
            if (S._selected is null || string.IsNullOrWhiteSpace(S._projectId)) return;
            if (_selectedBookCandidatePaths.Count == 0)
            {
                // Clear stored book refs when nothing selected
                _savingBookRefs = true;
                try
                {
                    await S.Engine.SetCharacterBookRefsAsync(S._projectId, S._selected.Key, []);
                    await S.SoftReloadAsync();
                    _seedOrder.Clear();
                    S._message = "Book pictures cleared.";
                    _showBookCandidateGallery = false;
                }
                catch (Exception ex) { S._error = ex.Message; }
                finally { _savingBookRefs = false; }
                return;
            }

            var ok = await EnsureGalleryBookSelectionAppliedAsync();
            if (ok && S._selected is not null)
            {
                S._message =
                    $"Saved {_seedOrder.Count} book picture(s) for {S._selected.DisplayName}. " +
                    "Only those are selected for Generate (click Book tiles to change).";
            }
        }


        internal int ApiMaxSeedRefs => Math.Max(1, _imageSeedLimits?.MaxReferenceImages ?? 3);


        internal int SelectedSeedCount => _seedOrder.Count;


        internal static string CandidateKey(Candidate c) => $"{c.Kind}:{c.Index}";



        internal void ResetSeedSelection()
        {
            _seedOrder.Clear();
            if (S._selected is null) return;
            // Book plates first (identity from the book), then preferred lock, then gen options.
            // Old order put preferred+variants first and often filled the 3-ref cap before any book pic.
            AddBookRefsToSeedOrder();
            if (S._selected.HasPreferred && _seedOrder.Count < ApiMaxSeedRefs)
                _seedOrder.Add("p");
            foreach (var v in S._selected.Variants.Where(x => x.Exists).OrderBy(x => x.Index))
            {
                if (_seedOrder.Count >= ApiMaxSeedRefs) break;
                var key = $"v{v.Index ?? 0}";
                if (!_seedOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                    _seedOrder.Add(key);
            }
        }


        /// <summary>After the operator picks book pictures, those become the generate seeds.</summary>
        internal void PreferBookRefsAsSeeds()
        {
            _seedOrder.Clear();
            if (S._selected is null) return;
            AddBookRefsToSeedOrder();
            // Keep preferred only if there is room — never let old variants crowd out book plates
            if (S._selected.HasPreferred && _seedOrder.Count < ApiMaxSeedRefs)
                _seedOrder.Add("p");
        }


        internal void AddBookRefsToSeedOrder()
        {
            if (S._selected is null) return;
            foreach (var b in S._selected.BookRefs.Where(x => x.Exists).OrderBy(x => x.Index ?? 0))
            {
                if (_seedOrder.Count >= ApiMaxSeedRefs) break;
                if (b.Index is not int i) continue;
                var key = $"b{i}";
                if (!_seedOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                    _seedOrder.Add(key);
            }
        }


        internal int SeedRank(string key)
        {
            var i = _seedOrder.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? 0 : i + 1;
        }


        internal void ToggleSeedKey(string key)
        {
            var i = _seedOrder.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                _seedOrder.RemoveAt(i);
            else
                _seedOrder.Add(key);
        }


        internal void RequestDeleteImage(string kind, int index)
        {
            _deleteConfirm = new PendingDelete { Kind = kind, Index = index };
            S._error = null;
            S._message = null;
        }


        internal void CancelDeleteImage() => _deleteConfirm = null;


        internal async Task ConfirmDeleteImageAsync()
        {
            if (S._selected is null || _deleteConfirm is null) return;
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.DeleteCharacterImageAsync(
                    S._projectId, S._selected.Key, _deleteConfirm.Kind, _deleteConfirm.Index);
                _deleteConfirm = null;
                await S.SoftReloadAsync();
                ResetSeedSelection();
                S._message = "Picture deleted.";
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal static bool IsWeakBookPlate(string? fileName)
        {
            var n = (fileName ?? "").ToLowerInvariant();
            return n.Contains("sampled") || n.Contains("text_page") || n.Contains("ocr");
        }


        internal static string BookPlateKindLabel(string? fileName)
        {
            var n = (fileName ?? "").ToLowerInvariant();
            if (n.Contains("cover")) return "cover";
            if (n.Contains("sparse")) return "art";
            if (n.Contains("sampled")) return "text?";
            if (n.Contains("embedded")) return "embed";
            if (n.Contains("bookref")) return "page";
            return "page";
        }


        /// <summary>Book-guided path only when the project has a PDF or page images.</summary>
        internal bool CanUseBookPictures =>
            S.ActiveProject.Status?.Book is { } book
            && (book.PdfExists || book.PageImageCount > 0);


        internal void ChoosePictureRoute(PictureRoute route)
        {
            _pictureRoute = route;
            S._error = null;
            if (route == PictureRoute.Book)
                _ = ToggleBookCandidateGalleryAsync();
            if (route == PictureRoute.Choose)
                _showBookCandidateGallery = false;
            S.StateHasChanged();
        }


        /// <summary>Book path: ensure selected plates are seeds, then generate 3 looks.</summary>
        internal async Task StartBookGuidedGenerateAsync()
        {
            if (S._selected is null) return;
            if (_selectedBookCandidatePaths.Count > 0)
            {
                var ok = await EnsureGalleryBookSelectionAppliedAsync();
                if (!ok) return;
            }
            if (!S._selected.BookRefs.Any(b => b.Exists) && SelectedSeedCount == 0)
            {
                S._error = "Select at least one book picture first.";
                return;
            }
            _seedOrder.Clear();
            AddBookRefsToSeedOrder();
            await StartRegenerateAsync();
        }


        internal void BackToSource()
        {
            CloseLookZoom();
            ResetCompare();
            _mode = Mode.PickSource;
            if (S._selected?.HasPreferred == true)
                _pictureRoute = PictureRoute.Choose;
        }


        internal void ResetCompare()
        {
            _allCandidates = new();
            CloseLookZoom();
        }


        internal async Task StartRegenerateAsync()
        {
            if (S._selected is null) return;

            // Gallery checkmarks are the intended seeds — do not require a separate "Use for generation"
            // click, and do not mix in preferred/variants the operator did not rank as tiles.
            if (_selectedBookCandidatePaths.Count > 0)
            {
                var prepared = await EnsureGalleryBookSelectionAppliedAsync();
                if (!prepared)
                    return;
            }

            if (SelectedSeedCount == 0 && string.IsNullOrWhiteSpace(_editDescription))
            {
                S._error = "Select book pictures (or another reference) or enter a description.";
                return;
            }

            var maxSend = ApiMaxSeedRefs;
            var sendOrder = _seedOrder.Take(maxSend).ToList();
            var includePref = sendOrder.Any(k => k is "p");
            var variants = new List<int>();
            var books = new List<int>();
            foreach (var k in sendOrder)
            {
                if (k.Length >= 2 && k[0] == 'v' && int.TryParse(k[1..], out var vi))
                    variants.Add(vi);
                if (k.Length >= 2 && k[0] == 'b' && int.TryParse(k[1..], out var bi))
                    books.Add(bi);
            }

            // Always 3 options so the pick grid is useful on first and later generates
            // (engine otherwise uses 1 when the character is already locked).
            await StartGenerateCoreAsync(new StartCharacterVariantsRequest
            {
                ProjectId = S._projectId,
                CharKey = S._selected.Key,
                Count = 3,
                SeedMode = SelectedSeedCount == 0 ? "none" : "explicit",
                IncludePreferred = includePref,
                IncludeLockedRef = includePref,
                BookRefIndices = books,
                VariantIndices = variants,
                SeedOrderKeys = sendOrder,
                MaxRefs = maxSend,
                DescriptionOverride = _editDescription,
                VisualLockOverride = _editVisualLock,
                PersistDescription = true,
            });
        }


        /// <summary>
        /// Persist gallery checkmarks as book refs and set generate seed order to those plates only.
        /// </summary>
        internal async Task<bool> EnsureGalleryBookSelectionAppliedAsync()
        {
            if (S._selected is null || _selectedBookCandidatePaths.Count == 0)
                return true;

            _savingBookRefs = true;
            S._error = null;
            S.StateHasChanged();
            try
            {
                var paths = _selectedBookCandidatePaths.Take(ApiMaxSeedRefs).ToList();
                var ok = await S.Engine.SetCharacterBookRefsAsync(S._projectId, S._selected.Key, paths);
                if (!ok)
                {
                    S._error = "Could not save the selected book pictures for generation.";
                    return false;
                }

                await S.SoftReloadAsync();
                // ONLY the checked book plates — not preferred, not previous options
                _seedOrder.Clear();
                AddBookRefsToSeedOrder();
                if (_seedOrder.Count == 0)
                {
                    S._error = "Book pictures were saved but could not be loaded as references. Try again.";
                    return false;
                }

                _showBookCandidateGallery = false;
                return true;
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                return false;
            }
            finally
            {
                _savingBookRefs = false;
                S.StateHasChanged();
            }
        }


        internal async Task StartSortCharacterPlatesAsync(bool useGrok = true)
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                try { await S.Hub.StartAsync(); } catch (Exception hex) { S._error = $"SignalR: {hex.Message}"; }
                await S.Engine.StartSortCharacterPlatesAsync(S._projectId, useGrok: useGrok, maxImages: 32);
                // Progress card owns in-progress UI (one Cancel there) — no green status banner
                S._message = null;
                var jobs = await S.Engine.GetJobAsync();
                S._job = jobs?.Job;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task StartGenerateCoreAsync(StartCharacterVariantsRequest req)
        {
            if (S._selected is null) return;
            S._busy = true;
            S._error = null;
            S._message = null;
            // Reset progress UI immediately so a prior 3/3 bar never carries over
            var total = req.Count > 0 ? req.Count : 3;
            S._job = new JobSnapshot
            {
                Status = "queued",
                Kind = "character",
                ProjectId = S._projectId,
                CharKey = req.CharKey,
                Message = "Starting…",
                Index = 0,
                Total = total,
                Log = new List<string>(),
                JobId = Guid.NewGuid().ToString("N"), // temporary until server job id arrives
            };
            _mode = Mode.WaitingGenerate;
            S.StateHasChanged();
            try
            {
                try { await S.Hub.StartAsync(); } catch (Exception hex) { S._error = $"SignalR: {hex.Message}"; }
                await S.Engine.StartCharacterVariantsAsync(req);
                var jobs = await S.Engine.GetJobAsync();
                if (jobs?.Job is { } j &&
                    (string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)))
                {
                    // Never adopt a finished job's Index/Total for the new run
                    if (j.Index > 0 && string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase))
                        j.Index = 0;
                    S._job = j;
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                _mode = Mode.PickSource;
                S._job = null;
            }
            finally { S._busy = false; }
        }


        internal void BeginCompareFromVariants()
        {
            if (S._selected is null)
            {
                _mode = Mode.PickSource;
                return;
            }

            var vars = S._selected.Variants.Where(v => v.Exists).OrderBy(v => v.Index).ToList();
            if (vars.Count == 0)
            {
                _mode = Mode.PickSource;
                S._error = "Generate finished but no pictures found.";
                return;
            }

            _allCandidates = vars.Select(v => new Candidate
            {
                Kind = "variant",
                Index = v.Index ?? 1,
                Label = $"Option {v.Index}",
                // Bust cache so second generate doesn't show stale first-round images
                Url = S.CacheBust(S.Engine.CharacterVariantUrl(S._projectId, S._selected.Key, v.Index ?? 1)),
            }).ToList();

            _mode = Mode.Compare;
            _panelPictureOpen = true;
            S._message = null;
        }



        internal void OpenLookZoom(Candidate c)
        {
            _zoomCandidate = c;
            _zoomScale = 1;
        }


        internal void CloseLookZoom()
        {
            _zoomCandidate = null;
            _zoomScale = 1;
        }


        internal void ToggleLookZoomScale()
        {
            _zoomScale = _zoomScale > 1.01 ? 1 : 2;
        }


        internal void ZoomPrev()
        {
            if (_zoomCandidate is null || _allCandidates.Count == 0) return;
            var i = _allCandidates.FindIndex(x =>
                string.Equals(x.Url, _zoomCandidate.Url, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Label, _zoomCandidate.Label, StringComparison.Ordinal));
            if (i < 0) i = 0;
            i = (i - 1 + _allCandidates.Count) % _allCandidates.Count;
            _zoomCandidate = _allCandidates[i];
            _zoomScale = 1;
        }


        internal void ZoomNext()
        {
            if (_zoomCandidate is null || _allCandidates.Count == 0) return;
            var i = _allCandidates.FindIndex(x =>
                string.Equals(x.Url, _zoomCandidate.Url, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Label, _zoomCandidate.Label, StringComparison.Ordinal));
            if (i < 0) i = 0;
            i = (i + 1) % _allCandidates.Count;
            _zoomCandidate = _allCandidates[i];
            _zoomScale = 1;
        }


        internal async Task LockFromZoomAsync()
        {
            if (_zoomCandidate is null) return;
            var c = _zoomCandidate;
            CloseLookZoom();
            await LockCandidateAsync(c);
        }


        internal async Task LockCandidateAsync(Candidate c, bool overrideStyle = false, string? overrideReason = null)
        {
            if (S._selected is null) return;
            // Remember choice so a cast-list switch can finish the save if this call is in flight.
            _pendingLockCandidate = c;
            _chosenCandidateKey = CandidateKey(c);
            var charKey = S._selected.Key;
            var display = S._selected.DisplayName;
            S._busy = true;
            S._error = null;
            if (overrideStyle) { _styleRejectCandidate = null; _styleRejectMessage = null; }
            try
            {
                if (c.Kind == "variant")
                    await S.Engine.LockCharacterVariantAsync(S._projectId, charKey, c.Index, overrideStyle, overrideReason);
                else if (c.Kind == "book")
                    await S.Engine.LockCharacterBookRefAsync(S._projectId, charKey, c.Index, overrideStyle, overrideReason);
                else
                    throw new InvalidOperationException($"Cannot lock look kind '{c.Kind}'.");
                _styleRejectCandidate = null;
                _styleRejectMessage = null;

                // Saved thumbnail/icon is the confirmation — no redundant "Saved look" banner.
                _pendingLockCandidate = null;
                _chosenCandidateKey = null;
                _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await S.SoftReloadAsync();
                await S.RefreshNavReadinessAsync();
                // Stay on this character; show the preferred look (do not wipe list state for others).
                ResetCompare();
                _mode = Mode.PickSource;
                _pictureRoute = PictureRoute.Choose;
                S.ApplyPanelsForSelected();
                ResetSeedSelection();
                if (S._selected is not null)
                {
                    foreach (var v in S._selected.Variants.Where(x => x.Exists))
                    {
                        var key = $"v{v.Index ?? 0}";
                        if (!_seedOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                            _seedOrder.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                // Style-gate rejection is overridable: the creator can lock any look regardless of the
                // project's default medium (photoreal character in an animated film, or vice versa).
                if (!overrideStyle && IsStyleGateRejection(ex.Message))
                {
                    _styleRejectCandidate = c;
                    _styleRejectMessage = ex.Message;
                }
                // Keep _pendingLockCandidate so switching cast can retry / flush once.
            }
            finally { S._busy = false; }
        }


        /// <summary>Keep the classifier's verdict (close the override prompt without locking).</summary>
        internal void DismissStyleReject()
        {
            _styleRejectCandidate = null;
            _styleRejectMessage = null;
            S._error = null;
            _pendingLockCandidate = null;
            _chosenCandidateKey = null;
        }


        internal static bool IsStyleGateRejection(string? message)
        {
            var m = message ?? "";
            return m.Contains("does not match the project style", StringComparison.OrdinalIgnoreCase)
                || m.Contains("live-action", StringComparison.OrdinalIgnoreCase)
                || m.Contains("could not read the portrait", StringComparison.OrdinalIgnoreCase)
                || m.Contains("style check", StringComparison.OrdinalIgnoreCase);
        }


        internal void OnLookDescriptionInput(ChangeEventArgs e)
        {
            _editDescription = e.Value?.ToString() ?? "";
            ScheduleAutoSaveLook();
        }


        internal void OnLookVisualLockInput(ChangeEventArgs e)
        {
            _editVisualLock = e.Value?.ToString() ?? "";
            ScheduleAutoSaveLook();
        }


        internal Task OnLookDescriptionChanged(string value)
        {
            _editDescription = value ?? "";
            ScheduleAutoSaveLook();
            return Task.CompletedTask;
        }


        internal Task OnLookVisualLockChanged(string value)
        {
            _editVisualLock = value ?? "";
            ScheduleAutoSaveLook();
            return Task.CompletedTask;
        }


        /// <summary>
        /// Debounced autosave: wait until typing pauses (~800ms) so we do not hit the API on every keystroke.
        /// Same pattern as voice profile autosave on this card.
        /// </summary>
        internal void ScheduleAutoSaveLook()
        {
            _lookSaveCts?.Cancel();
            _lookSaveCts?.Dispose();
            _lookSaveCts = new CancellationTokenSource();
            var token = _lookSaveCts.Token;
            _lookSaveHint = "Pending…";
            _ = AutoSaveLookDebouncedAsync(token);
        }


        internal async Task AutoSaveLookDebouncedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Characters.LookAutosaveDebounceMs, token);
                if (token.IsCancellationRequested || S._selected is null) return;
                _lookSaveHint = "Saving…";
                await S.InvokeAsync(S.StateHasChanged);
                await SaveLookAsync(silent: true);
                if (!token.IsCancellationRequested)
                {
                    _lookSaveHint = "Saved";
                    await S.InvokeAsync(S.StateHasChanged);
                }
            }
            catch (TaskCanceledException) { /* typing continued — new debounce wins */ }
            catch (Exception ex)
            {
                _lookSaveHint = "Save failed";
                S._error = ex.Message;
                await S.InvokeAsync(S.StateHasChanged);
            }
        }


        /// <param name="silent">Autosave: no full-page busy, no toast spam; skip AI scrub (cheap disk write).</param>
        internal async Task SaveLookAsync(bool silent = false)
        {
            if (S._selected is null) return;

            // Snapshot identity + text — never re-read S._selected after await for the POST.
            var charKey = S._selected.Key;
            var displayName = S._selected.DisplayName;

            // No text change → no API
            var desc = _editDescription ?? "";
            var vis = _editVisualLock ?? "";
            if (string.Equals(desc, _savedLookDescription, StringComparison.Ordinal) &&
                string.Equals(vis, _savedLookVisualLock, StringComparison.Ordinal))
            {
                if (!silent)
                {
                    S._error = null;
                    S._message = "No look changes.";
                }
                return;
            }

            if (!silent)
            {
                S._busy = true;
                S._error = null;
                S._message = null;
            }
            _savingLook = true;
            try
            {
                // Autosave: no Grok scrub (cost + latency every pause). Explicit saves / generate can scrub.
                var result = await S.Engine.UpdateCharacterLookAsync(
                    S._projectId,
                    charKey,
                    description: desc,
                    visualLock: vis,
                    scrubWithAi: !silent);

                var stillOnChar = string.Equals(S._selectedKey, charKey, StringComparison.OrdinalIgnoreCase);
                if (stillOnChar && !silent)
                {
                    if (!string.IsNullOrWhiteSpace(result.Description))
                        _editDescription = result.Description!;
                    if (result.VisualLock is not null)
                        _editVisualLock = result.VisualLock;
                }

                // Saved thumbnail/icon is the confirmation — no redundant "Saved look" banner.

                // Soft reload on silent is fine but keep editors stable if scrub didn't rewrite.
                await S.SoftReloadAsync();
                if (stillOnChar &&
                    string.Equals(S._selectedKey, charKey, StringComparison.OrdinalIgnoreCase) &&
                    S._selected is not null)
                {
                    if (!silent && !string.IsNullOrWhiteSpace(result.Description))
                        _editDescription = result.Description!;
                    else if (silent)
                    {
                        // Keep what the operator typed; mark as saved baseline
                    }
                    else
                        _editDescription = S._selected.Description ?? _editDescription ?? "";

                    if (!silent && result.VisualLock is not null)
                        _editVisualLock = result.VisualLock;
                    else if (!silent)
                        _editVisualLock = S._selected.VisualLock ?? _editVisualLock ?? "";

                    _savedLookDescription = _editDescription ?? "";
                    _savedLookVisualLock = _editVisualLock ?? "";
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    S._error = ex.Message;
                    S._message = null;
                }
                else throw;
            }
            finally
            {
                if (!silent) S._busy = false;
                _savingLook = false;
            }
        }


        internal async Task UnlockAsync()
        {
            if (S._selected is null) return;
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.UnlockCharacterAsync(S._projectId, S._selected.Key);
                S._message = $"Unlocked {S._selected.DisplayName}";
                await S.LoadAsync();
                ResetCompare();
                _mode = Mode.PickSource;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task OnUploadRefAsync(InputFileChangeEventArgs e)
        {
            if (S._selected is null || S._selected.VoiceOnly || S._selected.IsGroup) return;
            var file = e.File;
            if (file is null) return;

            // Capture identity before any re-render; buffer bytes while InputFile is still mounted.
            var charKey = S._selected.Key;
            var display = S._selected.DisplayName;
            var fileName = file.Name;
            byte[] bytes;
            try
            {
                const long max = 25 * 1024 * 1024;
                await using var stream = file.OpenReadStream(max);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                return;
            }

            if (bytes.Length < 64)
            {
                S._error = "That image is empty or too small.";
                return;
            }

            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                await using var stream = new MemoryStream(bytes, writable: false);
                await S.Engine.UploadCharacterRefAsync(S._projectId, charKey, stream, fileName);
                // Saved thumbnail/icon is the confirmation — no redundant "Saved look" banner.
                _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await S.SoftReloadAsync();
                try { await S.ActiveProject.RefreshReadinessAsync(S.Engine); } catch { /* nav */ }
                ResetCompare();
                _mode = Mode.PickSource;
                _pictureRoute = PictureRoute.Choose;
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }

    }
}
