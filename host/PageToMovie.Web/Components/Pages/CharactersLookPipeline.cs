using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Utils;
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

        /// <summary>
        /// The card whose save is actually in flight right now. Deliberately not
        /// <see cref="_chosenCandidateKey"/>, which also marks the chosen card and survives a
        /// failed save — a spinner keyed on that would come back on an unrelated later operation.
        /// Set and cleared in one try/finally so it cannot outlive the call.
        /// </summary>
        private string? _savingCandidateKey;

        internal PendingDelete? _deleteConfirm;

        internal long _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Last generate was a one-look face tweak (not generate-3).</summary>
        internal bool _lastGenerateWasIterative;

        internal Mode _mode = Mode.PickSource;

        internal int GeneratingLookCount =>
            S.Jobs._job is { Total: > 0 } job
                ? job.Total
                : CharacterLookEdit.VariantCount(_lastGenerateWasIterative);

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


        internal Task StartTweakFromCoachAsync(string instruction)
        {
            if (!string.IsNullOrWhiteSpace(instruction))
                S.LookEdit._imageEditInstruction = instruction.Trim();
            return StartRegenerateAsync();
        }

        internal async Task StartRegenerateAsync()
        {
            if (S.List._selected is null) return;
            if (!await TryApplyGallerySeedsAsync()) return;
            if (MissingRegenInputs())
            {
                S._error = "Select book pictures (or another reference), enter a description, or type a face tweak.";
                return;
            }

            var maxSend = S.LookBook.ApiMaxSeedRefs;
            var sendOrder = S.LookBook._seedOrder.Take(maxSend).ToList();
            var (includePref, variants, books) = PartitionSeedOrder(sendOrder);
            var hasImageEdit = !string.IsNullOrWhiteSpace(S.LookEdit._imageEditInstruction)
                               && S.List.PreferredImageUrl is { Length: > 0 };
            await StartGenerateCoreAsync(BuildRegenRequest(
                hasImageEdit, includePref, variants, books, sendOrder, maxSend));
            // Keep the instruction if start failed so a retry still has the tweak text.
            if (hasImageEdit && S._error is null)
                S.LookEdit._imageEditInstruction = "";
        }

        private async Task<bool> TryApplyGallerySeedsAsync()
        {
            // Gallery checkmarks are the intended seeds — do not require a separate "Use for generation"
            // click, and do not mix in preferred/variants the operator did not rank as tiles.
            if (S.LookBook._selectedBookCandidatePaths.Count == 0) return true;
            return await S.LookBook.EnsureGalleryBookSelectionAppliedAsync();
        }

        private bool MissingRegenInputs() =>
            S.LookBook.SelectedSeedCount == 0
            && string.IsNullOrWhiteSpace(S.LookEdit._editDescription)
            && string.IsNullOrWhiteSpace(S.LookEdit._imageEditInstruction);

        private static (bool IncludePref, List<int> Variants, List<int> Books) PartitionSeedOrder(
            List<string> sendOrder)
        {
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
            return (includePref, variants, books);
        }

        internal StartCharacterVariantsRequest BuildRegenRequest(
            bool hasImageEdit,
            bool includePref,
            List<int> variants,
            List<int> books,
            List<string> sendOrder,
            int maxSend)
            => BuildRegenRequest(new RegenRequestArgs
            {
                ProjectId = S._projectId,
                CharKey = S.List._selected!.Key,
                HasImageEdit = hasImageEdit,
                IncludePref = includePref,
                Variants = variants,
                Books = books,
                SendOrder = sendOrder,
                MaxSend = maxSend,
                Description = S.LookEdit._editDescription,
                VisualLock = S.LookEdit._editVisualLock,
                ImageEditInstruction = S.LookEdit._imageEditInstruction,
                SelectedSeedCount = S.LookBook.SelectedSeedCount,
            });

        /// <summary>
        /// Inputs for <see cref="BuildRegenRequest(RegenRequestArgs)"/>.
        /// Object-initializer shape so generate vs tweak fields stay grouped.
        /// </summary>
        internal sealed class RegenRequestArgs
        {
            public required string ProjectId { get; init; }
            public required string CharKey { get; init; }
            public bool HasImageEdit { get; init; }
            public bool IncludePref { get; init; }
            public List<int> Variants { get; init; } = new();
            public List<int> Books { get; init; } = new();
            public List<string> SendOrder { get; init; } = new();
            public int MaxSend { get; init; }
            public string? Description { get; init; }
            public string? VisualLock { get; init; }
            public string? ImageEditInstruction { get; init; }
            public int SelectedSeedCount { get; init; }
        }

        /// <summary>Request shape for generate-3 vs iterative tweak-1. Testable without a page host.</summary>
        internal static StartCharacterVariantsRequest BuildRegenRequest(RegenRequestArgs args)
        {
            string seedMode;
            if (args.HasImageEdit) seedMode = "preferred_only";
            else if (args.SelectedSeedCount == 0) seedMode = "none";
            else seedMode = "explicit";
            return new StartCharacterVariantsRequest
            {
                ProjectId = args.ProjectId,
                CharKey = args.CharKey,
                Count = CharacterLookEdit.VariantCount(args.HasImageEdit),
                // Voice/text image edit always anchors on the preferred plate.
                SeedMode = seedMode,
                IncludePreferred = args.HasImageEdit || args.IncludePref,
                IncludeLockedRef = args.HasImageEdit || args.IncludePref,
                BookRefIndices = args.HasImageEdit ? new List<int>() : args.Books,
                VariantIndices = args.HasImageEdit ? new List<int>() : args.Variants,
                SeedOrderKeys = args.HasImageEdit ? new List<string> { "p" } : args.SendOrder,
                MaxRefs = args.HasImageEdit ? 1 : args.MaxSend,
                DescriptionOverride = args.Description,
                VisualLockOverride = args.VisualLock,
                ImageEditInstruction = args.HasImageEdit ? args.ImageEditInstruction : null,
                PersistDescription = !args.HasImageEdit, // merge instruction into look text after a successful tweak
                AutoLockBest = CharacterLookEdit.ShouldAutoLockBest(args.HasImageEdit),
                IterativeEdit = args.HasImageEdit,
            };
        }

        /// <summary>Prompt for image edit: keep identity, apply spoken/typed change. Instruction wins.</summary>
        internal static string BuildImageEditPrompt(string? description, string? visualLock, string instruction)
            => CharacterLookEdit.BuildImageEditPrompt(description, visualLock, instruction);


        internal async Task StartGenerateCoreAsync(StartCharacterVariantsRequest req)
        {
            if (S.List._selected is null) return;
            S._busy = true;
            S._error = null;
            S._message = null;
            // Reset progress UI immediately so a prior 3/3 bar never carries over
            _lastGenerateWasIterative = req.IterativeEdit;
            var total = req.Count > 0 ? req.Count : CharacterLookEdit.VariantCount(req.IterativeEdit);
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
                Label = $"Look #{v.Index}",
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


        /// <summary>
        /// True for the one card whose save is in flight, so the spinner lands on the picture the
        /// operator clicked rather than on all three at once.
        /// </summary>
        internal bool IsSavingCandidate(Candidate c) =>
            _savingCandidateKey is { Length: > 0 }
            && string.Equals(_savingCandidateKey, CandidateKey(c), StringComparison.OrdinalIgnoreCase);

        internal async Task LockCandidateAsync(Candidate c, bool overrideStyle = false, string? overrideReason = null)
        {
            if (S.List._selected is null) return;
            // Remember choice so a cast-list switch can finish the save if this call is in flight.
            _pendingLockCandidate = c;
            _chosenCandidateKey = CandidateKey(c);
            var charKey = S.List._selected.Key;
            _savingCandidateKey = CandidateKey(c);
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
            finally
            {
                _savingCandidateKey = null;
                S._busy = false;
            }
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


        /// <summary>Operator kept a picture flagged as drawn from an out-of-date reference.</summary>
        internal async Task KeepStaleLookAsync()
        {
            if (S.List._selected is null) return;
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.KeepCharacterLookAsync(S._projectId, S.List._selected.Key);
                await S.List.LoadAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
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
            var fileName = file.Name;
            byte[] bytes;
            try
            {
                const long max = 8_000_000;
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
