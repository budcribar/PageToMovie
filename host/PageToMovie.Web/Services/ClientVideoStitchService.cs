using Microsoft.JSInterop;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Services;

/// <summary>
/// Browser-side video concat via <c>PageToMovieFfmpeg</c> (ffmpeg.wasm).
/// Offloads multi-clip / multi-scene preview stitch from the API host.
/// </summary>
public sealed class ClientVideoStitchService
{
    private readonly IJSRuntime _js;
    private readonly EngineApiClient _engine;
    private readonly ClientMediaFolderService? _media;

    public ClientVideoStitchService(
        IJSRuntime js,
        EngineApiClient engine,
        ClientMediaFolderService? media = null)
    {
        _js = js;
        _engine = engine;
        _media = media;
    }

    /// <summary>
    /// Ordered media URLs for the given scenes: prefer a fresh composite, else on-disk clips.
    /// </summary>
    public async Task<IReadOnlyList<string>> CollectSceneMediaUrlsAsync(
        string projectId,
        IReadOnlyList<int> sceneNumbers,
        IReadOnlyList<SceneSummary>? sceneList,
        IReadOnlySet<int>? staleScenes,
        CancellationToken ct = default)
    {
        // A server clip/composite fallback URL (ClipVideoUrl/CompositeVideoUrl) is only authorized when
        // BrowserMediaPath can append a fresh ?mt= media token. Ensure one exists BEFORE building URLs —
        // otherwise the raw ffmpeg fetch hits the authenticated endpoint with no token and gets a 401.
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        var urls = new List<string>();
        foreach (var sn in sceneNumbers.Distinct().OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            await AppendSceneMediaUrlsAsync(urls, projectId, sn, sceneList, ct);
        }

        return urls;
    }

    private async Task AppendSceneMediaUrlsAsync(
        List<string> urls,
        string projectId,
        int sn,
        IReadOnlyList<SceneSummary>? sceneList,
        CancellationToken ct)
    {
        var summary = sceneList?.FirstOrDefault(s => s.SceneNumber == sn);

        // 1. If scene has an explicit user/editor custom override, strictly use the custom scene composite
        if (summary?.IsUserOverride == true)
        {
            urls.Add(_engine.CompositeVideoUrl(projectId, sn));
            return;
        }

        // 2. Standard scene: fetch scene details and prefer atomic clip files for precision
        var detail = await TryGetSceneDetailAsync(projectId, sn, ct);

        if (detail?.IsUserOverride == true)
        {
            urls.Add(_engine.CompositeVideoUrl(projectId, sn));
            return;
        }

        if (await TryAppendOnDiskClipUrlsAsync(urls, projectId, sn, detail))
            return;

        // Fallback: if no atomic clips on disk for this scene, use composite if available
        if (summary?.CompositeExists == true || detail?.CompositeExists == true)
            urls.Add(_engine.CompositeVideoUrl(projectId, sn));
    }

