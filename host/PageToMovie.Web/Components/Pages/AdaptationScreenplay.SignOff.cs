using Microsoft.JSInterop;
using PageToMovie.Core.Models;

using PageToMovie.Core.Utils;
namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    /// <summary>Warnings + approve / sign-off for the screenplay page.</summary>
    public sealed class ScreenplaySignOff
    {
        private readonly AdaptationScreenplay S;
        public ScreenplaySignOff(AdaptationScreenplay host) => S = host;

        internal List<string> _signOffWarnings = new();
        internal ScreenplayStatus? _screenplayStatus;

        /// <summary>User has approved at least once (signed hash recorded).</summary>
        internal bool WasEverSigned =>
            (_screenplayStatus?.Signed ?? false)
            || !string.IsNullOrWhiteSpace(_screenplayStatus?.SignedHash);

        /// <summary>Approved and not edited since sign-off.</summary>
        internal bool IsApprovedClean =>
            (_screenplayStatus?.Signed ?? false)
            && !_screenplayStatus.Dirty
            && !S.Save._dirtyLocal;

        /// <summary>Had an approval but draft changed (or local unsaved edits after sign-off).</summary>
        internal bool NeedsReapprove =>
            WasEverSigned
            && ((_screenplayStatus?.Dirty ?? false) || S.Save._dirtyLocal)
            && !string.IsNullOrWhiteSpace(S.Editor._text);

        internal string StatusTitle
        {
            get
            {
                if (_screenplayStatus?.Title is { Length: > 0 } t)
                    return t;
                return string.IsNullOrWhiteSpace(S.Editor._text) ? "" : "Untitled";
            }
        }

        internal void MapWarnings(string[]? codes)
        {
            _signOffWarnings = new List<string>();
            if (codes is null) return;
            foreach (var c in codes)
            {
                switch (c)
                {
                    case "empty":
                        _signOffWarnings.Add("The screenplay is empty.");
                        break;
                    case "no_scenes":
                        _signOffWarnings.Add("No scene headings found — add lines like INT. ROOM - DAY.");
                        break;
                    case "very_short":
                        _signOffWarnings.Add("This draft is very short.");
                        break;
                }
            }
        }

        internal void UpdateWarningsFromText(string text)
        {
            var codes = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) codes.Add("empty");
            else
            {
                if (text.Trim().Length < 40) codes.Add("very_short");
                // crude scene check until JS reports
                if (!CommonRegex.IsMatch(text, @"(?im)^(INT|EXT|EST|I/E|\.)"))
                    codes.Add("no_scenes");
            }
            MapWarnings(codes.ToArray());
        }

        internal async Task SignOffAsync()
        {
            await S.Editor.SyncTextFromEditorAsync();

            // Soft gate: confirm if warnings
            if (string.IsNullOrWhiteSpace(S.Editor._text))
            {
                S.Error = "Add some screenplay text before approving.";
                return;
            }

            // Structured editor warnings come from local text analysis.
            UpdateWarningsFromText(S.Editor._text);

            if (_signOffWarnings.Count > 0)
            {
                // Still allow, but set message so user sees why
                S.Message = "Approving with notes: " + string.Join(" ", _signOffWarnings);
            }

            S.Busy = true;
            S.BusyMessage = "Saving…";
            S.Error = null;
            // Paint progress before the long sign-off call (includes cast build on the server)
            await S.InvokeAsync(S.StateHasChanged);

            // Continue always persists first (merged Save + approve).
            if (S.Save._dirtyLocal)
            {
                try { await S.Save.SaveDraftAsync(manual: false); }
                catch { /* sign-off still sends full text */ }
                S.Busy = true;
                S.BusyMessage = "Approving…";
                await S.InvokeAsync(S.StateHasChanged);
            }
            else
            {
                S.BusyMessage = "Approving…";
            }

            try
            {
                // Structured editor has no CodeMirror read-only toggle.

                // Server may spend many seconds extracting cast after approve
                S.BusyMessage = "Approving and preparing cast…";
                await S.InvokeAsync(S.StateHasChanged);

                var result = await S.Engine.SignOffScreenplayAsync(S.ProjectId, S.Editor._text);
                S.Editor._loadedText = S.Editor._text;
                S.Save._dirtyLocal = false;
                S.Save._lastSavedUtc = DateTime.UtcNow;
                _screenplayStatus = result?.Screenplay;
                if (result?.Adaptation is not null)
                    S.Status = result.Adaptation;
                else
                    await S.SoftLoadAsync();
                S.Message = result?.Message ?? "Screenplay approved";
                if (result?.Ok ?? false)
                {
                    S.BusyMessage = "Opening estimate…";
                    await S.InvokeAsync(S.StateHasChanged);
                    // DecisionCard: cost + duration → Generate | Edit (not cast-first maze)
                    S.Nav.NavigateTo("cost");
                }
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
            finally
            {
                S.Busy = false;
                S.BusyMessage = null;
                // no-op: structured editor does not use fountainEditor JS
            }
        }
    }

}
