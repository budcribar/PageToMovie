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

    private CutPreviewVideos? _preview = null;
    internal ElementReference ClipPlayer => _preview?.ClipPlayer ?? default;
    internal ElementReference MoviePlayer => _preview?.MoviePlayer ?? default;
    internal ElementReference TextOverlay { get; set; }
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
    private int _playingMergeClips;
    private int _playGen;
    private PlayMode _playMode;
    private CutJitPlay.Window? _nativeWindow;
    private CutJitPlay.Window? _firstStart;
    private CancellationTokenSource? _composeCts;
    private DotNetObjectReference<MediaTimeSink>? _timeRef;
    private PlaySurface _boundSurface;
    private string? _clipSrcBound;
    private string? _movieSrcBound;
    private bool _needTextCues;
    internal int ProgressPercent { get; private set; }
    internal string? ProgressMessage { get; private set; }
    internal string ProgressText => $"{ProgressPercent}% · {ProgressMessage}";
    internal string? SavedNote { get; private set; }
    private const string SeekMediaJs = "PageToMovieCut.seekMedia";
    private const string PlayUrlAtJs = "PageToMovieCut.playUrlAt";
    private const string PauseVideoJs = "PageToMovieCut.pauseVideo";
    private const string PaintPlayheadJs = "PageToMovieCut.paintPlayhead";

    private CancellationToken ComposeToken => _composeCts?.Token ?? CancellationToken.None;

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

    private string? ClipPlayerSrc =>
        CutPlayClock.BlazorOwnsVideoSrc(IsPlaying) ? _selected?.PreviewUrl : _clipSrcBound;

    private string? MoviePlayerSrc =>
        CutPlayClock.BlazorOwnsVideoSrc(IsPlaying) ? ActiveMovieUrl : _movieSrcBound;

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
        await TryAttachFreshMovieAsync();
        await SeekPreviewToInAsync();
        _needTextCues = true;
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

        await PaintPlayVisualsAsync();
    }

    private void OnTimelineEdited()
    {
        ForgetPreview();
        SavedNote = null;
        _needTextCues = true;
        if (_selected?.SelectedTake is { } take)
            _ = CaptureFilmstripAsync(take);
        if (!_wantPlay)
            return;
        _firstStart = CutJitPlay.At(Folder.Clips, _playhead);
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
            await SyncPlayheadFromPlayerAsync();
            StopPlay();
            await PaintPlayVisualsAsync();
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
        {
            await SyncPlayheadFromPlayerAsync();
            StopPlay();
        }
        if (!CutSplit.TryAt(Folder.Clips, _playhead, out _))
            return;
        OnTimelineEdited();
        await InvokeAsync(StateHasChanged);
    }

    private async Task PlayAsync()
    {
        _error = null;
        _wantPlay = true;
        _clipSrcBound = _selected?.PreviewUrl;
        _movieSrcBound = ActiveMovieUrl;
        _firstStart = CutJitPlay.At(Folder.Clips, _playhead);
        _needTextCues = true;
        await InvokeAsync(StateHasChanged);
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
                    if (ShouldHandOffToMerge())
                        _ = InvokeAsync(() => ContinuePlayAsync(_playhead));
                    else if (CutPlayClock.ShouldRenderOnPrefix(_playMode == PlayMode.Waiting, _wantPlay))
                        _ = InvokeAsync(StateHasChanged);
                },
                cts.Token,
                Folder.TextClips);
            if (cts.IsCancellationRequested || gen != _playGen)
                return;
            _prefixUrl = Compose.MoviePreviewUrl ?? _prefixUrl;
            _prefixClipCount = Folder.Clips.Count;
            _composing = false;
            ProgressMessage = "Playing";
            if (ShouldHandOffToMerge())
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
        var ready = CutJitPlay.ReadyThroughSec(Folder.Clips, _prefixClipCount, _firstStart);
        var total = CutJitPlay.TotalSec(Folder.Clips);
        var playUrl = Compose.MoviePreviewUrl ?? _prefixUrl;
        var playMerge = CutPlayMerge.ShouldPlayMerge(
            playUrl, Folder.Clips, _prefixClipCount, _playhead, _firstStart);

        if (playMerge)
        {
            await PlayMovieAsync(_playhead, playUrl);
            return;
        }

        if (CutPlayMerge.ShouldPlayFirstStart(_firstStart, _playhead, playMerge))
        {
            await PlayNativeAsync(_playhead);
            return;
        }

        if (CutJitPlay.NeedsWait(_playhead, ready, total))
        {
            await EnterWaitAsync();
            return;
        }

        await EnterWaitAsync();
    }

    private bool ShouldHandOffToMerge()
    {
        if (!CutPlayClock.ShouldSwitchToMergeOnPrefix(
                _wantPlay, _playMode == PlayMode.Waiting, _playMode == PlayMode.Native))
            return false;
        var playUrl = Compose.MoviePreviewUrl ?? _prefixUrl;
        return CutPlayMerge.ShouldPlayMerge(
            playUrl, Folder.Clips, _prefixClipCount, _playhead, _firstStart);
    }

    private async Task PlayNativeAsync(double timelineSec)
    {
        var window = CutJitPlay.At(Folder.Clips, timelineSec);
        if (window is null || window.Value.Clip.Missing || string.IsNullOrWhiteSpace(window.Value.Clip.PreviewUrl))
        {
            await EnterWaitAsync();
            return;
        }

        if (_firstStart is { } start && window.Value.Index != start.Index)
        {
            await EnterWaitAsync();
            return;
        }

        var samePlayer = _playMode == PlayMode.Native;
        _playMode = PlayMode.Native;
        _nativeWindow = window;
        var local = CutJitPlay.TimelineToLocal(window.Value, timelineSec);
        if (CutPlayClock.ShouldRebindPlayback(samePlayer))
            await BindPlaybackAsync(ClipPlayer, PlaySurface.Clip, OnNativeTime, OnNativeEnded);
        if (Js is not null)
        {
            try
            {
                await BindPlaySurfacesAsync();
                await Js.InvokeVoidAsync(PlayUrlAtJs, ClipPlayer, window.Value.Clip.PreviewUrl, local);
                await Js.InvokeVoidAsync("PageToMovieCut.setPlayClockWindow",
                    "native", window.Value.TimelineStart, window.Value.LocalStart, window.Value.LocalEnd);
                await Js.InvokeVoidAsync(PaintPlayheadJs, timelineSec);
                await PrimeMergeAsync();
            }
            catch (JSException)
            {
                // player may not be mounted yet
            }
        }

        if (CutPlayClock.ShouldRenderOnNativeAdvance)
            await InvokeAsync(StateHasChanged);
    }

    private async Task PlayMovieAsync(double timelineSec, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        var samePlayer = _playMode == PlayMode.Movie;
        var sameUrl = samePlayer && string.Equals(_movieSrcBound, url, StringComparison.Ordinal);
        var stillInsidePlayingMerge = samePlayer
            && !CutPlayClock.ShouldReplaceMergeSrcWhilePlaying
            && _playhead < CutPlayMerge.MergeReadyThroughSec(Folder.Clips, _playingMergeClips) - 0.05;
        if (sameUrl || stillInsidePlayingMerge)
        {
            _playhead = Math.Max(0, timelineSec);
            if (Js is not null)
            {
                try
                {
                    await Js.InvokeVoidAsync(PlayUrlAtJs, MoviePlayer, _movieSrcBound ?? url, _playhead);
                    await Js.InvokeVoidAsync("PageToMovieCut.setPlayClockWindow", "movie", 0, 0, 0);
                    await Js.InvokeVoidAsync(PaintPlayheadJs, _playhead);
                }
                catch (JSException)
                {
                    // player may not be mounted yet
                }
            }

            return;
        }

        _playMode = PlayMode.Movie;
        _nativeWindow = null;
        _playhead = Math.Max(0, timelineSec);
        _movieSrcBound = url;
        _playingMergeClips = _prefixClipCount;
        if (CutPlayClock.ShouldRebindPlayback(samePlayer))
            await BindPlaybackAsync(MoviePlayer, PlaySurface.Movie, OnMovieTime, OnMovieEnded);
        if (Js is not null)
        {
            try
            {
                await BindPlaySurfacesAsync();
                await Js.InvokeVoidAsync(PlayUrlAtJs, MoviePlayer, url, _playhead);
                await Js.InvokeVoidAsync("PageToMovieCut.setPlayClockWindow", "movie", 0, 0, 0);
                await Js.InvokeVoidAsync(PaintPlayheadJs, _playhead);
            }
            catch (JSException)
            {
                // player may not be mounted yet
            }
        }
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
                await Js.InvokeVoidAsync("PageToMovieCut.pausePlaySurfaces", ComposeToken);
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
        if (CutPlayClock.ShouldAdvanceNative(localSec, window.LocalEnd))
            _ = AdvanceNativeAsync();
        else if (CutPlayClock.ShouldRenderOnTimeUpdate)
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
        if (CutPlayClock.ShouldRenderOnTimeUpdate)
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

    private async Task BindPlaybackAsync(
        ElementReference player, PlaySurface surface, Action<double> onTime, Action onEnded)
    {
        if (Js is null)
            return;
        if (_boundSurface == surface && _timeRef is not null)
            return;
        DisposeTimeSink();
        _boundSurface = surface;
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
        _firstStart = null;
        _playingMergeClips = 0;
        _boundSurface = PlaySurface.None;
        _playGen++;
        CancelCompose();
        DisposeTimeSink();
        if (Js is not null)
            _ = Js.InvokeVoidAsync("PageToMovieCut.resetPlaySurfaces", CancellationToken.None);
        _ = PausePlayersAsync();
    }

    private async Task PausePlayersAsync()
    {
        if (Js is null)
            return;
        try
        {
            await Js.InvokeVoidAsync("PageToMovieCut.pausePlaySurfaces", CancellationToken.None);
            await Js.InvokeVoidAsync(PauseVideoJs, MoviePlayer);
            await Js.InvokeVoidAsync(PauseVideoJs, ClipPlayer);
        }
        catch (JSException)
        {
            // pause is best-effort while the player remounts
        }
    }

    private async Task BindPlaySurfacesAsync()
    {
        if (Js is null || _preview is null)
            return;
        await Js.InvokeVoidAsync("PageToMovieCut.bindPlaySurfaces", ClipPlayer, MoviePlayer);
    }

    private async Task PrimeMergeAsync()
    {
        if (Js is null || !CutPlayMerge.ShouldPrimeMerge)
            return;
        var playUrl = Compose.MoviePreviewUrl ?? _prefixUrl;
        if (string.IsNullOrWhiteSpace(playUrl))
            return;
        await Js.InvokeVoidAsync("PageToMovieCut.primeUrlAt", playUrl, _playhead, "movie");
    }

    private async Task TryAttachFreshMovieAsync()
    {
        if (!CutPlayMerge.IsFreshMerge(
                Folder.SavedMovieFingerprint, Folder.Clips, Folder.TextClips, Folder.PendingMusicFileName))
            return;
        var url = await Folder.TryOpenMovieMp4Async();
        if (string.IsNullOrWhiteSpace(url))
            return;
        Compose.AttachExistingMerge(url, Folder.Clips.Count);
        _prefixUrl = url;
        _prefixClipCount = Folder.Clips.Count;
        _movieSrcBound = url;
    }

    private string? CurrentMergeFingerprint() =>
        CutPlayMerge.Fingerprint(Folder.Clips, Folder.TextClips, Compose.AudioFileName);

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
        _playingMergeClips = 0;
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
            _prefixUrl = Compose.MoviePreviewUrl ?? _prefixUrl;
            _prefixClipCount = Folder.Clips.Count;
            if (Folder.CanWrite && !string.IsNullOrWhiteSpace(Compose.MoviePreviewUrl))
                await Folder.WriteMovieMp4Async(Compose.MoviePreviewUrl);
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
        if (CutPlayClock.ShouldRenderOnProgress(ShowComposeOverlay))
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
        var fp = Compose.HasCachedMoviePreview ? CurrentMergeFingerprint() : null;
        if (!await Folder.SaveFinishAsync(Compose.AudioFileName, fp))
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

    private async Task SyncPlayheadFromPlayerAsync()
    {
        if (Js is null)
            return;
        try
        {
            if (_playMode == PlayMode.Native && _nativeWindow is { } window)
            {
                var local = await Js.InvokeAsync<double>("PageToMovieCut.readCurrentTime", ClipPlayer);
                _playhead = CutJitPlay.LocalToTimeline(window, local);
            }
            else if (_playMode == PlayMode.Movie)
            {
                _playhead = await Js.InvokeAsync<double>("PageToMovieCut.readCurrentTime", MoviePlayer);
            }
        }
        catch (JSException)
        {
            // playhead stays at the last known time
        }
    }

    private async Task PaintPlayVisualsAsync()
    {
        if (Js is null)
            return;
        try
        {
            await Js.InvokeVoidAsync(
                "PageToMovieCut.setLiveTextOverlay",
                CutPlayOverlay.UseLiveOverlay(ShowMovie));
            await Js.InvokeVoidAsync(PaintPlayheadJs, _playhead);
        }
        catch (JSException)
        {
            // paint is best-effort while the player remounts
        }
    }

    private async Task PushTextCuesAsync()
    {
        if (Js is null)
            return;
        try
        {
            var cues = CutPlayOverlay.Cues(Folder.Clips, Folder.TextClips);
            await Js.InvokeVoidAsync("PageToMovieCut.setTextCues", TextOverlay, cues);
            await PaintPlayVisualsAsync();
        }
        catch (JSException)
        {
            // overlay mounts with the preview stage
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await BindPlaySurfacesAsync();
            if (!IsPlaying && Js is not null)
            {
                await Js.InvokeVoidAsync("PageToMovieCut.resetPlaySurfaces", CancellationToken.None);
                await Js.InvokeVoidAsync("PageToMovieCut.setPreviewSurface", MoviePlayer, ClipPlayer, ShowMovie);
            }
        }
        catch (JSException)
        {
            // surfaces mount with the preview stage
        }

        if (!_needTextCues)
            return;
        _needTextCues = false;
        await PushTextCuesAsync();
    }

    private enum PlayMode
    {
        Idle,
        Native,
        Movie,
        Waiting,
    }

    private enum PlaySurface
    {
        None,
        Clip,
        Movie,
    }
}
