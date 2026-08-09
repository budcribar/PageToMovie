namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    /// <summary>Dirty / autosave / draft persistence for the screenplay page.</summary>
    public sealed class ScreenplaySave
    {
        private readonly AdaptationScreenplay S;
        public ScreenplaySave(AdaptationScreenplay host) => S = host;

        internal bool _dirtyLocal;
        internal int _saveGeneration;
        internal DateTime? _lastSavedUtc;
        internal CancellationTokenSource? _saveCts;

        internal void ScheduleAutosave()
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            var ct = _saveCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(900, ct);
                    await S.InvokeAsync(async () =>
                    {
                        if (_dirtyLocal && !S.Busy && !ct.IsCancellationRequested)
                            await SaveDraftAsync(manual: false);
                    });
                }
                catch (OperationCanceledException) { /* expected */ }
            });
        }

        internal async Task SaveDraftAsync(bool manual)
        {
            await S.Editor.SyncTextFromEditorAsync();
            if (string.IsNullOrEmpty(S.Editor._text) && !_dirtyLocal && !manual)
                return;

            var gen = ++_saveGeneration;
            try
            {
                if (manual) S.Busy = true;
                var result = await S.Engine.SaveScreenplayAsync(S.ProjectId, S.Editor._text);
                if (gen != _saveGeneration) return;
                S.Editor._loadedText = S.Editor._text;
                _dirtyLocal = false;
                _lastSavedUtc = DateTime.UtcNow;
                S.SignOff._screenplayStatus = result?.Screenplay ?? S.SignOff._screenplayStatus;
                if (result?.Adaptation is not null)
                    S.Status = result.Adaptation;
                if (manual)
                    S.Message = result?.Message ?? "Draft saved";
            }
            catch (Exception ex)
            {
                if (manual || gen == _saveGeneration)
                    S.Error = ex.Message;
            }
            finally
            {
                if (manual) S.Busy = false;
                S.StateHasChanged();
            }
        }

        internal async Task OnFilmLengthChangedAsync()
        {
            try { await S.SoftLoadAsync(); } catch { /* ignore */ }
        }

        internal void DisposeSaveCts()
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = null;
        }
    }

    // ── Method forwarders (ScreenplaySave) ──
    private void ScheduleAutosave() => Save.ScheduleAutosave();
    private Task SaveDraftAsync(bool manual) => Save.SaveDraftAsync(manual);
    private Task OnFilmLengthChangedAsync() => Save.OnFilmLengthChangedAsync();
}
