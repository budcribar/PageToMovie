using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    /// <summary>Editor text / JS lifecycle for the screenplay page.</summary>
    public sealed class ScreenplayEditor
    {
        private readonly AdaptationScreenplay S;
        public ScreenplayEditor(AdaptationScreenplay host) => S = host;

        internal const string EditorId = "screenplay-main";

        internal string _text = "";
        internal string _loadedText = "";
        internal bool _editorReady;
        internal bool _jsInitStarted;
        internal bool _editorDataLoaded;
        internal bool _copied;
        internal int _sceneCount;

        internal ElementReference _editorHost;
        internal ElementReference _previewEl;
        internal ElementReference _scenesEl;
        internal DotNetObjectReference<AdaptationScreenplay>? _dotNetRef;

        internal bool CanEdit => _editorReady && !S.Busy && !S.Jobs.JobRunning;

        /// <summary>Copy the full screenplay to the clipboard — read action, works even while read-only.</summary>
        internal async Task CopyScreenplayAsync()
        {
            string text;
            try { text = await S.Js.InvokeAsync<string>("fountainEditor.getValue", EditorId); }
            catch { text = _text; }
            if (string.IsNullOrEmpty(text)) text = _text;

            try
            {
                await S.Js.InvokeVoidAsync("PageToMovieExport.copyTextAsync", text ?? "");
                _copied = true;
                S.StateHasChanged();
                await Task.Delay(1500);
                _copied = false;
                S.StateHasChanged();
            }
            catch { /* clipboard may be blocked (permissions / non-secure context) — non-fatal */ }
        }

        internal async Task LoadEditorDataAsync()
        {
            try
            {
                var doc = await S.Engine.GetScreenplayAsync(S.ProjectId);
                _text = doc?.Text ?? "";
                _loadedText = _text;
                S.Save._dirtyLocal = false;
                S.SignOff._screenplayStatus = doc?.Screenplay ?? S.Status?.Screenplay;
                _sceneCount = S.SignOff._screenplayStatus?.SceneHeadingCount ?? 0;
                if (doc?.Adaptation is not null)
                    S.Status = doc.Adaptation;
                _editorDataLoaded = true;
                S.SignOff.UpdateWarningsFromText(_text);

                if (_editorReady)
                {
                    await S.Js.InvokeVoidAsync("fountainEditor.setValue", EditorId, _text);
                }
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
                _editorDataLoaded = true; // still try to init empty editor
            }
        }

        /// <summary>After host LoadAsync: push text into an already-ready editor, or allow re-init.</summary>
        internal async Task OnHostLoadAsync()
        {
            if (_editorReady)
            {
                await S.Js.InvokeVoidAsync("fountainEditor.setValue", EditorId, _text);
                await S.Js.InvokeVoidAsync("fountainEditor.refresh", EditorId);
                if (!S.Busy && !S.Jobs.JobRunning)
                    await S.Js.InvokeVoidAsync("fountainEditor.setReadOnly", EditorId, false);
            }
            else
            {
                _jsInitStarted = false; // allow re-init after project switch
                S.StateHasChanged();
            }
        }

        internal async Task TryInitEditorAsync()
        {
            if (_jsInitStarted || string.IsNullOrEmpty(S.ProjectId)) return;
            // Wait until we have loaded draft at least once
            if (!_editorDataLoaded) return;

            _jsInitStarted = true;
            try
            {
                _dotNetRef = DotNetObjectReference.Create(S);
                await S.Js.InvokeVoidAsync(
                    "fountainEditor.init",
                    EditorId,
                    _editorHost,
                    _text,
                    _dotNetRef,
                    _previewEl,
                    _scenesEl,
                    S.Busy || S.Jobs.JobRunning);
                _editorReady = true;
                if (!S.Busy && !S.Jobs.JobRunning)
                    await S.Js.InvokeVoidAsync("fountainEditor.setReadOnly", EditorId, false);
                S.StateHasChanged();
            }
            catch (Exception ex)
            {
                S.Error = "Could not start the editor: " + ex.Message;
                _jsInitStarted = false;
            }
        }

        internal Task OnEditorChanged(string text, string[] warnings, int sceneCount)
        {
            _text = text ?? "";
            _sceneCount = sceneCount;
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.SignOff.MapWarnings(warnings);
            S.Save.ScheduleAutosave();
            return S.InvokeAsync(S.StateHasChanged);
        }

        internal async Task InsertAsync(string snippet)
        {
            if (!_editorReady) return;
            try
            {
                await S.Js.InvokeVoidAsync("fountainEditor.insertAtCursor", EditorId, snippet);
                _text = await S.Js.InvokeAsync<string>("fountainEditor.getValue", EditorId);
                S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
                S.Save.ScheduleAutosave();
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
        }

        /// <summary>
        /// Title:/Author: only parse as title-page metadata near the top of the file, so unlike every
        /// other Advanced helper (which inserts at the cursor) this always targets the document start —
        /// see ProjectStore.ReadScreenplayTitle/ReadScreenplayAuthor, which scan the same first-30-lines
        /// window for the credits card. <paramref name="afterKey"/> anchors a new line after an existing
        /// one instead of at line 0 (Author passes "Title" so it lands below Title, not above it).
        /// </summary>
        internal async Task InsertTitleFieldAsync(string key, string? afterKey = null)
        {
            if (!_editorReady) return;
            try
            {
                await S.Js.InvokeVoidAsync("fountainEditor.insertTitleField", EditorId, key, afterKey);
                _text = await S.Js.InvokeAsync<string>("fountainEditor.getValue", EditorId);
                S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
                S.Save.ScheduleAutosave();
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
        }

        internal async Task SyncTextFromEditorAsync()
        {
            if (!_editorReady) return;
            try
            {
                _text = await S.Js.InvokeAsync<string>("fountainEditor.getValue", EditorId) ?? _text;
            }
            catch { /* keep local */ }
        }

        internal async Task DisposeEditorAsync()
        {
            try
            {
                if (_jsInitStarted)
                    await S.Js.InvokeVoidAsync("fountainEditor.dispose", EditorId);
            }
            catch { /* ignore */ }
            _dotNetRef?.Dispose();
            _dotNetRef = null;
        }
    }


}
