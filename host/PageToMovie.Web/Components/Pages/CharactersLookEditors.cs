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
    /// <summary>Look text editors and debounced autosave for Characters.</summary>
    public sealed class CharactersLookEditors
    {
        private readonly Characters S;
        public CharactersLookEditors(Characters host) => S = host;

        internal string _editDescription = "";

        internal string _editVisualLock = "";

        /// <summary>Voice/text instruction for refining the preferred plate on next generate.</summary>
        internal string _imageEditInstruction = "";

        internal VisualMedium _editVisualMedium = VisualMedium.LiveAction;

        internal CancellationTokenSource? _lookSaveCts;

        internal string? _lookSaveHint;

        internal bool _panelPictureOpen = true;

        internal bool _panelVoiceOpen;

        /// <summary>Last loaded/saved look text — skip scrub API when editors match.</summary>
        internal string _savedLookDescription = "";

        internal string _savedLookVisualLock = "";

        internal VisualMedium _savedLookVisualMedium = VisualMedium.LiveAction;

        internal bool _savingLook;

        /// <summary>
        /// True when this character's look text is the pipeline's own invention rather than the
        /// story's — nothing in the book or screenplay described them, so words were written so a
        /// portrait could exist. Worth saying out loud: it is the difference between a detail the
        /// author chose and one nobody did, and only the operator can tell which they wanted.
        /// </summary>
        internal static bool LookWasSuggested(CharacterSummary? c) =>
            c is not null && !c.VoiceOnly && LookProvenanceTokens.IsInvented(c.LookProvenance);


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

        internal Task OnImageEditInstructionChanged(string value)
        {
            _imageEditInstruction = value ?? "";
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
                if (token.IsCancellationRequested || S.List._selected is null) return;
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
            if (S.List._selected is null) return;

            // Snapshot identity — never re-read S.List._selected after await for the POST.
            var charKey = S.List._selected.Key;
            if (LookTextUnchanged())
            {
                NotifyNoLookChanges(silent);
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
                    description: _editDescription ?? "",
                    visualLock: _editVisualLock ?? "",
                    scrubWithAi: !silent);
                await ApplyLookSaveResultAsync(charKey, silent, result);
            }
            catch (Exception ex)
            {
                HandleLookSaveError(silent, ex);
            }
            finally
            {
                if (!silent) S._busy = false;
                _savingLook = false;
            }
        }

        private bool LookTextUnchanged() =>
            string.Equals(_editDescription ?? "", _savedLookDescription, StringComparison.Ordinal) &&
            string.Equals(_editVisualLock ?? "", _savedLookVisualLock, StringComparison.Ordinal) &&
            _editVisualMedium == _savedLookVisualMedium;

        private void NotifyNoLookChanges(bool silent)
        {
            if (silent) return;
            S._error = null;
            S._message = "No look changes.";
        }

        private async Task ApplyLookSaveResultAsync(string charKey, bool silent, UpdateCharacterLookResult result)
        {
            var stillOnChar = string.Equals(S.List._selectedKey, charKey, StringComparison.OrdinalIgnoreCase);
            ApplyImmediateLookEdits(stillOnChar, silent, result);
            await S.List.SoftReloadAsync();
            ApplyReloadedLookEdits(charKey, silent, result);
        }

        private void ApplyImmediateLookEdits(bool stillOnChar, bool silent, UpdateCharacterLookResult result)
        {
            if (!stillOnChar || silent) return;
            if (!string.IsNullOrWhiteSpace(result.Description))
                _editDescription = result.Description;
            if (result.VisualLock is not null)
                _editVisualLock = result.VisualLock;
        }

        private void ApplyReloadedLookEdits(string charKey, bool silent, UpdateCharacterLookResult result)
        {
            if (!string.Equals(S.List._selectedKey, charKey, StringComparison.OrdinalIgnoreCase) ||
                S.List._selected is null)
                return;

            _editDescription = ReloadedLookDescription(silent, result);
            _editVisualLock = ReloadedLookVisualLock(silent, result);
            _savedLookDescription = _editDescription ?? "";
            _savedLookVisualLock = _editVisualLock ?? "";
            _savedLookVisualMedium = _editVisualMedium;
        }

        private string ReloadedLookDescription(bool silent, UpdateCharacterLookResult result)
        {
            if (!silent && !string.IsNullOrWhiteSpace(result.Description))
                return result.Description ?? "";
            if (silent)
                return _editDescription ?? "";
            return S.List._selected!.Description ?? _editDescription ?? "";
        }

        private string ReloadedLookVisualLock(bool silent, UpdateCharacterLookResult result)
        {
            if (!silent && result.VisualLock is not null)
                return result.VisualLock;
            if (!silent)
                return S.List._selected!.VisualLock ?? _editVisualLock ?? "";
            return _editVisualLock ?? "";
        }

        private void HandleLookSaveError(bool silent, Exception ex)
        {
            if (!silent)
            {
                S._error = ex.Message;
                S._message = null;
                return;
            }
            throw ex;
        }

    }
}
