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
    /// <summary>Book gallery + seed selection for Characters look workflow.</summary>
    public sealed class CharactersLookBook
    {
        private readonly Characters S;
        public CharactersLookBook(Characters host) => S = host;

        internal ImageSeedLimits? _imageSeedLimits;

        internal bool _loadingBookCandidates;

        internal List<RankedBookCandidateDto>? _rankedBookCandidates;

        internal bool _savingBookRefs;

        internal readonly List<string> _seedOrder = new();

        internal readonly List<string> _selectedBookCandidatePaths = new();

        internal bool _showBookCandidateGallery;


        internal async Task ToggleBookCandidateGalleryAsync()
        {
            _showBookCandidateGallery = !_showBookCandidateGallery;
            if (_showBookCandidateGallery && S.List._selected is not null)
            {
                await LoadBookCandidatesAsync();
            }
        }


        internal async Task LoadBookCandidatesAsync()
        {
            if (S.List._selected is null || string.IsNullOrWhiteSpace(S._projectId)) return;
            _loadingBookCandidates = true;
            S._error = null;
            S.StateHasChanged();
            try
            {
                _rankedBookCandidates = await S.Engine.GetRankedBookCandidatesAsync(S._projectId, S.List._selected.Key);
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
            if (S.List._selected is null || string.IsNullOrWhiteSpace(S._projectId)) return;
            if (_selectedBookCandidatePaths.Count == 0)
            {
                // Clear stored book refs when nothing selected
                _savingBookRefs = true;
                try
                {
                    await S.Engine.SetCharacterBookRefsAsync(S._projectId, S.List._selected.Key, []);
                    await S.List.SoftReloadAsync();
                    _seedOrder.Clear();
                    S._message = "Book pictures cleared.";
                    _showBookCandidateGallery = false;
                }
                catch (Exception ex) { S._error = ex.Message; }
                finally { _savingBookRefs = false; }
                return;
            }

            var ok = await EnsureGalleryBookSelectionAppliedAsync();
            if (ok && S.List._selected is not null)
            {
                S._message =
                    $"Saved {_seedOrder.Count} book picture(s) for {S.List._selected.DisplayName}. " +
                    "Only those are selected for Generate (click Book tiles to change).";
            }
        }


        internal int ApiMaxSeedRefs => Math.Max(1, _imageSeedLimits?.MaxReferenceImages ?? 3);


        internal int SelectedSeedCount => _seedOrder.Count;


        internal void ResetSeedSelection()
        {
            _seedOrder.Clear();
            if (S.List._selected is null) return;
            // Book plates first (identity from the book), then preferred lock, then gen options.
            // Old order put preferred+variants first and often filled the 3-ref cap before any book pic.
            AddBookRefsToSeedOrder();
            if (S.List._selected.HasPreferred && _seedOrder.Count < ApiMaxSeedRefs)
                _seedOrder.Add("p");
            foreach (var v in S.List._selected.Variants.Where(x => x.Exists).OrderBy(x => x.Index))
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
            if (S.List._selected is null) return;
            AddBookRefsToSeedOrder();
            // Keep preferred only if there is room — never let old variants crowd out book plates
            if (S.List._selected.HasPreferred && _seedOrder.Count < ApiMaxSeedRefs)
                _seedOrder.Add("p");
        }


        internal void AddBookRefsToSeedOrder()
        {
            if (S.List._selected is null) return;
            foreach (var b in S.List._selected.BookRefs.Where(x => x.Exists).OrderBy(x => x.Index ?? 0))
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


        /// <summary>
        /// Persist gallery checkmarks as book refs and set generate seed order to those plates only.
        /// </summary>
        internal async Task<bool> EnsureGalleryBookSelectionAppliedAsync()
        {
            if (S.List._selected is null || _selectedBookCandidatePaths.Count == 0)
                return true;

            _savingBookRefs = true;
            S._error = null;
            S.StateHasChanged();
            try
            {
                var paths = _selectedBookCandidatePaths.Take(ApiMaxSeedRefs).ToList();
                var ok = await S.Engine.SetCharacterBookRefsAsync(S._projectId, S.List._selected.Key, paths);
                if (!ok)
                {
                    S._error = "Could not save the selected book pictures for generation.";
                    return false;
                }

                await S.List.SoftReloadAsync();
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
                S.Jobs._job = jobs?.Job;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }

    }
}
