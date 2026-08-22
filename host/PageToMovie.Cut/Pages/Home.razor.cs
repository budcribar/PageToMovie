using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;

namespace PageToMovie.Cut.Pages;

public partial class Home : IAsyncDisposable
{
    [Inject] private IJSRuntime? Js { get; set; }

    internal ElementReference ClipPlayer { get; set; }
    internal ElementReference MoviePlayer { get; set; }
    private CutClip? _selected;
    private bool _folderBusy;
    private bool _exporting;
    private bool _composing;
    private bool _wantPlay;
    private bool _advancing;
    private string? _error;
    private double _playhead;
    private string? _prefixUrl;
    private int _prefixClipCount;
    private int _playGen;
    private PlayMode _playMode;
    private CutJitPlay.Window? _nativeWindow;
    private CancellationTokenSource? _composeCts;
    private DotNetObjectReference<MediaTimeSink>? _timeRef;
    internal int ProgressPercent { get; private set; }
    internal string? ProgressMessage { get; private set; }
    internal string ProgressText => $"{ProgressPercent}% · {ProgressMessage}";
    internal string? SavedNote { get; private set; }
    private const string SeekMediaJs = "PageToMovieCut.seekMedia";
    private const string PlayUrlAtJs = "PageToMovieCut.playUrlAt";
    private const string PauseVideoJs = "PageToMovieCut.pauseVideo";

    private bool _busy => TransportLocked;
    internal bool TransportLocked => _folderBusy || _exporting;

    internal bool ShowComposeOverlay =>
        _playMode == PlayMode.Waiting
        || (_exporting && !string.IsNullOrWhiteSpace(ProgressMessage));

    internal bool ShowMovie =>
        _playMode is PlayMode.Movie
        || (_playMode != PlayMode.Native
            && (!string.IsNullOrWhiteSpace(Compose.MoviePreviewUrl) || !string.IsNullOrWhiteSpace(_prefixUrl)));

    internal string? ActiveMovieUrl => Compose.MoviePreviewUrl ?? _prefixUrl;

    private bool PlayDisabled =>
        TransportLocked
        || Folder.Clips.Count == 0
        || Folder.Clips.Any(c => c.Missing || string.IsNullOrWhiteSpace(c.PreviewUrl));

    private bool ExportDisabled => PlayDisabled;

    internal bool IsPlaying => _wantPlay;

    private void Select(CutClip clip)
    {
        _selected = clip;
        SavedNote = null;
        _error = clip.Missing ? (clip.MissingReason ?? $"Selected take file is missing: {clip.Label}.") : null;
        if (!_wantPlay)
            _ = SeekPreviewToInAsync();
    }

    private async Task SelectTakeAsync(int take)
    {
        if (_selected is null)
            return;
        await Folder.SetCurrentTakeAsync(_selected, take);
        ForgetPreview();
        _error = _selected.Missing ? (_selected.MissingReason ?? $"Selected take file is missing: {_selected.Label}.") : Folder.FolderError;
        await ProbeAndStripTakeAsync(_selected.SelectedTake);
        await SeekPreviewToInAsync();
    }

    private async Task PickFolderAsync()
    {
        _error = null;
        StopPlay();
        _folderBusy = true;
        try
        {
            await Folder.PickFolderAsync();
            await AfterFolderLoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _folderBusy = false;
        }
    }

    private async Task PickFilesAsync()
    {
        _error = null;
        StopPlay();
        _folderBusy = true;
        try
        {
            await Folder.PickMp4FilesFallbackAsync();
            await AfterFolderLoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _folderBusy = false;
        }
    }

    private async Task AfterFolderLoadAsync()
    {
        ForgetPreview();
        SavedNote = null;
        _error = Folder.FolderError;
        _selected = Folder.Clips.FirstOrDefault();
        _playhead = 0;
        if (_selected?.Missing == true && string.IsNullOrWhiteSpace(_error))
            _error = _selected.MissingReason ?? $"Selected take file is missing: {_selected.Label}.";
        if (!string.IsNullOrWhiteSpace(Folder.PendingMusicFileName))
            await Compose.TrySetAudioFromFolderAsync(Folder.PendingMusicFileName);
        foreach (var clip in Folder.Clips)
            await ProbeAndStripTakeAsync(clip.SelectedTake, captureStrip: false);
        await SeekPreviewToInAsync();
        _ = RefreshFilmstripsAsync();
    }

