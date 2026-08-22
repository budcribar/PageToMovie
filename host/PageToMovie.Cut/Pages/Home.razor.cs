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
    private bool _busy;
    private string? _error;
    private double _playhead;
    private DotNetObjectReference<MediaTimeSink>? _timeRef;
    internal int ProgressPercent { get; private set; }
    internal string? ProgressMessage { get; private set; }
    internal string ProgressText => $"{ProgressPercent}% · {ProgressMessage}";
    internal string? SavedNote { get; private set; }

    private bool ExportDisabled =>
        _busy
        || Folder.Clips.Count == 0
        || Folder.Clips.Any(c => c.Missing || string.IsNullOrWhiteSpace(c.PreviewUrl));

    private bool ShowCard => _selected is not null && _selected.IsFirstOfScene(Folder.Clips);

    private void Select(CutClip clip)
    {
        _selected = clip;
        SavedNote = null;
        _error = clip.Missing ? (clip.MissingReason ?? $"Selected take file is missing: {clip.Label}.") : null;
        _ = SeekPreviewToInAsync();
    }

    private async Task SelectTakeAsync(int take)
    {
        if (_selected is null)
            return;
        await Folder.SetCurrentTakeAsync(_selected, take);
        Compose.ClearMoviePreview();
        _error = _selected.Missing ? (_selected.MissingReason ?? $"Selected take file is missing: {_selected.Label}.") : Folder.FolderError;
        await ProbeAndStripTakeAsync(_selected.SelectedTake);
        await SeekPreviewToInAsync();
    }

    private async Task PickFolderAsync()
    {
        _error = null;
        _busy = true;
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
            _busy = false;
        }
    }

    private async Task PickFilesAsync()
    {
        _error = null;
        _busy = true;
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
            _busy = false;
        }
    }

    private async Task AfterFolderLoadAsync()
    {
        Compose.ClearMoviePreview();
        SavedNote = null;
        _error = Folder.FolderError;
        _selected = Folder.Clips.FirstOrDefault();
        _playhead = 0;
        if (_selected?.Missing == true && string.IsNullOrWhiteSpace(_error))
            _error = _selected.MissingReason ?? $"Selected take file is missing: {_selected.Label}.";
        if (!string.IsNullOrWhiteSpace(Folder.PendingMusicFileName))
            await Compose.TrySetAudioFromFolderAsync(Folder.PendingMusicFileName);
        await ProbeAllTakesAsync();
        await SeekPreviewToInAsync();
        _ = CaptureAllFilmstripsAsync();
    }

    private async Task ProbeAllTakesAsync()
    {
        foreach (var clip in Folder.Clips)
            await ProbeAndStripTakeAsync(clip.SelectedTake, captureStrip: false);
    }

    private async Task CaptureAllFilmstripsAsync()
    {
        foreach (var clip in Folder.Clips)
        {
            await CaptureFilmstripAsync(clip.SelectedTake);
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
        await SeekPreviewToInAsync();
    }

    private async Task SeekPreviewToInAsync()
    {
        if (Js is null || _selected is null || _selected.Missing)
            return;
        try
        {
            await Js.InvokeVoidAsync("PageToMovieCut.seekMedia", ClipPlayer, _selected.MarkIn);
        }
        catch (JSException)
        {
            // player may not be mounted yet
        }
    }

    private async Task OnPlayheadAsync(double timelineSec)
    {
        _playhead = Math.Max(0, timelineSec);
        if (Js is null)
            return;
        try
        {
            if (!string.IsNullOrWhiteSpace(Compose.MoviePreviewUrl))
            {
                await Js.InvokeVoidAsync("PageToMovieCut.seekMedia", MoviePlayer, _playhead);
                return;
            }

            if (CutTimelineLayout.HitTest(Folder.Clips, _playhead) is { } hit && hit.Clip == _selected)
                await Js.InvokeVoidAsync("PageToMovieCut.seekMedia", ClipPlayer, hit.LocalSec);
        }
        catch (JSException)
        {
            // seek is best-effort while the player remounts
        }
    }

    private void OnTimelineEdited()
    {
        Compose.ClearMoviePreview();
        SavedNote = null;
        if (_selected?.SelectedTake is { } take)
            _ = CaptureFilmstripAsync(take);
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
            Compose.ClearMoviePreview();
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
        Compose.ClearMoviePreview();
        SavedNote = null;
    }

    private async Task PlayAsync()
    {
        _error = null;
        _busy = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting…";
        try
        {
            await Compose.PreviewMovieAsync(Folder.Clips, ReportProgress);
            ProgressMessage = "Playing";
            await InvokeAsync(StateHasChanged);
            if (Js is not null)
            {
                DisposeTimeSink();
                _timeRef = DotNetObjectReference.Create(new MediaTimeSink(sec =>
                {
                    _playhead = sec;
                    _ = InvokeAsync(StateHasChanged);
                }));
                await Js.InvokeVoidAsync("PageToMovieCut.bindTimeUpdate", MoviePlayer, _timeRef);
                await Js.InvokeVoidAsync("PageToMovieCut.seekMedia", MoviePlayer, _playhead);
                await Js.InvokeVoidAsync("PageToMovieCut.playVideo", MoviePlayer);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SkipStartAsync() => await OnPlayheadAsync(0);

    private async Task StepAsync(double delta) =>
        await OnPlayheadAsync(Math.Max(0, _playhead + delta));

    private async Task ExportAsync()
    {
        _error = null;
        _busy = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting…";
        try
        {
            await Compose.ExportMovieAsync(Folder.Clips, ReportProgress);
            ProgressMessage = "Downloaded movie.mp4";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
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
        Compose.ClearMoviePreview();
        SavedNote = null;
    }

    private void OnCardCheck(ChangeEventArgs e)
    {
        ToggleCard(e.Value is bool flag && flag);
    }

    private void ToggleCard(bool enabled)
    {
        if (_selected is null)
            return;
        _selected.Card.Enabled = enabled;
        if (enabled && string.IsNullOrWhiteSpace(_selected.Card.Text))
            _selected.Card.Text = $"Scene {_selected.Scene}";
        Compose.ClearMoviePreview();
        SavedNote = null;
    }

    private void SetCardText(ChangeEventArgs e)
    {
        if (_selected is null)
            return;
        _selected.Card.Text = Convert.ToString(e.Value, CultureInfo.InvariantCulture) ?? "";
        Compose.ClearMoviePreview();
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
        DisposeTimeSink();
        return ValueTask.CompletedTask;
    }
}
