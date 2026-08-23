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
    public CutMergeRuntime Cache { get; } = new();
    public string? MoviePreviewUrl { get; private set; }
    public string? PrefixPreviewUrl { get; private set; }
    public int PrefixClipCount { get; private set; }
    public bool HasCachedMoviePreview => CutComposeContract.CanReusePreview(MoviePreviewUrl);
    public CutMergePlan CurrentPlan { get; private set; } = new([], [], "", "", "");
    public CutMergeDiff LastDiff { get; private set; } =
        new(false, false, false, [], [], true, false);
    public IReadOnlyList<int> LastRebuiltScenes { get; private set; } = [];
    public IReadOnlyList<int> LastRebuiltJoins { get; private set; } = [];

    public CutComposeService(IJSRuntime js) => _js = js;

    public string? AudioFileName { get; private set; }
    public string? AudioUrl => _audioUrl;
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
        AudioFileName = CutClipNaming.FileNameOnly(file.Name);
        Music.SetFile(AudioFileName);
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
        Music.DisplayName = saved.DisplayName;
        Music.SetStart(saved.StartSec);
        Music.ApplyInOut(saved.MarkIn, saved.MarkOut > saved.MarkIn ? saved.MarkOut : Music.MarkOut);
        Music.SetVolumePercent(saved.VolumePercent);
        Music.SetFadeIn(saved.FadeInSec);
        Music.SetFadeOut(saved.FadeOutSec);
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
        if (TryReuseMovie(clips, texts, progress, onPrefix: null))
            return MoviePreviewUrl;
        return await ComposeAsync(clips, download: false, progress, cancellationToken, texts: texts);
    }

    public async Task<string?> PreviewMovieJitAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        Action<string, int> onPrefix,
        CancellationToken cancellationToken = default,
        IReadOnlyList<CutTextClip>? texts = null)
    {
        if (TryReuseMovie(clips, texts, progress, onPrefix))
            return MoviePreviewUrl;
        return await ComposeAsync(clips, download: false, progress, cancellationToken, onPrefix, texts);
    }

    public async Task<string?> ExportMovieAsync(
        IReadOnlyList<CutClip> clips,
        Action<int, string> progress,
        CancellationToken cancellationToken = default,
        IReadOnlyList<CutTextClip>? texts = null)
    {
        await PrepareExportJsAsync();
        if (TryReuseMovie(clips, texts, progress, onPrefix: null)
            && !string.IsNullOrWhiteSpace(MoviePreviewUrl))
        {
            await _js.InvokeVoidAsync("PageToMovieCut.downloadUrlAs", MoviePreviewUrl, CutPlayMerge.MovieFileName);
            return MoviePreviewUrl;
        }

        return await ComposeAsync(clips, download: true, progress, cancellationToken, texts: texts);
    }

    public void InvalidateMovie()
    {
        MoviePreviewUrl = null;
        PrefixPreviewUrl = null;
        PrefixClipCount = 0;
    }

    public void ClearMoviePreview()
    {
        InvalidateMovie();
        Cache.Clear();
        CurrentPlan = new([], [], "", "", "");
    }

    public void AttachExistingMerge(string url, int clipCount)
    {
        if (string.IsNullOrWhiteSpace(url) || clipCount <= 0)
            return;
        MoviePreviewUrl = url;
        PrefixPreviewUrl = url;
        PrefixClipCount = clipCount;
        Cache.PictureUrl ??= url;
    }

    /// <summary>
    /// Stop in-flight preview/JIT so Stop / second Play does not call a
    /// disposed progress sink or revoke blobs ffmpeg still holds.
    /// Waits for the compose gate and exclusive ffmpeg lock, then sweeps
    /// leftover <c>cut_*</c> MEMFS names.
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
        if (!CutTransport.CanPlay(clips))
            throw new InvalidOperationException("No current takes to export.");
        var ready = CutTransport.ComposeClips(clips);

        var payload = BuildComposePlan(ready, texts);
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
            throw new InvalidOperationException(CutComposeContract.OperatorComposeError(r.Error, download));
        RememberComposeResult(r, ready.Count);
        return r.Url;
    }

    internal bool TryReuseMovie(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts,
        Action<int, string> progress,
        Action<string, int>? onPrefix)
    {
        RefreshPlan(clips, texts);
        string? url;
        if (CutComposeContract.CanReuseExport(MoviePreviewUrl, LastDiff))
            url = MoviePreviewUrl;
        else if (LastDiff.MovieFresh && string.IsNullOrWhiteSpace(AudioFileName))
            url = Cache.PictureUrl;
        else
            url = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        MoviePreviewUrl = url;
        PrefixPreviewUrl = url;
        PrefixClipCount = clips.Count;
        progress(100, "Ready");
        onPrefix?.Invoke(url, clips.Count);
        return true;
    }

    internal JsComposePlan BuildComposePlan(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts)
    {
        RefreshPlan(clips, texts);
        var payload = new JsComposePlan
        {
            Clips = BuildExportPayload(clips, texts),
            ReuseMovieUrl = CutMergeCache.CanReuseMovie(LastDiff, MoviePreviewUrl)
                ? MoviePreviewUrl
                : null,
            ReusePictureUrl = CutMergeCache.CanReusePicture(LastDiff, Cache.PictureUrl)
                ? Cache.PictureUrl
                : null,
        };
        foreach (var scene in CurrentPlan.Scenes)
        {
            payload.Scenes.Add(new JsComposeScene
            {
                Scene = scene.Scene,
                First = scene.FirstClipIndex,
                Count = scene.ClipCount,
                Seconds = scene.Seconds,
                Url = Cache.SceneUrlIfFresh(scene.Scene, scene.Fingerprint),
            });
        }

        foreach (var join in CurrentPlan.Joins)
        {
            payload.Joins.Add(new JsComposeJoin
            {
                From = join.FromScene,
                To = join.ToScene,
                Kind = CutTransitionMap.WireName(join.Kind),
                Hold = join.HoldSec,
                Fade = join.FadeSec,
                Encodes = join.Encodes,
                Url = join.Encodes ? Cache.JoinUrlIfFresh(join.FromScene, join.Fingerprint) : null,
            });
        }

        return payload;
    }

    private void RefreshPlan(IReadOnlyList<CutClip> clips, IReadOnlyList<CutTextClip>? texts)
    {
        CurrentPlan = CutMergeCache.Build(clips, texts, AudioFileName, Music);
        LastDiff = CutMergeCache.Diff(CurrentPlan, Cache.Built);
    }

    private void RememberComposeResult(JsResult r, int clipCount)
    {
        MoviePreviewUrl = r.Url;
        PrefixPreviewUrl = r.Url;
        PrefixClipCount = clipCount;
        LastRebuiltScenes = r.RebuiltScenes.Count > 0 ? r.RebuiltScenes.ToList() : [];
        LastRebuiltJoins = r.RebuiltJoins.Count > 0 ? r.RebuiltJoins.ToList() : [];
        if (!string.IsNullOrWhiteSpace(r.PictureUrl))
            Cache.PictureUrl = r.PictureUrl;
        else if (string.IsNullOrWhiteSpace(AudioFileName) || string.IsNullOrWhiteSpace(Cache.PictureUrl))
            Cache.PictureUrl = r.Url;
        foreach (var scene in r.Scenes)
        {
            var row = CurrentPlan.Scenes.FirstOrDefault(s => s.Scene == scene.Id);
            if (row.Scene > 0 && !string.IsNullOrWhiteSpace(scene.Url))
                Cache.RememberScene(scene.Id, scene.Url, row.Fingerprint);
        }

        foreach (var join in r.Joins)
        {
            var row = CurrentPlan.Joins.FirstOrDefault(j => j.FromScene == join.Id);
            if (row.FromScene > 0 && !string.IsNullOrWhiteSpace(join.Url))
                Cache.RememberJoin(join.Id, join.Url, row.Fingerprint);
        }

        Cache.RememberPlan(CurrentPlan);
        LastDiff = CutMergeCache.Diff(CurrentPlan, Cache.Built);
    }

    internal static List<JsExportClip> BuildExportPayload(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts = null)
    {
        var payload = new List<JsExportClip>(clips.Count);
        for (var i = 0; i < clips.Count; i++)
            payload.Add(ToExportClip(clips, i));
        AttachOverlays(payload, clips, texts);
        return payload;
    }

    private static JsExportClip ToExportClip(IReadOnlyList<CutClip> clips, int index)
    {
        var c = clips[index];
        var hold = c.HoldsPicture;
        var next = index + 1 < clips.Count ? clips[index + 1] : null;
        var joinOut = "cut";
        var joinHold = 0d;
        if (next is not null)
        {
            var join = c.JoinToNext(next);
            joinOut = CutTransitionMap.WireName(join);
            joinHold = CutComposeContract.HoldSeconds(join);
        }

        return new JsExportClip
        {
            Url = hold ? null : c.PreviewUrl,
            Label = c.Label,
            FileName = c.FileName,
            MarkIn = c.MarkIn,
            MarkOut = c.HasDuration ? c.MarkOut : 0,
            Duration = c.DurationSec,
            Hold = hold,
            Windows = c.KeepWindows().Select(w => new JsKeepWindow { Start = w.Start, End = w.End }).ToList(),
            JoinOut = joinOut,
            JoinHold = joinHold,
            Card = CardPayload(c, clips),
        };
    }

    private static void AttachOverlays(
        List<JsExportClip> payload,
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts)
    {
        foreach (var overlay in CutTextTrack.OverlaysForCompose(clips, texts ?? []))
        {
            if (overlay.ClipIndex < 0 || overlay.ClipIndex >= payload.Count)
                continue;
            payload[overlay.ClipIndex].Texts.Add(ToJsOverlay(overlay));
        }
    }

    private static JsTextOverlay ToJsOverlay(CutTextOverlay overlay) =>
        new()
        {
            Text = overlay.Text,
            Start = overlay.LocalStart,
            Seconds = overlay.Seconds,
            Style = ToJsStyle(overlay.Style, overlay.Seconds),
        };

    private async Task<JsResult> InvokeComposeAsync<T>(
        string method,
        JsComposePlan payload,
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

    private async Task PrepareExportJsAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("PageToMovieCut.prepareExportAsync");
        }
        catch (JSException)
        {
            // Helper may already be gone; compose will surface a readable error.
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

    private object? MusicMixArg() =>
        string.IsNullOrWhiteSpace(_audioUrl) ? null : ToJsMix(_audioUrl, Music);

    internal static JsMusicMix ToJsMix(string url, CutMusic music)
    {
        ArgumentNullException.ThrowIfNull(music);
        var (inn, outt) = music.ResolvedInOut();
        return new JsMusicMix
        {
            Url = url,
            Start = music.StartSec,
            MarkIn = inn,
            MarkOut = outt,
            Volume = CutMusicMix.GainOf(music.VolumePercent),
            FadeIn = music.FadeInSec,
            FadeOut = music.FadeOutSec,
            Filter = CutMusicMix.ComplexFilter(music),
            FallbackFilter = CutMusicMix.MusicOnlyFilter(music),
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
            X = look.X,
            Bar = look.HasBar,
            FadeSec = look.FadeSec(holdSeconds),
            Font = CutTextStyle.WireFont(look.Font),
            Align = CutTextStyle.WireAlign(look.Align),
            CssFont = look.CssFont,
        };
    }

    public async ValueTask DisposeAsync()
    {
        MoviePreviewUrl = null;
        await AbortAsync();
        await ClearAudioAsync();
    }
}
