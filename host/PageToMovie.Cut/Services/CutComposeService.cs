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
    private int _composeGen;
    private string? _audioUrl;
    public CutMusic Music { get; } = new();
    public string? MoviePreviewUrl { get; private set; }
    public string? PrefixPreviewUrl { get; private set; }
    public int PrefixClipCount { get; private set; }
    public bool HasCachedMoviePreview => CutComposeContract.CanReusePreview(MoviePreviewUrl);

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
        Music.SetFile(file.Name);
        await ProbeMusicDurationAsync();
    }

    public async Task<bool> TrySetAudioFromFolderAsync(string relativePath)
    {
        var r = await _js.InvokeAsync<JsResult>("PageToMovieCut.getFileBlobUrlAsync", relativePath);
        if (!r.Success || string.IsNullOrWhiteSpace(r.Url))
            return false;
        await ClearAudioAsync();
        _audioUrl = r.Url;
        AudioFileName = CutClipNaming.FileNameOnly(relativePath);
        Music.SetFile(AudioFileName);
        await ProbeMusicDurationAsync();
        return true;
    }

    public void ApplySavedMusic(CutMusic saved)
    {
        Music.SetStart(saved.StartSec);
        Music.ApplyInOut(saved.MarkIn, saved.MarkOut > saved.MarkIn ? saved.MarkOut : Music.MarkOut);
    }

    public async Task ProbeMusicDurationAsync()
    {
        if (string.IsNullOrWhiteSpace(_audioUrl))
            return;
        try
        {
            var seconds = await _js.InvokeAsync<double>("PageToMovieCut.probeUrlDuration", _audioUrl);
            Music.SetDuration(seconds);
        }
        catch (JSException)
        {
            // duration is optional until Play/export probes again
        }
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
        Music.Clear();
    }

    public async Task<double> ReadMediaDurationAsync(ElementReference media) =>
        await _js.InvokeAsync<double>("PageToMovieCut.readMediaDuration", media);

    public async Task<string?> PreviewMovieAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        CancellationToken cancellationToken = default,
        IReadOnlyList<CutTextClip>? texts = null)
    {
        if (HasCachedMoviePreview)
        {
            progress(100, "Ready");
            return MoviePreviewUrl;
        }

        return await ComposeAsync(clips, download: false, progress, cancellationToken, texts: texts);
    }

    public async Task<string?> PreviewMovieJitAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        Action<string, int> onPrefix,
        CancellationToken cancellationToken = default,
        IReadOnlyList<CutTextClip>? texts = null)
    {
        if (HasCachedMoviePreview)
        {
            var cached = MoviePreviewUrl ?? "";
            PrefixPreviewUrl = cached;
            PrefixClipCount = clips.Count;
            progress(100, "Ready");
            onPrefix(cached, clips.Count);
            return cached;
        }

        return await ComposeAsync(clips, download: false, progress, cancellationToken, onPrefix, texts);
    }

    public async Task<string?> ExportMovieAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        CancellationToken cancellationToken = default,
        IReadOnlyList<CutTextClip>? texts = null) =>
        await ComposeAsync(clips, download: true, progress, cancellationToken, texts: texts);

    public void ClearMoviePreview()
    {
        MoviePreviewUrl = null;
        PrefixPreviewUrl = null;
        PrefixClipCount = 0;
    }

    public void AttachExistingMerge(string url, int clipCount)
    {
        if (string.IsNullOrWhiteSpace(url) || clipCount <= 0)
            return;
        MoviePreviewUrl = url;
        PrefixPreviewUrl = url;
        PrefixClipCount = clipCount;
    }

    /// <summary>
    /// Stop in-flight preview/JIT so Stop / second Play does not call a
    /// disposed progress sink or revoke blobs ffmpeg still holds.
    /// </summary>
    public Task AbortAsync()
    {
        Interlocked.Increment(ref _composeGen);
        return AbortComposeJsAsync();
    }

    private async Task<string?> ComposeAsync(
        IReadOnlyList<CutClip> clips,
        bool download,
        Action<int, string> progress,
        CancellationToken cancellationToken,
        Action<string, int>? onPrefix = null,
        IReadOnlyList<CutTextClip>? texts = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ready = CutTransport.PlayableClips(clips);
        if (ready.Count == 0)
            throw new InvalidOperationException("No current takes to export.");

        var payload = BuildExportPayload(ready, texts);
        string method;
        if (download)
            method = "PageToMovieCut.exportMovieAsync";
        else if (onPrefix is null)
            method = "PageToMovieCut.previewMovieAsync";
        else
            method = "PageToMovieCut.previewMovieJitAsync";
        var r = onPrefix is null
            ? await InvokeComposeAsync(method, payload, new ExportProgressSink(progress), cancellationToken)
            : await InvokeComposeAsync(
                method,
                payload,
                new JitPreviewSink(progress, (url, count) =>
                {
                    PrefixPreviewUrl = url;
                    PrefixClipCount = count;
                    onPrefix(url, count);
                }),
                cancellationToken);
        if (!r.Success)
            throw new InvalidOperationException(r.Error ?? (download ? "Export failed." : "Play failed."));
        MoviePreviewUrl = r.Url;
        PrefixPreviewUrl = r.Url;
        PrefixClipCount = ready.Count;
        return r.Url;
    }

    internal static List<JsExportClip> BuildExportPayload(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts = null)
    {
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

        foreach (var overlay in CutTextTrack.OverlaysForCompose(clips, texts ?? []))
        {
            if (overlay.ClipIndex < 0 || overlay.ClipIndex >= payload.Count)
                continue;
            payload[overlay.ClipIndex].Texts.Add(new JsTextOverlay
            {
                Text = overlay.Text,
                Start = overlay.LocalStart,
                Seconds = overlay.Seconds,
                Style = ToJsStyle(overlay.Style, overlay.Seconds),
            });
        }

        return payload;
    }

    private async Task<JsResult> InvokeComposeAsync<T>(
        string method,
        List<JsExportClip> payload,
        T sink,
        CancellationToken cancellationToken)
        where T : class, IDisposable
    {
        var gen = Interlocked.Increment(ref _composeGen);
        var sinkRef = DotNetObjectReference.Create(sink);
        try
        {
            return await _js.InvokeAsync<JsResult>(method, cancellationToken, payload, MusicMixArg(), sinkRef);
        }
        catch (OperationCanceledException)
        {
            if (gen == Volatile.Read(ref _composeGen))
                await AbortComposeJsAsync();
            throw;
        }
        finally
        {
            sink.Dispose();
            sinkRef.Dispose();
        }
    }

    private async Task AbortComposeJsAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("PageToMovieCut.abortCompose");
        }
        catch (JSException)
        {
            // Circuit or helper may already be gone.
        }
    }

    private object? MusicMixArg()
    {
        if (string.IsNullOrWhiteSpace(_audioUrl))
            return null;
        var (inn, outt) = Music.ResolvedInOut();
        return new JsMusicMix
        {
            Url = _audioUrl,
            Start = Music.StartSec,
            MarkIn = inn,
            MarkOut = outt,
        };
    }

    private static JsCard? CardPayload(CutClip clip, IReadOnlyList<CutClip> strip)
    {
        if (!clip.Card.Enabled || !clip.IsFirstOfScene(strip))
            return null;
        var text = string.IsNullOrWhiteSpace(clip.Card.Text) ? $"Scene {clip.Scene}" : clip.Card.Text.Trim();
        var hold = clip.Card.HoldSeconds;
        return new JsCard { Text = text, Seconds = hold, Style = ToJsStyle(clip.Card.Style, hold) };
    }

    internal static JsTextStyle ToJsStyle(CutTextStyle? style, double holdSeconds)
    {
        var look = style ?? new CutTextStyle();
        return new JsTextStyle
        {
            FontPx = look.FontPx,
            Color = look.ColorHex,
            Y = look.Y,
            Bar = look.HasBar,
            FadeSec = look.FadeSec(holdSeconds),
        };
    }

    public async ValueTask DisposeAsync()
    {
        MoviePreviewUrl = null;
        await AbortAsync();
        await ClearAudioAsync();
    }
}
