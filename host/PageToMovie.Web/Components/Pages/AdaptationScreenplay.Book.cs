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
                        S.Editor._text = await S.Js.InvokeAsync<string>("fountainEditor.getValue", ScreenplayEditor.EditorId) ?? S.Editor._text;
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
            S.ProgressIndex = 0;
            S.ProgressTotal = 10;
            await S.InvokeAsync(S.StateHasChanged);
            try
            {
                await S.EnsureHubAsync();
                await S.Engine.StartBookImportAsync(
                    S.ProjectId,
                    skipPrepare: true,
                    forceExtract: false,
                    forceVision: false,
                    autoVision: false,
                    model: S.Model);
                var jobs0 = await S.Engine.GetJobAsync();
                S.Job = jobs0?.Job;
                S.AbsorbProgressFromSnapshot(S.Job ?? new PageToMovie.Core.Models.JobSnapshot());
                // Shared poller re-renders soft progress while the long draft call is quiet.
                S.StartJobPolling();
                // Wait until draft job finishes (poller keeps Job + bar live)
                for (var i = 0; i < 3600; i++)
                {
                    var snap = S.Job;
                    if (snap is not null)
                    {
                        S.BusyMessage = AdaptationPageBase.OperatorJobRunningMessage(snap);
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
                    await S.Js.InvokeVoidAsync("fountainEditor.setValue", ScreenplayEditor.EditorId, S.Editor._text);
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

    // ── Method forwarders (ScreenplayBook) ──
    private void CloseBookModal() => Book.CloseBookModal();
    private Task CreateFromBookAsync() => Book.CreateFromBookAsync();

    [JSInvokable]
    public Task OnSceneSelected(int line, int sceneIndex, string heading, bool openBookModal = false) =>
        Book.OnSceneSelected(line, sceneIndex, heading, openBookModal);
}