    private async Task<SceneDetail?> TryGetSceneDetailAsync(string projectId, int sn, CancellationToken ct)
    {
        try
        {
            return (await _engine.GetSceneDetailAsync(projectId, sn, ct))?.Scene;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> TryAppendOnDiskClipUrlsAsync(
        List<string> urls, string projectId, int sn, SceneDetail? detail)
    {
        var clips = detail?.Clips?
            .Where(c => c.OnDisk)
            .OrderBy(c => c.ClipNumber)
            .ToList();

        if (clips is not { Count: > 0 })
            return false;

        foreach (var c in clips)
        {
            var fileName = string.IsNullOrWhiteSpace(c.FileName)
                ? $"scene_{sn:D2}_clip_{c.ClipNumber:D2}.mp4"
                : c.FileName;

            var local = _media is null
                ? null
                : await _media.GetLocalBlobUrlAsync(projectId, $"assets/video/{fileName}");
            // No local file: the server/provider copy of a video-extend clip is the combined video —
            // ResolveServerClipUrlAsync slices the previous clip's head off first.
            urls.Add(local ?? await ResolveServerClipUrlAsync(projectId, sn, c));
        }
        return true;
    }

    /// <summary>
    /// Multi-scene collection variant of <see cref="CollectSceneMediaUrlsAsync"/> that mixes each
    /// scene's own locally-synced background music into that scene's segment before returning —
    /// callers then concat the returned per-scene URLs into the final multi-scene video via
    /// <see cref="ConcatAsync"/>. Mixing per scene (not on the final concatenated result) is
    /// required since different scenes have different music; a scene whose stitch or mix step
    /// fails is skipped rather than aborting the whole collection.
    /// </summary>
    public async Task<IReadOnlyList<string>> CollectAndMixSceneSegmentsAsync(
        string projectId,
        IReadOnlyList<int> sceneNumbers,
        IReadOnlyList<SceneSummary>? sceneList,
        IReadOnlySet<int>? staleScenes,
        CancellationToken ct = default)
    {
        var infos = await CollectAndMixSceneSegmentInfosAsync(
            projectId, sceneNumbers, sceneList, staleScenes, ct).ConfigureAwait(false);
        return infos.Select(s => s.Url).ToList();
    }

    /// <summary>
    /// The URL to play/save for a clip that has no local file. A video-extend clip's provider copy is
    /// the COMBINED video (previous clip + this one) — the server says how long that head is
    /// (ClipSummary.ProviderLeadInSeconds); slice it off here with ffmpeg.wasm so the previous clip's
    /// footage and lines never play twice. Returns a blob URL (caller may revoke) or the plain URL.
    /// </summary>
    public async Task<string> ResolveServerClipUrlAsync(string projectId, int sceneNumber, ClipSummary clip, CancellationToken ct = default)
    {
        var url = _engine.ClipVideoUrl(projectId, sceneNumber, clip.ClipNumber);
        if (clip.ProviderLeadInSeconds is not { } leadIn || leadIn <= 0.1)
            return url;
        try
        {
            var probe = await _js.InvokeAsync<JsProbeResult>("PageToMovieFfmpeg.probeDurationAsync", ct, url);
            if (probe is { Success: true, Seconds: > 0 } && probe.Seconds > leadIn + 0.1)
            {
                var slice = await _js.InvokeAsync<JsTrimTailResult>("PageToMovieFfmpeg.trimTailAsync", ct, url, probe.Seconds - leadIn, null);
                if (slice is { Success: true } && !string.IsNullOrWhiteSpace(slice.Url))
                    return slice.Url;
            }
        }
        catch { /* fall through: playing the combined copy is wrong but not worse than nothing */ }
        return url;
    }

    /// <summary>Why the last segment collection dropped a scene (stitch/fetch failure), or null.</summary>
    public string? LastCollectError { get; private set; }

    /// <summary>On-disk clip URLs for one scene (ordered).</summary>
    public async Task<IReadOnlyList<string>> CollectClipUrlsAsync(
        string projectId,
        int sceneNumber,
        SceneDetail? detail = null,
        CancellationToken ct = default,
        bool includeServerFallback = true,
        IEnumerable<int>? clipNumbers = null)
    {
        // Ensure a fresh ?mt= media token before any ClipVideoUrl fallback (see CollectSceneMediaUrlsAsync).
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        if (detail is null)
            detail = (await _engine.GetSceneDetailAsync(projectId, sceneNumber, ct))?.Scene;
        if (detail?.Clips is null || detail.Clips.Count == 0)
            return Array.Empty<string>();

        var clipSet = clipNumbers?.ToHashSet();
        var list = new List<string>();
        var query = detail.Clips.Where(c => c.OnDisk);
        if (clipSet is not null && clipSet.Count > 0)
            query = query.Where(c => clipSet.Contains(c.ClipNumber));

        foreach (var clipRow in query.OrderBy(c => c.ClipNumber))
        {
            var clipNumber = clipRow.ClipNumber;
            var local = _media is null
                ? null
                : await _media.GetLocalBlobUrlAsync(
                    projectId, $"assets/video/scene_{sceneNumber:D2}_clip_{clipNumber:D2}.mp4");
            if (!string.IsNullOrEmpty(local))
                list.Add(local);
            else if (includeServerFallback)
                list.Add(await ResolveServerClipUrlAsync(projectId, sceneNumber, clipRow, ct));
        }
        return list;
    }

    public async Task<ClientStitchResult> ConcatAsync(
        IReadOnlyList<string> urls,
        CancellationToken ct = default)
    {
        if (urls is null || urls.Count == 0)
            return ClientStitchResult.Fail("No video URLs to combine");

        if (urls.Count == 1)
        {
            // Still hash so film_build gets a studio.sha256 for single-scene projects.
            string? sha = null;
            long? blen = null;
            try
            {
                var hash = await _js.InvokeAsync<JsHashResult>(
                    "PageToMovieFfmpeg.hashUrlAsync", ct, urls[0]);
                if (hash is { Success: true })
                {
                    sha = hash.Sha256;
                    blen = hash.ByteLength;
                }
            }
            catch { /* non-fatal */ }
            return ClientStitchResult.Ok(urls[0], count: 1, single: true, sha, blen);
        }

        try
        {
            var raw = await _js.InvokeAsync<JsConcatResult>(
                "PageToMovieFfmpeg.concatVideosAsync",
                ct,
                (object)urls.ToArray());

            if (raw is null)
                return ClientStitchResult.Fail("No response from browser stitch");

            if (!raw.Success)
                return ClientStitchResult.Fail(raw.Error ?? "Browser stitch failed");

            if (raw.Url is not { Length: > 0 } stitchedUrl)
                return ClientStitchResult.Fail("Stitch produced no video URL");

            return ClientStitchResult.Ok(
                stitchedUrl,
                raw.Count > 0 ? raw.Count : urls.Count,
                raw.Single,
                raw.Sha256,
                raw.ByteLength);
        }
        catch (JSException jex)
        {
            return ClientStitchResult.Fail(jex.Message);
        }
        catch (Exception ex)
        {
            return ClientStitchResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Same as <see cref="CollectAndMixSceneSegmentsAsync"/> but keeps scene numbers + relative paths
    /// for film_build EDL registration after full-film stitch.
    /// </summary>
    public async Task<IReadOnlyList<ClientWipSegment>> CollectAndMixSceneSegmentInfosAsync(
        string projectId,
        IReadOnlyList<int> sceneNumbers,
        IReadOnlyList<SceneSummary>? sceneList,
        IReadOnlySet<int>? staleScenes,
        CancellationToken ct = default)
    {
        var segments = new List<ClientWipSegment>();
        LastCollectError = null;
        foreach (var sn in sceneNumbers.Distinct().OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            var sceneUrls = await CollectSceneMediaUrlsAsync(projectId, new[] { sn }, sceneList, staleScenes, ct);
            if (sceneUrls.Count == 0)
                continue;

            string sceneUrl;
            if (sceneUrls.Count == 1)
                sceneUrl = sceneUrls[0];
            else
            {
                var concat = await ConcatAsync(sceneUrls, ct);
                if (concat is not { Success: true } || concat.Url is not { Length: > 0 } concatUrl)
                {
                    // Remember why: "no clips" and "could not stitch the clips" are different problems.
                    LastCollectError = $"S{sn:D2}: {concat?.Error ?? "browser stitch failed"}";
                    continue;
                }
                sceneUrl = concatUrl;
            }

            sceneUrl = await MixSceneMusicAsync(projectId, sceneUrl, sn, ct: ct);
            segments.Add(new ClientWipSegment
            {
                SceneNumber = sn,
                Url = sceneUrl,
                RelativeSrc = $"assets/video/scene_{sn:D2}.mp4",
            });
        }
        return segments;
    }

    /// <summary>
    /// After a full-film browser stitch: probe segment + total durations, ensure studio sha256,
    /// POST film_build.v1 to the API (non-fatal on failure).
    /// </summary>
    public async Task<(bool Ok, string? FilmId, string? Error)> RegisterFilmBuildAfterWipStitchAsync(
        string projectId,
        IReadOnlyList<ClientWipSegment> segments,
        ClientStitchResult stitch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || stitch.Url is null || !stitch.Success)
            return (false, null, "No stitch result");
        if (segments.Count == 0)
            return (false, null, "No segments");

        try
        {
            var sha = stitch.Sha256;
            long? byteLen = stitch.ByteLength;
            if (string.IsNullOrWhiteSpace(sha))
            {
                var hash = await _js.InvokeAsync<JsHashResult>("PageToMovieFfmpeg.hashUrlAsync", ct, stitch.Url);
                if (hash is { Success: true } && !string.IsNullOrWhiteSpace(hash.Sha256))
                {
                    sha = hash.Sha256;
                    byteLen = hash.ByteLength ?? byteLen;
                }
            }

            if (string.IsNullOrWhiteSpace(sha))
                return (false, null, "Could not hash stitched video");

            double t = 0;
            var edl = new List<object>();
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var dur = await ProbeDurationSecondsAsync(seg.Url, ct);
                if (dur <= 0) dur = 0.1; // keep monotonic timeline
                edl.Add(new
                {
                    index = i,
                    scene = seg.SceneNumber,
                    clip = (int?)null,
                    take = (int?)null,
                    tStart = t,
                    tEnd = t + dur,
                    src = seg.RelativeSrc,
                    srcSha256 = (string?)null,
                    sidecar = (string?)null,
                });
                t += dur;
            }

            // Prefer probed total; fall back to sum of segments
            var total = await ProbeDurationSecondsAsync(stitch.Url, ct);
            if (total <= 0) total = t;

            var body = new
            {
                studioSha256 = sha,
                durationSeconds = total,
                byteLength = byteLen,
                studioPath = "assets/movie_wip.mp4",
                assemblyWhere = "client",
                segments = edl,
            };

            var resp = await _engine.RegisterFilmBuildAsync(projectId, body, ct);
            return resp;
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<double> ProbeDurationSecondsAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;
        try
        {
            var probe = await _js.InvokeAsync<JsProbeResult>(
                "PageToMovieFfmpeg.probeDurationAsync", ct, url);
            return probe is { Success: true } && probe.Seconds > 0 ? probe.Seconds : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Layers a scene's locally-synced background-music segments under a video URL (volume
    /// ducking + fade-out), the client-side replacement for the old server ffmpeg mix. Returns
    /// <paramref name="videoUrl"/> unchanged if no music segments are synced locally for the scene.
    /// </summary>
    public async Task<string> MixSceneMusicAsync(
        string projectId,
        string videoUrl,
        int sceneNumber,
        int volumePercent = 20,
        CancellationToken ct = default)
    {
        _ = ct;
        if (_media is null || string.IsNullOrWhiteSpace(videoUrl))
            return videoUrl;

        var segmentUrls = await _media.GetSceneMusicSegmentUrlsAsync(projectId, sceneNumber);
        if (segmentUrls.Count == 0)
            return videoUrl;

        try
        {
            string musicUrl;
            if (segmentUrls.Count == 1)
            {
                musicUrl = segmentUrls[0];
            }
            else
            {
                var concat = await _js.InvokeAsync<JsConcatResult>(
                    "PageToMovieFfmpeg.concatAudioSegmentsAsync", (object)segmentUrls.ToArray());
                if (concat is not { Success: true } || concat.Url is not { Length: > 0 } concatUrl)
                    return videoUrl;
                musicUrl = concatUrl;
            }

            var mixed = await _js.InvokeAsync<JsConcatResult>(
                "PageToMovieFfmpeg.mixSceneAudioAsync", videoUrl, musicUrl, volumePercent);
            return mixed is { Success: true, Url: { Length: > 0 } mixedUrl } ? mixedUrl : videoUrl;
        }
        catch
        {
            return videoUrl; // mixing is best-effort — never block playback on a music failure
        }
    }

    public async Task RevokePreviewUrlAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("PageToMovieFfmpeg.revokePreviewUrl");
        }
        catch
        {
            // optional
        }
    }

    /// <summary>Browser duration probe (ffmpeg.wasm).</summary>
    /// <summary>Keep the last <paramref name="keepSeconds"/> of a video as a blob URL (ffmpeg.wasm); null on failure.</summary>
    public async Task<string?> TrimTailAsync(string url, double keepSeconds, CancellationToken ct = default)
    {
        try
        {
            var r = await _js.InvokeAsync<JsTrimTailResult>("PageToMovieFfmpeg.trimTailAsync", ct, url, keepSeconds, null);
            return r is { Success: true } && !string.IsNullOrWhiteSpace(r.Url) ? r.Url : null;
        }
        catch { return null; }
    }

    public async Task<double?> ProbeDurationAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var raw = await _js.InvokeAsync<JsProbeResult>(
                "PageToMovieFfmpeg.probeDurationAsync", ct, url);
            return raw is { Success: true, Seconds: > 0 } ? raw.Seconds : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sample JPEG frames for one clip (and previous tail when clip &gt; 1) for auto-review upload.
    /// Prefers local media-folder blobs, else authenticated clip proxy URLs.
    /// </summary>
    public async Task<(IReadOnlyList<ClipAutoReviewClientFrame> Frames, string? Error)> SampleAutoReviewFramesAsync(
        string projectId,
        int scene,
        int clip,
        CancellationToken ct = default)
    {
        var frames = new List<ClipAutoReviewClientFrame>();
        try
        {
            await AppendPreviousClipTailFramesAsync(projectId, scene, clip, frames, ct);

            var curUrl = await ResolveClipUrlAsync(projectId, scene, clip, ct);
            if (string.IsNullOrWhiteSpace(curUrl))
                return (frames, $"No video URL for S{scene:D2}C{clip:D2} (connect media folder or ensure clip exists).");

            var cur = await ExtractFramesRawAsync(curUrl, mode: "span", count: 3, ct);
            if (!cur.Success || cur.Frames is null || cur.Frames.Count == 0)
                return (frames, cur.Error ?? "Could not sample frames from current clip");

            AppendLabeledFrames(frames, cur.Frames, "CURRENT_CLIP");

            if (frames.Count == 0)
                return (frames, "No frames produced");
            return (frames, null);
        }
        catch (Exception ex)
        {
            return (frames, ex.Message);
        }
    }

    private async Task AppendPreviousClipTailFramesAsync(
        string projectId, int scene, int clip, List<ClipAutoReviewClientFrame> frames, CancellationToken ct)
    {
        if (clip > 1)
        {
            var prevUrl = await ResolveClipUrlAsync(projectId, scene, clip - 1, ct);
            if (!string.IsNullOrWhiteSpace(prevUrl))
            {
                var prev = await ExtractFramesRawAsync(prevUrl, mode: "tail", count: 3, ct);
                if (prev.Success && prev.Frames is { Count: > 0 })
                    AppendLabeledFrames(frames, prev.Frames, "PREVIOUS_CLIP_TAIL");
            }
        }
    }

    private static void AppendLabeledFrames(
        List<ClipAutoReviewClientFrame> frames, IReadOnlyList<JsFrameItem> source, string label)
    {
        foreach (var f in source)
        {
            if (string.IsNullOrWhiteSpace(f.Base64)) continue;
            frames.Add(new ClipAutoReviewClientFrame
            {
                Label = label,
                Mime = string.IsNullOrWhiteSpace(f.Mime) ? "image/jpeg" : f.Mime,
                Base64 = f.Base64,
            });
        }
    }

    /// <summary>Resolve a clip's playable URL: local media-folder blob when synced, else clip proxy.</summary>
    public async Task<string?> ResolveClipUrlAsync(
        string projectId, int scene, int clip, CancellationToken ct)
    {
        var rel = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
        if (_media is not null)
        {
            var local = await _media.GetLocalBlobUrlAsync(projectId, rel);
            if (!string.IsNullOrWhiteSpace(local))
                return local;
        }
        // No local copy → fall back to the authenticated clip proxy; ensure a fresh ?mt= token first.
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        return _engine.ClipVideoUrl(projectId, scene, clip);
    }

    public async Task<JsFramesResult> ExtractFramesRawAsync(
        string url, string mode, int count, CancellationToken ct = default)
    {
        try
        {
            var raw = await _js.InvokeAsync<JsFramesResult>(
                "PageToMovieFfmpeg.extractFramesAsync",
                ct,
                url,
                new { mode, count, maxWidth = 640, quality = 5 });
            return raw ?? new JsFramesResult { Success = false, Error = "No response from frame extract" };
        }
        catch (Exception ex)
        {
            return new JsFramesResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Render the deterministic end-credits card client-side and store it as the scene's clip: draw the
    /// exact strings on a canvas, roll them into a format-matched mp4 via ffmpeg.wasm, then save to the
    /// media folder (if connected) and upload to the server clip slot. The credits scene thereby becomes
    /// a normal on-disk clip the stitch concatenates — no video-gen call, no hallucinated text.
    /// </summary>
    public async Task<(bool Ok, string? Error)> RenderAndStoreCreditsClipAsync(
        ProjectClipRef clipRef, double durationSeconds,
        int width, int height, int fps, CancellationToken ct = default)
    {
        var projectId = clipRef.ProjectId;
        var scene = clipRef.Scene;
        var clip = clipRef.Clip;
        try
        {
            var content = await _engine.GetCreditsContentAsync(projectId, ct).ConfigureAwait(false);
            var res = await _js.InvokeAsync<JsCreditsResult>(
                "PageToMovieFfmpeg.renderCreditsClipAsync", ct, new
                {
                    title = content?.Title ?? "The End",
                    author = content?.Author ?? "",
                    softwareName = content?.SoftwareName ?? "PageToMovie",
                    siteUrl = content?.SiteUrl ?? "pagetomovie.com",
                    width,
                    height,
                    fps,
                    durationSec = durationSeconds <= 0 ? 5 : durationSeconds,
                }).ConfigureAwait(false);
            if (res is not { Success: true } || string.IsNullOrEmpty(res.Mp4Base64))
                return (false, res?.Error ?? "Credits card render failed");

            var bytes = Convert.FromBase64String(res.Mp4Base64);
            var relPath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";

            // Client storage is primary, same as every other clip type: save locally and register the
            // hash with the server (writes a .client.json sidecar — see POST .../media/register) rather
            // than treating the server upload as the main copy. Best-effort — the register call can
            // fail (offline, no folder), so this alone is never sufficient; see the upload fallback below.
            if (_media is not null)
            {
                var (savedOk, _, sha256, sizeBytes, _) =
                    await _media.SaveBytesAsync(projectId, relPath, bytes).ConfigureAwait(false);
                if (savedOk && !string.IsNullOrWhiteSpace(sha256))
                {
                    try
                    {
                        await _engine.RegisterMediaAsync(projectId, new MediaRegisterRequest
                        {
                            RelativePath = relPath,
                            Sha256 = sha256,
                            SizeBytes = sizeBytes,
                            Kind = "credits",
                            Scene = scene,
                            Clip = clip,
                        }, ct).ConfigureAwait(false);
                    }
                    catch { /* best effort — the upload below is the real safety net */ }
                }
            }

            // Always also ensure the server has the real bytes. The credits card is a few seconds of
            // simple canvas-rendered text — trivially small next to a real AI-generated clip — so
            // uploading it unconditionally is cheap, and it's the only way to guarantee correctness for
            // keep-media-on-server projects (whose forks depend on this clip being server-resolvable)
            // without the client needing to know that flag. A failed upload must NOT be reported as
            // success (it silently was, before: this card would never show up on-disk or play in the
            // assembled movie, with no visible error at all).
            var (uploaded, uploadError) = await _engine.UploadClipWithResultAsync(projectId, scene, clip, bytes, ct)
                .ConfigureAwait(false);
            if (!uploaded)
                return (false, $"Rendered the credits card but could not save it: {uploadError}");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private sealed class JsCreditsResult
    {
        public bool Success { get; set; } = false;
        public string? Mp4Base64 { get; set; } = null;
        public long ByteLength { get; set; } = 0;
        public string? Error { get; set; } = null;
    }

    private sealed class JsConcatResult
    {
        public bool Success { get; set; } = false;
        public string? Url { get; set; } = null;
        public string? Error { get; set; } = null;
        public int Count { get; set; } = 0;
        public bool Single { get; set; } = false;
        public string? Sha256 { get; set; } = null;
        public long? ByteLength { get; set; } = null;
    }

    private sealed class JsProbeResult
    {
        public bool Success { get; set; } = false;
        public double Seconds { get; set; } = 0;
        public string? Error { get; set; } = null;
    }

    private sealed class JsTrimTailResult
    {
        public bool Success { get; set; } = false;
        public string? Url { get; set; } = null;
        public double SourceDurationSec { get; set; } = 0;
        public double KeptSec { get; set; } = 0;
        public string? Error { get; set; } = null;
    }

    public sealed class JsFramesResult
    {
        public bool Success { get; set; } = false;
        public string? Error { get; set; } = null;
        public List<JsFrameItem>? Frames { get; set; } = null;
    }

    public sealed class JsFrameItem
    {
        public string? Base64 { get; set; } = null;
        public string? Mime { get; set; } = null;
    }
}

public sealed class ClientStitchResult
{
    public bool Success { get; init; }
    public string? Url { get; init; }
    public string? Error { get; init; }
    public int Count { get; init; }
    public bool Single { get; init; }
    /// <summary>SHA-256 of stitched bytes when computed in the browser.</summary>
    public string? Sha256 { get; init; }
    public long? ByteLength { get; init; }

    public static ClientStitchResult Ok(
        string url, int count = 1, bool single = false, string? sha256 = null, long? byteLength = null) =>
        new()
        {
            Success = true,
            Url = url,
            Count = count,
            Single = single,
            Sha256 = sha256,
            ByteLength = byteLength,
        };

    public static ClientStitchResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>One scene segment used in a full-film client stitch (URL + path for film_build EDL).</summary>
public sealed class ClientWipSegment
{
    public int SceneNumber { get; init; }
    public string Url { get; init; } = "";
    public string RelativeSrc { get; init; } = "";
}

public sealed class JsHashResult
{
    public bool Success { get; set; } = false;
    public string? Sha256 { get; set; } = null;
    public long? ByteLength { get; set; } = null;
    public string? Error { get; set; } = null;
}