    private async Task RefreshFilmstripsAsync()
    {
        foreach (var clip in Folder.Clips)
        {
            await CaptureFilmstripAsync(clip.SelectedTake);
            if (ReferenceEquals(clip, _selected))
                await SeekPreviewToInAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ProbeAndStripTakeAsync(CutTake? take, bool captureStrip = true)
    {
        if (take is null || take.Missing || string.IsNullOrWhiteSpace(take.PreviewUrl) || Js is null)
            return;
        try
        {
            var seconds = await Js.InvokeAsync<double>("PageToMovieCut.probeUrlDuration", take.PreviewUrl);
            take.SetDuration(seconds);
        }
        catch (JSException)
        {
            // probe is best-effort; metadata on the preview player still applies hop
        }

        if (captureStrip)
            await CaptureFilmstripAsync(take);
    }

    private async Task CaptureFilmstripAsync(CutTake? take)
    {
        if (take is null || take.Missing || string.IsNullOrWhiteSpace(take.PreviewUrl) || Js is null)
            return;
        var start = take.MarkIn;
        var stop = take.MarkOut > take.MarkIn ? take.MarkOut : take.DurationSec;
        if (stop <= start)
            return;
        var count = Math.Clamp((int)Math.Round((stop - start) / 0.85), 2, 8);
        try
        {
            var strip = await Js.InvokeAsync<JsFilmstrip>(
                "PageToMovieCut.captureFilmstrip", take.PreviewUrl, start, stop, count);
            take.Filmstrip.Clear();
            if (strip.Success)
                take.Filmstrip.AddRange(strip.Frames);
        }
        catch (JSException)
        {
            // thumbs are optional
        }
    }

    private async Task OnPreviewMetadataAsync()
    {
        if (_selected is null || string.IsNullOrWhiteSpace(_selected.PreviewUrl))
            return;
        var seconds = await Compose.ReadMediaDurationAsync(ClipPlayer);
        _selected.SetDuration(seconds);
        if (_playMode == PlayMode.Native && _wantPlay)
            return;
        await SeekPreviewToInAsync();
    }

    private async Task SeekPreviewToInAsync()
    {
        if (Js is null || _selected is null || _selected.Missing)
            return;
        try
        {
            await Js.InvokeVoidAsync(SeekMediaJs, ClipPlayer, _selected.MarkIn);
        }
        catch (JSException)
        {
            // player may not be mounted yet
        }
    }

    private async Task OnPlayheadAsync(double timelineSec)
    {
        _playhead = Math.Max(0, timelineSec);
        if (_wantPlay)
        {
            await ContinuePlayAsync(_playhead);
            return;
        }

        if (Js is null)
            return;
        try
        {
            if (!string.IsNullOrWhiteSpace(ActiveMovieUrl) && ShowMovie)
            {
                await Js.InvokeVoidAsync(SeekMediaJs, MoviePlayer, _playhead);
                return;
            }

            if (CutTimelineLayout.HitTest(Folder.Clips, _playhead) is { } hit && hit.Clip == _selected)
                await Js.InvokeVoidAsync(SeekMediaJs, ClipPlayer, hit.LocalSec);
        }
        catch (JSException)
        {
            // seek is best-effort while the player remounts
        }
    }

    private void OnTimelineEdited()
    {
        ForgetPreview();
        SavedNote = null;
        if (_selected?.SelectedTake is { } take)
            _ = CaptureFilmstripAsync(take);
        if (!_wantPlay)
            return;
        StartJitCompose();
        _ = ContinuePlayAsync(_playhead);
    }

    private async Task OnAudioAsync(InputFileChangeEventArgs e)
    {
        _error = null;
        try
        {
            var file = e.File;
            if (file is null)
                return;
            await Compose.SetAudioFromBrowserFileAsync(file);
            ForgetPreview();
            SavedNote = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task ClearAudioAsync()
    {
        await Compose.ClearAudioAsync();
        ForgetPreview();
        SavedNote = null;
    }

    private async Task TogglePlayAsync()
    {
        if (_wantPlay)
        {
            StopPlay();
            await InvokeAsync(StateHasChanged);
            return;
        }

        await PlayAsync();
    }

    private async Task SplitAtPlayheadAsync()
    {
        if (TransportLocked || !CutSplit.CanAt(Folder.Clips, _playhead))
            return;
        if (_wantPlay)
            StopPlay();
        if (!CutSplit.TryAt(Folder.Clips, _playhead, out _))
            return;
        OnTimelineEdited();
        await InvokeAsync(StateHasChanged);
    }

    private async Task PlayAsync()
    {
        _error = null;
        _wantPlay = true;
        if (CutJitPlay.CanReuseFullPreview(Compose.MoviePreviewUrl))
        {
            await PlayMovieAsync(_playhead, Compose.MoviePreviewUrl);
            return;
        }

        StartJitCompose();
        await ContinuePlayAsync(_playhead);
    }

    private void StartJitCompose()
    {
        if (_composing)
            return;
        _composing = true;
        ProgressPercent = 0;
        ProgressMessage = "Preparing movie…";
        var cts = new CancellationTokenSource();
        _composeCts?.Cancel();
        _composeCts = cts;
        var gen = _playGen;
        _ = RunJitComposeAsync(cts, gen);
    }

    private async Task RunJitComposeAsync(CancellationTokenSource cts, int gen)
    {
        try
        {
            await Compose.PreviewMovieJitAsync(
                Folder.Clips,
                ReportProgress,
                (url, count) =>
                {
                    if (cts.IsCancellationRequested || gen != _playGen)
                        return;
                    _prefixUrl = url;
                    _prefixClipCount = count;
                    _ = InvokeAsync(async () =>
                    {
                        if (_wantPlay && _playMode == PlayMode.Waiting)
                            await ContinuePlayAsync(_playhead);
                        else
                            StateHasChanged();
                    });
                },
                cts.Token,
                Folder.TextClips);
            if (cts.IsCancellationRequested || gen != _playGen)
                return;
            _prefixUrl = Compose.MoviePreviewUrl ?? _prefixUrl;
            _prefixClipCount = Folder.Clips.Count;
            _composing = false;
            ProgressMessage = "Playing";
            if (_wantPlay)
                await ContinuePlayAsync(_playhead);
        }
        catch (OperationCanceledException)
        {
            _composing = false;
        }
        catch (Exception ex)
        {
            _composing = false;
            _error = ex.Message;
            if (_playMode == PlayMode.Waiting)
                _playMode = PlayMode.Idle;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ContinuePlayAsync(double timelineSec)
    {
        if (Js is null || !_wantPlay)
            return;
        _playhead = Math.Max(0, timelineSec);
        var ready = CutJitPlay.ReadyThroughSec(Folder.Clips, _prefixClipCount);
        var nativeEnd = CutJitPlay.NativeReachableThrough(Folder.Clips);
        var total = CutJitPlay.TotalSec(Folder.Clips);
        var playUrl = Compose.MoviePreviewUrl ?? _prefixUrl;

        if (CutJitPlay.NeedsWait(_playhead, ready, total))
        {
            await EnterWaitAsync();
            return;
        }

        if (CutJitPlay.CanReuseFullPreview(Compose.MoviePreviewUrl))
        {
            await PlayMovieAsync(_playhead, Compose.MoviePreviewUrl);
            return;
        }

        if (!string.IsNullOrWhiteSpace(playUrl)
            && _prefixClipCount > 0
            && _playhead >= nativeEnd - 0.001)
        {
            await PlayMovieAsync(_playhead, playUrl);
            return;
        }

        await PlayNativeAsync(_playhead);
    }

    private async Task PlayNativeAsync(double timelineSec)
    {
        var window = CutJitPlay.At(Folder.Clips, timelineSec);
        if (window is null || window.Value.Clip.Missing || string.IsNullOrWhiteSpace(window.Value.Clip.PreviewUrl))
        {
            await EnterWaitAsync();
            return;
        }

        _playMode = PlayMode.Native;
        _nativeWindow = window;
        _selected = window.Value.Clip;
        var local = CutJitPlay.TimelineToLocal(window.Value, timelineSec);
        await BindPlaybackAsync(ClipPlayer, OnNativeTime, OnNativeEnded);
        if (Js is not null)
        {
            try
            {
                await Js.InvokeVoidAsync(PauseVideoJs, MoviePlayer);
                await Js.InvokeVoidAsync(PlayUrlAtJs, ClipPlayer, window.Value.Clip.PreviewUrl, local);
            }
            catch (JSException)
            {
                // player may not be mounted yet
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task PlayMovieAsync(double timelineSec, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        _playMode = PlayMode.Movie;
        _nativeWindow = null;
        _playhead = Math.Max(0, timelineSec);
        await BindPlaybackAsync(MoviePlayer, OnMovieTime, OnMovieEnded);
        if (Js is not null)
        {
            try
            {
                await Js.InvokeVoidAsync(PauseVideoJs, ClipPlayer);
                await Js.InvokeVoidAsync(PlayUrlAtJs, MoviePlayer, url, _playhead);
            }
            catch (JSException)
            {
                // player may not be mounted yet
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task EnterWaitAsync()
    {
        _playMode = PlayMode.Waiting;
        if (string.IsNullOrWhiteSpace(ProgressMessage))
            ProgressMessage = "Preparing movie…";
        if (Js is not null)
        {
            try
            {
                await Js.InvokeVoidAsync(PauseVideoJs, MoviePlayer);
                await Js.InvokeVoidAsync(PauseVideoJs, ClipPlayer);
            }
            catch (JSException)
            {
                // pause is best-effort
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private void OnNativeTime(double localSec)
    {
        if (_playMode != PlayMode.Native || _nativeWindow is not { } window)
            return;
        _playhead = CutJitPlay.LocalToTimeline(window, localSec);
        if (localSec >= window.LocalEnd - 0.04)
            _ = AdvanceNativeAsync();
        else
            _ = InvokeAsync(StateHasChanged);
    }

    private void OnNativeEnded() => _ = AdvanceNativeAsync();

    private async Task AdvanceNativeAsync()
    {
        if (_advancing || _playMode != PlayMode.Native || _nativeWindow is not { } window)
            return;
        _advancing = true;
        try
        {
            await ContinuePlayAsync(window.TimelineEnd);
        }
        finally
        {
            _advancing = false;
        }
    }

    private void OnMovieTime(double seconds)
    {
        if (_playMode != PlayMode.Movie)
            return;
        _playhead = Math.Max(0, seconds);
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnMovieEnded()
    {
        if (!_wantPlay)
            return;
        var total = CutJitPlay.TotalSec(Folder.Clips);
        if (CutJitPlay.IsTimelineEnd(_playhead, total))
        {
            _wantPlay = false;
            _playMode = PlayMode.Idle;
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        // S01 prefix EOF is not Stop — wait for the scene-change stitch or play it.
        _ = ContinuePlayAsync(_playhead);
    }

    private async Task BindPlaybackAsync(ElementReference player, Action<double> onTime, Action onEnded)
    {
        if (Js is null)
            return;
        DisposeTimeSink();
        _timeRef = DotNetObjectReference.Create(new MediaTimeSink(onTime, onEnded));
        try
        {
            await Js.InvokeVoidAsync("PageToMovieCut.bindPlayback", player, _timeRef);
        }
        catch (JSException)
        {
            // bind is best-effort while the player remounts
        }
    }

    private void StopPlay()
    {
        _wantPlay = false;
        _playMode = PlayMode.Idle;
        _nativeWindow = null;
        _playGen++;
        CancelCompose();
        _ = PausePlayersAsync();
    }

    private async Task PausePlayersAsync()
    {
        if (Js is null)
            return;
        try
        {
            await Js.InvokeVoidAsync(PauseVideoJs, MoviePlayer);
            await Js.InvokeVoidAsync(PauseVideoJs, ClipPlayer);
        }
        catch (JSException)
        {
            // pause is best-effort while the player remounts
        }
    }

    private void CancelCompose()
    {
        _composeCts?.Cancel();
        _composeCts?.Dispose();
        _composeCts = null;
        _composing = false;
        _ = Compose.AbortAsync();
    }

    private void ForgetPreview()
    {
        _playGen++;
        CancelCompose();
        Compose.ClearMoviePreview();
        _prefixUrl = null;
        _prefixClipCount = 0;
    }

    private async Task SkipStartAsync() => await OnPlayheadAsync(0);

    private async Task StepAsync(double delta) =>
        await OnPlayheadAsync(Math.Max(0, _playhead + delta));

    private async Task ExportAsync()
    {
        _error = null;
        StopPlay();
        _exporting = true;
        ProgressPercent = 0;
        ProgressMessage = "Preparing movie…";
        await InvokeAsync(StateHasChanged);
        try
        {
            await Compose.ExportMovieAsync(Folder.Clips, ReportProgress, texts: Folder.TextClips);
            ProgressMessage = "Downloaded movie.mp4";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _exporting = false;
        }
    }

    private void ReportProgress(int pct, string msg)
    {
        ProgressPercent = Math.Clamp(pct, 0, 100);
        ProgressMessage = msg;
        _ = InvokeAsync(StateHasChanged);
    }

    private static string FormatSec(double seconds) =>
        seconds.ToString("0.00", CultureInfo.InvariantCulture);

    private void RemoveRangeDelete(CutRangeSpan span)
    {
        _selected?.RangeDeletes.Remove(span);
        ForgetPreview();
        SavedNote = null;
    }

    private async Task SaveFinishAsync()
    {
        _error = null;
        SavedNote = null;
        if (!await Folder.SaveFinishAsync(Compose.AudioFileName))
        {
            _error = Folder.FolderError ?? "Could not save the cut.";
            return;
        }

        SavedNote = "Saved cut.project.json";
    }

    private void DisposeTimeSink()
    {
        _timeRef?.Dispose();
        _timeRef = null;
    }

    public ValueTask DisposeAsync()
    {
        StopPlay();
        DisposeTimeSink();
        return ValueTask.CompletedTask;
    }

    private enum PlayMode
    {
        Idle,
        Native,
        Movie,
        Waiting,
    }
}
