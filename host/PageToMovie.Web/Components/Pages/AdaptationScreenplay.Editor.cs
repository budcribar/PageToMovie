using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    /// <summary>Structured screenplay editor + fountain text bridge for the screenplay page.</summary>
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

        /// <summary>Structured model driving the Screenplay Editor workbench.</summary>
        internal ScreenplayModel _model = new();

        // Kept for book modal / residual host references.
        internal ElementReference _editorHost;
        internal ElementReference _previewEl;
        internal ElementReference _scenesEl;
        internal DotNetObjectReference<AdaptationScreenplay>? _dotNetRef;

        internal bool CanEdit => _editorReady && !S.Busy && !S.Jobs.JobRunning;

        internal async Task CopyScreenplayAsync()
        {
            await SyncTextFromEditorAsync();
            var text = _text ?? "";
            try
            {
                await S.Js.InvokeVoidAsync("PageToMovieExport.copyTextAsync", text);
                _copied = true;
                S.StateHasChanged();
                await Task.Delay(1500);
                _copied = false;
                S.StateHasChanged();
            }
            catch { /* clipboard may be blocked */ }
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
                if (doc?.Adaptation is not null)
                    S.Status = doc.Adaptation;

                HydrateModelFromText();
                _editorReady = true;
                _editorDataLoaded = true;
                S.SignOff.UpdateWarningsFromText(_text);
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
                HydrateModelFromText();
                _editorReady = true;
                _editorDataLoaded = true;
            }
        }

        internal void HydrateModelFromText()
        {
            _model = FountainFormatter.Parse(_text ?? "");
            if (_model.Scenes.Count == 0)
            {
                _model.Scenes.Add(new ScreenplayScene
                {
                    SceneNumber = 1,
                    Environment = "INT.",
                    Location = "LOCATION",
                    TimeOfDay = "DAY",
                    IsSelected = true,
                    Beats =
                    {
                        new ScreenplayBeat { Type = BeatType.Action, Text = "" }
                    }
                });
            }
            else if (!_model.Scenes.Any(s => s.IsSelected))
            {
                _model.Scenes[0].IsSelected = true;
            }
            _sceneCount = _model.Scenes.Count;
        }

        internal Task OnStructuredModelChanged(ScreenplayModel model)
        {
            _model = model ?? new ScreenplayModel();
            _text = FountainFormatter.ToFountain(_model);
            _sceneCount = _model.Scenes.Count;
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.SignOff.UpdateWarningsFromText(_text);
            S.Save.ScheduleAutosave();
            return S.InvokeAsync(S.StateHasChanged);
        }

        internal Task OnHostLoadAsync()
        {
            HydrateModelFromText();
            _editorReady = true;
            S.StateHasChanged();
            return Task.CompletedTask;
        }

        internal Task TryInitEditorAsync()
        {
            if (!_editorDataLoaded) return Task.CompletedTask;
            _editorReady = true;
            _jsInitStarted = true;
            return Task.CompletedTask;
        }

        internal Task OnEditorChanged(string text, string[] warnings, int sceneCount)
        {
            _text = text ?? "";
            _sceneCount = sceneCount;
            HydrateModelFromText();
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.SignOff.MapWarnings(warnings);
            S.Save.ScheduleAutosave();
            return S.InvokeAsync(S.StateHasChanged);
        }

        internal Task InsertAsync(string snippet)
        {
            if (!CanEdit) return Task.CompletedTask;
            _text = (_text ?? "").TrimEnd() + snippet;
            HydrateModelFromText();
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.Save.ScheduleAutosave();
            S.StateHasChanged();
            return Task.CompletedTask;
        }

        internal Task InsertTitleFieldAsync(string key, string? afterKey = null)
        {
            if (!CanEdit) return Task.CompletedTask;
            switch (key.Trim().ToLowerInvariant())
            {
                case "title":
                    if (string.IsNullOrWhiteSpace(_model.Metadata.Title))
                        _model.Metadata.Title = "Untitled";
                    break;
                case "author":
                case "authors":
                    if (string.IsNullOrWhiteSpace(_model.Metadata.Author))
                        _model.Metadata.Author = "Unknown";
                    break;
            }
            _ = afterKey;
            _text = FountainFormatter.ToFountain(_model);
            S.Save._dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            S.Save.ScheduleAutosave();
            S.StateHasChanged();
            return Task.CompletedTask;
        }

        internal Task SyncTextFromEditorAsync()
        {
            _text = FountainFormatter.ToFountain(_model);
            _sceneCount = _model.Scenes.Count;
            return Task.CompletedTask;
        }

        internal Task DisposeEditorAsync()
        {
            _dotNetRef?.Dispose();
            _dotNetRef = null;
            return Task.CompletedTask;
        }
    }
}
