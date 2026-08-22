using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;

namespace PageToMovie.Cut.Pages;

public partial class Home
{
    private ElementReference _player;
    private CutClip? _selected;
    private bool _busy;
    private string? _error;
    private int _progressPercent;
    private string? _progressMessage;

    private double RangeMax => _selected is { HasDuration: true } ? _selected.DurationSec : 1;
    private bool ExportDisabled =>
        _busy
        || Folder.Clips.Count == 0
        || Folder.Clips.Any(c => c.Missing || string.IsNullOrWhiteSpace(c.PreviewUrl));

    private void Select(CutClip clip)
    {
        _selected = clip;
        _error = clip.Missing ? (clip.MissingReason ?? $"Clip is missing: {clip.Label}.") : null;
    }

    private async Task SelectTakeAsync(int take)
    {
        if (_selected is null)
            return;
        await Folder.SetCurrentTakeAsync(_selected, take);
        _error = _selected.Missing ? (_selected.MissingReason ?? $"Clip is missing: {_selected.Label}.") : Folder.FolderError;
    }

    private async Task PickFolderAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            await Folder.PickFolderAsync();
            AfterFolderLoad();
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
            AfterFolderLoad();
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

    private void AfterFolderLoad()
    {
        _error = Folder.FolderError;
        _selected = Folder.Clips.FirstOrDefault();
        if (_selected?.Missing == true && string.IsNullOrWhiteSpace(_error))
            _error = _selected.MissingReason ?? $"Clip is missing: {_selected.Label}.";
    }

    private async Task OnPreviewMetadataAsync()
    {
        if (_selected is null || string.IsNullOrWhiteSpace(_selected.PreviewUrl))
            return;
        var seconds = await Compose.ReadMediaDurationAsync(_player);
        _selected.SetDuration(seconds);
    }

    private void SetIn(object? value)
    {
        if (_selected is null || !TryParseSec(value, out var seconds))
            return;
        _selected.ApplyInOut(seconds, _selected.MarkOut);
    }

    private void SetOut(object? value)
    {
        if (_selected is null || !TryParseSec(value, out var seconds))
            return;
        _selected.ApplyInOut(_selected.MarkIn, seconds);
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
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task ClearAudioAsync() => await Compose.ClearAudioAsync();

    private async Task ExportAsync()
    {
        _error = null;
        _busy = true;
        _progressPercent = 0;
        _progressMessage = "Starting…";
        try
        {
            await Compose.ExportMovieAsync(Folder.Clips, (pct, msg) =>
            {
                _progressPercent = Math.Clamp(pct, 0, 100);
                _progressMessage = msg;
                _ = InvokeAsync(StateHasChanged);
            });
            _progressMessage = "Downloaded movie.mp4";
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
}
