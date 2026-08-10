using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    /// <summary>Book modal + create-from-book for the screenplay page.</summary>
    public sealed class ScreenplayBook
    {
        private readonly AdaptationScreenplay S;
        public ScreenplayBook(AdaptationScreenplay host) => S = host;

        internal bool _showBookModal;
        internal bool _bookWindowNeedsDragBind;
        internal bool _bookLoading;
        internal int _bookRequestGen;
        internal BookContextDto? _bookContext;
        internal ElementReference _bookWindowEl;

        internal async Task AfterRenderDragBindAsync()
        {
            if (_bookWindowNeedsDragBind && _showBookModal)
            {
                _bookWindowNeedsDragBind = false;
                try
                {
                    await S.Js.InvokeVoidAsync("fountainEditor.bindBookWindowDrag", _bookWindowEl);
                }
                catch { /* optional */ }
            }
        }

        /// <param name="openBookModal">True when user clicked Book link (popup); false = jump only.</param>
        internal async Task OnSceneSelected(int line, int sceneIndex, string heading, bool openBookModal = false)
        {
            if (!openBookModal)
                return; // Scene click only navigates in the editor (handled in JS)

            _showBookModal = true;
            _bookWindowNeedsDragBind = true;
            _bookLoading = true;
            await S.InvokeAsync(S.StateHasChanged);

            var gen = ++_bookRequestGen;
            try
            {
                if (S.Editor._editorReady)
                {
                    try
                    {
                        await S.Editor.SyncTextFromEditorAsync();
                    }
                    catch { /* keep */ }
                }

                var ctx = await S.Engine.GetBookContextAsync(
                    S.ProjectId,
                    sceneIndex: Math.Max(1, sceneIndex),
                    line: line,
                    heading: heading,
                    fountainText: S.Editor._text);
                if (gen != _bookRequestGen) return;
                _bookContext = ctx;
            }
            catch (Exception ex)
            {
                if (gen == _bookRequestGen)
                {
                    _bookContext = new BookContextDto
                    {
                        Ok = false,
                        HasBook = S.Status?.Book.BookTextExists == true,
                        Heading = heading,
                        SceneIndex = sceneIndex,
                        Excerpt = "",
                        Message = ex.Message,
                    };
                }
            }
            finally
            {
                if (gen == _bookRequestGen)
                {
                    _bookLoading = false;
                    await S.InvokeAsync(S.StateHasChanged);
                }
            }
        }

        internal void CloseBookModal()
        {
            _showBookModal = false;
        }

        /// <summary>
        /// Book text exists but draft is missing, empty, or a stub — surface Create/Rewrite CTA.
        /// </summary>
        internal bool NeedsDraftFromBook
        {
            get
            {
                if (S.Status?.Book.BookTextExists != true) return false;
                if (string.IsNullOrWhiteSpace(S.Editor._text)) return true;
                if (S.SignOff._signOffWarnings.Any(w =>
                        w.Contains("empty", StringComparison.OrdinalIgnoreCase)
                        || w.Contains("very short", StringComparison.OrdinalIgnoreCase)))
                    return true;
                // Structured stub: single generic LOCATION scene with placeholder beat.
                if (S.Editor._sceneCount <= 1
                    && S.Editor._text.Contains("What we see", StringComparison.OrdinalIgnoreCase)
                    && S.Editor._text.Contains("LOCATION", StringComparison.OrdinalIgnoreCase)
                    && S.Editor._text.Length < 800)
                    return true;
                if (S.SignOff._screenplayStatus?.DraftExists != true
                    && S.Editor._text.Trim().Length < 200)
                    return true;
                return false;
            }
        }

        internal async Task CreateFromBookAsync()
        {
            if (S.Save._dirtyLocal && !string.IsNullOrWhiteSpace(S.Editor._text))
            {
                try { await S.Save.SaveDraftAsync(manual: false); } catch { /* ignore */ }
            }

            // Prior Cancel left ClientCancelRequested=true which blocked the poller entirely —
            // UI froze at the last progress (often 6/10) with no updates.
            S.Jobs.ResetClientCancel();
            // Best-effort: free Stage lock if a previous Odyssey adapt is still "running".
            if (S.Jobs.JobRunning)
                _ = await S.Engine.TryCancelJobAsync();

            S.Busy = true;
            S.BusyMessage = "Writing screenplay…";
            S.Error = null;
            S.Message = null;
            S.Jobs.ProgressIndex = 0;
            S.Jobs.ProgressTotal = 10;
            await S.InvokeAsync(S.StateHasChanged);
            try
            {
                await S.Jobs.EnsureHubAsync();
                var started = await S.Engine.StartBookImportAsync(
                    S.ProjectId,
                    skipPrepare: true,
                    forceExtract: false,
                    forceVision: false,
                    autoVision: false,
                    model: S.Pipeline.Model);
                if (started is not null)
                {
                    S.Jobs.Job = started;
                    S.Jobs.AbsorbProgressFromSnapshot(started);
                }
                else
                {
                    var jobs0 = await S.Engine.GetJobAsync();
                    S.Jobs.Job = jobs0?.Job;
                    if (S.Jobs.Job is not null)
                        S.Jobs.AbsorbProgressFromSnapshot(S.Jobs.Job);
                }

                var trackId = S.Jobs.Job?.JobId;
                S.Jobs.StartJobPolling();

                var finishedOk = false;
                var goneHits = 0;
                // Long novels: multi-chunk / single-pass can run 30–90+ minutes
                for (var i = 0; i < 5400; i++)
                {
                    if (S.Jobs.ClientCancelRequested)
                    {
                        S.Error = null;
                        S.Message = "Cancelled. You can try Create draft from book again.";
                        return;
                    }

                    JobSnapshot? snap = S.Jobs.Job;

                    // Always re-check the tracked job on the server (don't trust a frozen local 6/10).
                    if (!string.IsNullOrWhiteSpace(trackId))
                    {
                        try
                        {
                            using var idCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                            var live = await S.Engine.TryGetJobAsync(trackId, idCts.Token);
                            if (live is not null)
                            {
                                snap = live;
                                goneHits = 0;
                            }
                            else
                            {
                                // 404 or empty — job not in store (restart wiped in-memory jobs).
                                // Confirm via list: if nothing running for us, count as gone.
                                try
                                {
                                    using var listCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                                    var list = await S.Engine.GetJobAsync(listCts.Token);
                                    var primary = list?.Job;
                                    if (primary is null || primary.IsFinished
                                        || !string.Equals(primary.JobId, trackId, StringComparison.OrdinalIgnoreCase))
                                        goneHits++;
                                    else
                                    {
                                        snap = primary;
                                        goneHits = 0;
                                    }
                                }
                                catch
                                {
                                    // API 502 — do not count as gone
                                }
                            }
                        }
                        catch
                        {
                            // network
                        }
                    }

                    if (goneHits >= 3)
                    {
                        S.Jobs.MarkJobLostOnServer(
                            "The write job is no longer on the server (Admin shows no active jobs — often a restart mid-call). "
                            + "Try Create draft from book again when the host is stable.");
                        return;
                    }

                    if (snap is not null)
                    {
                        S.Jobs.Job = snap;
                        S.Jobs.AbsorbProgressFromSnapshot(snap);
                        S.BusyMessage = AdaptationPageBase.AdaptationJobs.OperatorJobRunningMessage(snap);
                        await S.InvokeAsync(S.StateHasChanged);
                        var st = snap.Status ?? "";
                        if (st is "error" or "cancelled")
                        {
                            S.Error = snap.Error ?? snap.Message ?? "Could not create draft. Try again.";
                            return;
                        }
                        if ((st == "done" || st == "partial") &&
                            (string.IsNullOrEmpty(snap.Kind) ||
                             snap.Kind is "book_import" or "book_prepare" or "stage1"))
                        {
                            finishedOk = true;
                            break;
                        }
                    }
                    await Task.Delay(1000);
                }

                if (!finishedOk)
                {
                    S.Error =
                        "Still no finished draft after a long wait. Check Admin → Jobs. "
                        + "If nothing is running, Cancel here and try Create draft from book again.";
                    return;
                }

                S.Message = "Draft created from book";
                await S.SoftLoadAsync();
                await S.Editor.LoadEditorDataAsync();
                if (S.Editor._editorReady)
                    S.Editor.HydrateModelFromText();
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
            finally
            {
                S.Busy = false;
                S.BusyMessage = null;
            }
        }
    }


}
