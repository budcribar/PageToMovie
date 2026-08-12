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
    /// <summary>Look generate / compare / lock / zoom pipeline for Characters.</summary>
    public sealed class CharactersLookPipeline
    {
        private readonly Characters S;
        public CharactersLookPipeline(Characters host) => S = host;

        internal List<Candidate> _allCandidates = new();

        internal string? _chosenCandidateKey;

        internal PendingDelete? _deleteConfirm;

        internal long _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal Mode _mode = Mode.PickSource;

        /// <summary>Look chosen in Compare mode, not yet confirmed locked (auto-flushed on cast switch).</summary>
        internal Candidate? _pendingLockCandidate;

        internal PictureRoute _pictureRoute = PictureRoute.Choose;

        internal Candidate? _styleRejectCandidate;

        internal string? _styleRejectMessage;

        internal Candidate? _zoomCandidate;

        internal double _zoomScale = 1;


        internal static string CandidateKey(Candidate c) => $"{c.Kind}:{c.Index}";


        internal void RequestDeleteImage(string kind, int index)
        {
            _deleteConfirm = new PendingDelete { Kind = kind, Index = index };
            S._error = null;
            S._message = null;
        }


        internal void CancelDeleteImage() => _deleteConfirm = null;


        internal async Task ConfirmDeleteImageAsync()
        {
            if (S.List._selected is null || _deleteConfirm is null) return;
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.DeleteCharacterImageAsync(
                    S._projectId, S.List._selected.Key, _deleteConfirm.Kind, _deleteConfirm.Index);
                _deleteConfirm = null;
                await S.List.SoftReloadAsync();
                S.LookBook.ResetSeedSelection();
                S._message = "Picture deleted.";
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal void ChoosePictureRoute(PictureRoute route)
        {
            _pictureRoute = route;
            S._error = null;
            if (route == PictureRoute.Book)
                _ = S.LookBook.ToggleBookCandidateGalleryAsync();
            if (route == PictureRoute.Choose)
                S.LookBook._showBookCandidateGallery = false;
            S.StateHasChanged();
        }


        /// <summary>Book path: ensure selected plates are seeds, then generate 3 looks.</summary>
        internal async Task StartBookGuidedGenerateAsync()
        {
            if (S.List._selected is null) return;
            if (S.LookBook._selectedBookCandidatePaths.Count > 0)
            {
                var ok = await S.LookBook.EnsureGalleryBookSelectionAppliedAsync();
                if (!ok) return;
            }
            if (!S.List._selected.BookRefs.Any(b => b.Exists) && S.LookBook.SelectedSeedCount == 0)
            {
                S._error = "Select at least one book picture first.";
                return;
            }
            S.LookBook._seedOrder.Clear();
            S.LookBook.AddBookRefsToSeedOrder();
            await StartRegenerateAsync();
        }


        internal void BackToSource()
        {
            CloseLookZoom();
            ResetCompare();
            _mode = Mode.PickSource;
            if (S.List._selected?.HasPreferred == true)
                _pictureRoute = PictureRoute.Choose;
        }


        internal void ResetCompare()
        {
            _allCandidates = new();
            CloseLookZoom();
        }


        internal async Task StartRegenerateAsync()
        {
            if (S.List._selected is null) return;

            // Gallery checkmarks are the intended seeds — do not require a separate "Use for generation"
            // click, and do not mix in preferred/variants the operator did not rank as tiles.
            if (S.LookBook._selectedBookCandidatePaths.Count > 0)
            {
                var prepared = await S.LookBook.EnsureGalleryBookSelectionAppliedAsync();
                if (!prepared)
                    return;
            }

            if (S.LookBook.SelectedSeedCount == 0
                && string.IsNullOrWhiteSpace(S.LookEdit._editDescription)
                && string.IsNullOrWhiteSpace(S.LookEdit._imageEditInstruction))
            {
                S._error = "Select book pictures (or another reference), enter a description, or type a face tweak.";
                return;
            }

            var maxSend = S.LookBook.ApiMaxSeedRefs;
            var sendOrder = S.LookBook._seedOrder.Take(maxSend).ToList();
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

            // First gen / full regen: 3 options + AI pick. Voice/text plate tweak: one edit, lock immediately.
            var hasImageEdit = !string.IsNullOrWhiteSpace(S.LookEdit._imageEditInstruction)
                               && S.List.PreferredImageUrl is { Length: > 0 };
            var descForGen = hasImageEdit
                ? BuildImageEditPrompt(S.LookEdit._editDescription, S.LookEdit._editVisualLock, S.LookEdit._imageEditInstruction)
                : S.LookEdit._editDescription;
            await StartGenerateCoreAsync(new StartCharacterVariantsRequest
            {
                ProjectId = S._projectId,
                CharKey = S.List._selected.Key,
                Count = hasImageEdit ? 1 : 3,
                // Voice/text image edit always anchors on the preferred plate.
                SeedMode = hasImageEdit
                    ? "preferred_only"
                    : (S.LookBook.SelectedSeedCount == 0 ? "none" : "explicit"),
                IncludePreferred = hasImageEdit || includePref,
                IncludeLockedRef = hasImageEdit || includePref,
                BookRefIndices = hasImageEdit ? new List<int>() : books,
                VariantIndices = hasImageEdit ? new List<int>() : variants,
                SeedOrderKeys = hasImageEdit ? new List<string> { "p" } : sendOrder,
                MaxRefs = hasImageEdit ? 1 : maxSend,
                DescriptionOverride = descForGen,
                VisualLockOverride = S.LookEdit._editVisualLock,
                PersistDescription = !hasImageEdit, // don't overwrite seed with ephemeral edit instruction
                AutoLockBest = true,
                IterativeEdit = hasImageEdit,
            });
            if (hasImageEdit)
                S.LookEdit._imageEditInstruction = "";
        }

        /// <summary>Prompt for Grok image edit: keep identity, apply spoken/typed change.</summary>
        internal static string BuildImageEditPrompt(string? description, string? visualLock, string instruction)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Edit this character reference image. Keep the same person, face identity, and era. ");
            sb.Append("Change only what the instruction asks. ");
            sb.Append("Instruction: ").Append(instruction.Trim()).Append('.');
            if (!string.IsNullOrWhiteSpace(visualLock))
                sb.Append(" Visual lock: ").Append(visualLock.Trim());
            if (!string.IsNullOrWhiteSpace(description))
                sb.Append(" Base description: ").Append(description.Trim());
            return sb.ToString();
        }


        internal async Task StartGenerateCoreAsync(StartCharacterVariantsRequest req)
        {
            if (S.List._selected is null) return;
            S._busy = true;
            S._error = null;
            S._message = null;
            // Reset progress UI immediately so a prior 3/3 bar never carries over
            var total = req.Count > 0 ? req.Count : 3;
            S.Jobs._job = new JobSnapshot
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
                    S.Jobs._job = j;
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                _mode = Mode.PickSource;
                S.Jobs._job = null;
            }
            finally { S._busy = false; }
        }


        internal void BeginCompareFromVariants()
        {
            if (S.List._selected is null)
            {
                _mode = Mode.PickSource;
                return;
            }

            var vars = S.List._selected.Variants.Where(v => v.Exists).OrderBy(v => v.Index).ToList();
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
                Url = S.CacheBust(S.Engine.CharacterVariantUrl(S._projectId, S.List._selected.Key, v.Index ?? 1)),
            }).ToList();

            _mode = Mode.Compare;
            S.LookEdit._panelPictureOpen = true;
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
            if (S.List._selected is null) return;
            // Remember choice so a cast-list switch can finish the save if this call is in flight.
            _pendingLockCandidate = c;
            _chosenCandidateKey = CandidateKey(c);
            var charKey = S.List._selected.Key;
            var display = S.List._selected.DisplayName;
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
                await S.List.SoftReloadAsync();
                await S.RefreshNavReadinessAsync();
                // Stay on this character; show the preferred look (do not wipe list state for others).
                ResetCompare();
                _mode = Mode.PickSource;
                _pictureRoute = PictureRoute.Choose;
                S.List.ApplyPanelsForSelected();
                S.LookBook.ResetSeedSelection();
                if (S.List._selected is not null)
                {
                    foreach (var v in S.List._selected.Variants.Where(x => x.Exists))
                    {
                        var key = $"v{v.Index ?? 0}";
                        if (!S.LookBook._seedOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                            S.LookBook._seedOrder.Add(key);
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


        internal async Task UnlockAsync()
        {
            if (S.List._selected is null) return;
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.UnlockCharacterAsync(S._projectId, S.List._selected.Key);
                S._message = $"Unlocked {S.List._selected.DisplayName}";
                await S.List.LoadAsync();
                ResetCompare();
                _mode = Mode.PickSource;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task OnUploadRefAsync(InputFileChangeEventArgs e)
        {
            if (S.List._selected is null || S.List._selected.VoiceOnly || S.List._selected.IsGroup) return;
            var file = e.File;
            if (file is null) return;

            // Capture identity before any re-render; buffer bytes while InputFile is still mounted.
            var charKey = S.List._selected.Key;
            var display = S.List._selected.DisplayName;
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
                await S.List.SoftReloadAsync();
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
