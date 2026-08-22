using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;

namespace PageToMovie.Cut.Services;

/// <summary>
/// Browser ffmpeg.wasm compose — concat trimmed clips, optional one-track audio mix, download movie.mp4.
/// </summary>
public sealed class CutComposeService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private string? _audioUrl;

    public CutComposeService(IJSRuntime js) => _js = js;

    public string? AudioFileName { get; private set; }
    public bool HasAudio => !string.IsNullOrWhiteSpace(_audioUrl);

    public async Task SetAudioFromBrowserFileAsync(IBrowserFile file)
    {
        await ClearAudioAsync();
        await using var stream = file.OpenReadStream(maxAllowedSize: 200_000_000);
        using var jsStream = new DotNetStreamReference(stream);
        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "audio/mpeg" : file.ContentType;
        var r = await _js.InvokeAsync<JsResult>("PageToMovieCut.createBlobUrlFromStream", jsStream, mime);
        if (!r.Success || string.IsNullOrWhiteSpace(r.Url))
            throw new InvalidOperationException(r.Error ?? "Could not read the audio file.");
        _audioUrl = r.Url;
        AudioFileName = file.Name;
    }

    public async Task ClearAudioAsync()
    {
        if (!string.IsNullOrWhiteSpace(_audioUrl))
        {
            try { await _js.InvokeVoidAsync("PageToMovieCut.revokeBlobUrl", _audioUrl); }
            catch { /* ignore */ }
        }

        _audioUrl = null;
        AudioFileName = null;
    }

    public async Task<double> ReadMediaDurationAsync(ElementReference media) =>
        await _js.InvokeAsync<double>("PageToMovieCut.readMediaDuration", media);

    public async Task<string?> ExportMovieAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var missing = clips.FirstOrDefault(c => c.Missing || string.IsNullOrWhiteSpace(c.PreviewUrl));
        if (missing is not null)
            throw new InvalidOperationException(missing.MissingReason ?? $"Clip is missing: {missing.Label}.");
        if (clips.Count == 0)
            throw new InvalidOperationException("No clips to export.");

        var payload = clips.Select(c => new JsExportClip
        {
            Url = c.PreviewUrl,
            Label = c.Label,
            FileName = c.FileName,
            MarkIn = c.MarkIn,
            MarkOut = c.HasDuration ? c.MarkOut : 0,
            Duration = c.DurationSec,
        }).ToList();

        var sink = new ExportProgressSink(progress);
        using var sinkRef = DotNetObjectReference.Create(sink);
        var r = await _js.InvokeAsync<JsResult>(
            "PageToMovieCut.exportMovieAsync",
            cancellationToken,
            payload,
            _audioUrl,
            sinkRef);
        if (!r.Success)
            throw new InvalidOperationException(r.Error ?? "Export failed.");
        return r.Url;
    }

    public async ValueTask DisposeAsync() => await ClearAudioAsync();
}
