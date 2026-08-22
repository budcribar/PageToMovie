using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;

namespace PageToMovie.Cut.Pages;

public partial class Home
{
    [Inject] private IJSRuntime? Js { get; set; }

    internal ElementReference ClipPlayer { get; set; }
    internal ElementReference MoviePlayer { get; set; }
    private CutClip? _selected;
    private bool _busy;
    private string? _error;
    internal int ProgressPercent { get; private set; }
    internal string? ProgressMessage { get; private set; }
    internal string ProgressText => $"{ProgressPercent}% · {ProgressMessage}";

    private double _spanFrom;
    private double _spanTo;
    private string? _savedNote;

    private double RangeMax => _selected is { HasDuration: true } ? _selected.DurationSec : 1;
    private bool ExportDisabled =>
        _busy
        || Folder.Clips.Count == 0
        || Folder.Clips.Any(c => c.Missing || string.IsNullOrWhiteSpace(c.PreviewUrl));

    private CutClip? NextAfterSelected
    {
        get
        {
            if (_selected is null)
                return null;
            for (var i = 0; i < Folder.Clips.Count - 1; i++)
            {
                if (ReferenceEquals(Folder.Clips[i], _selected))
                    return Folder.Clips[i + 1];
            }

            return null;
        }
    }

    private bool ShowJoin => NextAfterSelected is not null;
    private bool ShowCard => _selected is not null && _selected.IsFirstOfScene(Folder.Clips);
    private CutJoinKind ResolvedJoin =>
        _selected is null ? CutJoinKind.Cut : _selected.JoinToNext(NextAfterSelected);

    private void Select(CutClip clip)
    {
        _selected = clip;
        _savedNote = null;
        SeedSpanDefaults();
        _error = clip.Missing ? (clip.MissingReason ?? $"Selected take file is missing: {clip.Label}.") : null;
    }

    private async Task SelectTakeAsync(int take)
    {
        if (_selected is null)
            return;
        await Folder.SetCurrentTakeAsync(_selected, take);
        Compose.ClearMoviePreview();
        _error = _selected.Missing ? (_selected.MissingReason ?? $"Selected take file is missing: {_selected.Label}.") : Folder.FolderError;
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
        _savedNote = null;
        _error = Folder.FolderError;
        _selected = Folder.Clips.FirstOrDefault();
        SeedSpanDefaults();
        if (_selected?.Missing == true && string.IsNullOrWhiteSpace(_error))
            _error = _selected.MissingReason ?? $"Selected take file is missing: {_selected.Label}.";
        if (!string.IsNullOrWhiteSpace(Folder.PendingMusicFileName))
            await Compose.TrySetAudioFromFolderAsync(Folder.PendingMusicFileName);
    }

    private void SeedSpanDefaults()
    {
        if (_selected is not { HasDuration: true } c)
        {
            _spanFrom = 0;
            _spanTo = 0;
            return;
        }

        var mid = (c.MarkIn + c.MarkOut) / 2;
        _spanFrom = Math.Max(c.MarkIn, mid - 0.25);
        _spanTo = Math.Min(c.MarkOut, mid + 0.25);
    }

    private async Task OnPreviewMetadataAsync()
    {
        if (_selected is null || string.IsNullOrWhiteSpace(_selected.PreviewUrl))
            return;
        var seconds = await Compose.ReadMediaDurationAsync(ClipPlayer);
        _selected.SetDuration(seconds);
        SeedSpanDefaults();
    }

    private void SetIn(object? value)
    {
        if (_selected is null || !TryParseSec(value, out var seconds))
            return;
        _selected.ApplyInOut(seconds, _selected.MarkOut);
        Compose.ClearMoviePreview();
        _savedNote = null;
    }

    private void SetOut(object? value)
    {
        if (_selected is null || !TryParseSec(value, out var seconds))
            return;
        _selected.ApplyInOut(_selected.MarkIn, seconds);
        Compose.ClearMoviePreview();
        _savedNote = null;
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
            _savedNote = null;
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
                await Js.InvokeVoidAsync("PageToMovieCut.playVideo", MoviePlayer);
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

    private static bool TryParseSec(object? value, out double seconds)
    {
        seconds = 0;
        if (value is null)
            return false;
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    private void SetSpanFrom(object? value)
    {
        if (TryParseSec(value, out var seconds))
            _spanFrom = seconds;
    }

    private void SetSpanTo(object? value)
    {
        if (TryParseSec(value, out var seconds))
            _spanTo = seconds;
    }

    private void AddRangeDelete()
    {
        if (_selected is null)
            return;
        _error = null;
        if (!CutRangeDelete.TryAdd(_selected.RangeDeletes, _spanFrom, _spanTo, _selected.MarkIn, _selected.MarkOut, out _))
        {
            _error = "That span is too small or would remove the whole clip.";
            return;
        }

        Compose.ClearMoviePreview();
        _savedNote = null;
    }

    private void RemoveRangeDelete(CutRangeSpan span)
    {
        _selected?.RangeDeletes.Remove(span);
        Compose.ClearMoviePreview();
        _savedNote = null;
    }

    private void SetJoin(CutJoinKind kind)
    {
        if (_selected is null)
            return;
        _selected.JoinOverride = kind;
        Compose.ClearMoviePreview();
        _savedNote = null;
    }

    private void ToggleCard(bool enabled)
    {
        if (_selected is null)
            return;
        _selected.Card.Enabled = enabled;
        if (enabled && string.IsNullOrWhiteSpace(_selected.Card.Text))
            _selected.Card.Text = $"Scene {_selected.Scene}";
        Compose.ClearMoviePreview();
        _savedNote = null;
    }

    private void SetCardText(ChangeEventArgs e)
    {
        if (_selected is null)
            return;
        _selected.Card.Text = Convert.ToString(e.Value, CultureInfo.InvariantCulture) ?? "";
        Compose.ClearMoviePreview();
        _savedNote = null;
    }

    private async Task SaveFinishAsync()
    {
        _error = null;
        _savedNote = null;
        if (!await Folder.SaveFinishAsync(Compose.AudioFileName))
        {
            _error = Folder.FolderError ?? "Could not save the cut.";
            return;
        }

        _savedNote = "Saved cut.project.json";
    }
}
