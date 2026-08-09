using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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

        internal async Task CreateFromBookAsync()
        {
            if (S.Save._dirtyLocal && !string.IsNullOrWhiteSpace(S.Editor._text))
            {
                try { await S.Save.SaveDraftAsync(manual: false); } catch { /* ignore */ }
            }

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
                await S.Engine.StartBookImportAsync(
                    S.ProjectId,
                    skipPrepare: true,
                    forceExtract: false,
                    forceVision: false,
                    autoVision: false,
                    model: S.Pipeline.Model);
                var jobs0 = await S.Engine.GetJobAsync();
                S.Jobs.Job = jobs0?.Job;
                S.Jobs.AbsorbProgressFromSnapshot(S.Jobs.Job ?? new PageToMovie.Core.Models.JobSnapshot());
                // Shared poller re-renders soft progress while the long draft call is quiet.
                S.Jobs.StartJobPolling();
                // Wait until draft job finishes (poller keeps Job + bar live)
                for (var i = 0; i < 3600; i++)
                {
                    var snap = S.Jobs.Job;
                    if (snap is not null)
                    {
                        S.BusyMessage = AdaptationPageBase.AdaptationJobs.OperatorJobRunningMessage(snap);
                        await S.InvokeAsync(S.StateHasChanged);
                        var st = snap.Status ?? "";
                        if (st is "error" or "cancelled")
                        {
                            S.Error = snap.Error ?? snap.Message ?? "Could not create draft. Try again.";
                            return;
                        }
                        if (st == "done" &&
                            (string.IsNullOrEmpty(snap.Kind) ||
                             snap.Kind is "book_import" or "book_prepare" or "stage1"))
                            break;
                    }
                    await Task.Delay(1000);
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
