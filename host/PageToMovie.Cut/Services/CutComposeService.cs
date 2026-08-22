using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;

namespace PageToMovie.Cut.Services;

/// <summary>
/// Browser ffmpeg.wasm compose — concat trimmed clips, optional one-track audio mix.
/// Preview plays a blob URL; export downloads movie.mp4. Same queue, no download on preview.
/// </summary>
public sealed class CutComposeService : IAsyncDisposable
{
    /// <summary>S5693 cap — 8 MiB soundtrack is enough for V1 one-track mix.</summary>
    internal const long MaxAudioUploadBytes = 8_388_608;

    private readonly IJSRuntime _js;
    private string? _audioUrl;
    public string? MoviePreviewUrl { get; private set; }

    public CutComposeService(IJSRuntime js) => _js = js;

    public string? AudioFileName { get; private set; }
    public bool HasAudio => !string.IsNullOrWhiteSpace(_audioUrl);

    public async Task SetAudioFromBrowserFileAsync(IBrowserFile file)
    {
        if (file.Size > MaxAudioUploadBytes)
            throw new InvalidOperationException("Audio file is too large (max 8 MB).");
        await ClearAudioAsync();
        await using var stream = file.OpenReadStream(maxAllowedSize: MaxAudioUploadBytes);
        using var jsStream = new DotNetStreamReference(stream);
        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "audio/mpeg" : file.ContentType;
        var r = await _js.InvokeAsync<JsResult>("PageToMovieCut.createBlobUrlFromStream", jsStream, mime);
        if (!r.Success || string.IsNullOrWhiteSpace(r.Url))
            throw new InvalidOperationException(r.Error ?? "Could not read the audio file.");
        _audioUrl = r.Url;
        AudioFileName = file.Name;
    }

    public async Task<bool> TrySetAudioFromFolderAsync(string relativePath)
    {
        var r = await _js.InvokeAsync<JsResult>("PageToMovieCut.getFileBlobUrlAsync", relativePath);
        if (!r.Success || string.IsNullOrWhiteSpace(r.Url))
            return false;
        await ClearAudioAsync();
        _audioUrl = r.Url;
        AudioFileName = CutClipNaming.FileNameOnly(relativePath);
        return true;
    }

    public async Task ClearAudioAsync()
    {
        if (!string.IsNullOrWhiteSpace(_audioUrl))
        {
            try
            {
                await _js.InvokeVoidAsync("PageToMovieCut.revokeBlobUrl", _audioUrl);
            }
            catch (JSException)
            {
                // Blob may already be revoked on folder change or dispose.
            }
        }

        _audioUrl = null;
        AudioFileName = null;
    }

    public async Task<double> ReadMediaDurationAsync(ElementReference media) =>
        await _js.InvokeAsync<double>("PageToMovieCut.readMediaDuration", media);

    public async Task<string?> PreviewMovieAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        CancellationToken cancellationToken = default) =>
        await ComposeAsync(clips, download: false, progress, cancellationToken);

    public async Task<string?> ExportMovieAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        CancellationToken cancellationToken = default) =>
        await ComposeAsync(clips, download: true, progress, cancellationToken);

    public void ClearMoviePreview() => MoviePreviewUrl = null;

    private async Task<string?> ComposeAsync(
        IReadOnlyList<CutClip> clips,
        bool download,
        Action<int, string> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var missing = clips.FirstOrDefault(c => c.Missing || string.IsNullOrWhiteSpace(c.PreviewUrl));
        if (missing is not null)
            throw new InvalidOperationException(
                missing.MissingReason ?? $"Selected take file is missing: {missing.Label}.");
        if (clips.Count == 0)
            throw new InvalidOperationException("No clips to export.");

        var payload = new List<JsExportClip>(clips.Count);
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            var next = i + 1 < clips.Count ? clips[i + 1] : null;
            var windows = c.KeepWindows();
            payload.Add(new JsExportClip
            {
                Url = c.PreviewUrl,
                Label = c.Label,
                FileName = c.FileName,
                MarkIn = c.MarkIn,
                MarkOut = c.HasDuration ? c.MarkOut : 0,
                Duration = c.DurationSec,
                Windows = windows.Select(w => new JsKeepWindow { Start = w.Start, End = w.End }).ToList(),
                JoinOut = next is null ? "cut" : CutTransitionMap.WireName(c.JoinToNext(next)),
                Card = CardPayload(c, clips),
            });
        }

        var sink = new ExportProgressSink(progress);
        using var sinkRef = DotNetObjectReference.Create(sink);
        var method = download ? "PageToMovieCut.exportMovieAsync" : "PageToMovieCut.previewMovieAsync";
        var r = await _js.InvokeAsync<JsResult>(method, cancellationToken, payload, _audioUrl, sinkRef);
        if (!r.Success)
            throw new InvalidOperationException(r.Error ?? (download ? "Export failed." : "Play failed."));
        if (!download)
            MoviePreviewUrl = r.Url;
        return r.Url;
    }

    private static JsCard? CardPayload(CutClip clip, IReadOnlyList<CutClip> strip)
    {
        if (!clip.Card.Enabled || !clip.IsFirstOfScene(strip))
            return null;
        var text = string.IsNullOrWhiteSpace(clip.Card.Text) ? $"Scene {clip.Scene}" : clip.Card.Text.Trim();
        return new JsCard { Text = text, Seconds = clip.Card.HoldSeconds };
    }

    public async ValueTask DisposeAsync()
    {
        MoviePreviewUrl = null;
        await ClearAudioAsync();
    }
}
