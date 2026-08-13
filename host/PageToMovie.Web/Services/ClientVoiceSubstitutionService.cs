using Microsoft.JSInterop;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Services;

/// <summary>
/// Browser-side orchestration of the "substitute my cloned voice" overlay, the client half of the
/// movie-wide voice-substitution feature. The server job already synthesized each dialogue line in
/// the character's cloned voice and saved the per-clip <see cref="ProjectVoiceAlignment"/>. This
/// service, per clip:
///   1. detects the real speech windows with ffmpeg silence detection (free, local) — unless the
///      alignment already has detected timestamps from a prior run, in which case detection is
///      skipped;
///   2. persists any newly detected windows back to the server (server matches them to the known
///      lines) so subsequent runs are fast;
///   3. overlays the cloned-voice clips onto the ORIGINAL clip audio at those windows, ducking the
///      original only during speech so ambience/music/SFX survive.
///
/// All ffmpeg work runs in <c>PageToMovieFfmpeg</c> (ffmpeg.wasm); the API host never spawns ffmpeg.
/// </summary>
public sealed class ClientVoiceSubstitutionService
{
    private readonly IJSRuntime _js;
    private readonly EngineApiClient _engine;
    private readonly ClientMediaFolderService _media;
    private readonly ClientVideoStitchService _stitch;
    private readonly ClientVoiceCaptureService _capture;

    public ClientVoiceSubstitutionService(
        IJSRuntime js,
        EngineApiClient engine,
        ClientMediaFolderService media,
        ClientVideoStitchService stitch,
        ClientVoiceCaptureService capture)
    {
        _js = js;
        _engine = engine;
        _media = media;
        _stitch = stitch;
        _capture = capture;
    }

    /// <summary>Result of stitching one scene and overlaying its single cloned-voice narration track.</summary>
    public sealed record SceneOverlayResult(int Scene, bool Success, string? Url, string? Error);

    /// <summary>Outcome of the full "dub this movie in my voice" flow.</summary>
    public sealed record DubMovieResult(bool Ok, string? DownloadUrl, int ClipsDubbed, int ClipsFailed, string? Error);

    /// <summary>
    /// Full "make this movie in my cloned voice" flow, tying the server + client halves together:
    /// start the voice-substitution job (cloned-voice TTS per line + alignment), wait for it, sync the
    /// audio + clips locally, overlay the cloned voice onto each clip, stitch the dubbed clips into one
    /// movie, and hand back a downloadable blob URL. Narrator by default (server defaults the CharKey).
    /// Requires the media folder to be connected (clips + synthesized audio live there).
    /// </summary>
    public async Task<DubMovieResult> DubMovieInMyVoiceAsync(
        string projectId,
        string? charKey = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        onProgress?.Invoke("Generating your voice for each line…");
        var job = await _engine.StartVoiceSubstitutionAsync(
            new StartVoiceSubstitutionRequest { ProjectId = projectId, CharKey = charKey ?? "" }, ct);
        if (job is null)
            return new DubMovieResult(false, null, 0, 0, "Could not start the voice job.");

        var terminal = await _engine.WaitForJobTerminalAsync(job.JobId, TimeSpan.FromMinutes(15), ct);
        var status = terminal?.Status ?? "";
        var jobOk = string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
        if (!jobOk)
            return new DubMovieResult(false, null, 0, 0, terminal?.Error ?? terminal?.Message ?? "The voice job did not finish.");

        onProgress?.Invoke("Syncing clips and audio…");
        try { await _media.SyncProjectMediaToClientAsync(projectId); } catch { /* best effort — overlay reads whatever is local */ }

        // Once per book: STT-verify the dialogue windows so the overlay can place confirmed lines
        // exactly where the original spoke. Built + cached the first time; reused thereafter. Must
        // match the character actually being dubbed — a cache built for a different character's solo
        // lines would mismatch this movie's placement windows.
        try
        {
            var cached = await _engine.GetVoiceCapturePhrasesAsync(projectId, ct);
            var cachedKey = string.IsNullOrWhiteSpace(cached?.CharKey) ? "Character_Narrator" : cached.CharKey.Trim();
            var wantKey = string.IsNullOrWhiteSpace(charKey) ? "Character_Narrator" : charKey.Trim();
            if (cached is null || !string.Equals(cachedKey, wantKey, StringComparison.OrdinalIgnoreCase))
                await _capture.BuildPhrasesAsync(projectId, onProgress, charKey, ct);
        }
        catch { /* best effort — overlay falls back to word-count/WPS placement if this fails */ }

        onProgress?.Invoke("Placing your voice over each scene…");
        var overlays = await ApplyAcrossMovieAsync(projectId, ct);
        var ordered = overlays
            .Where(o => o.Success && !string.IsNullOrWhiteSpace(o.Url))
            .OrderBy(o => o.Scene)
            .Select(o => o.Url ?? "")
            .ToList();
        var failed = overlays.Count(o => !o.Success);
        if (ordered.Count == 0)
            return new DubMovieResult(false, null, 0, failed,
                "No scenes could be voiced — check that the movie's clips are available and a voice has been recorded.");

        onProgress?.Invoke("Stitching your movie…");
        var stitched = await _stitch.ConcatAsync(ordered, ct);
        if (!stitched.Success || string.IsNullOrWhiteSpace(stitched.Url))
            return new DubMovieResult(false, null, ordered.Count, failed, stitched.Error ?? "Could not stitch the dubbed movie.");

        return new DubMovieResult(true, stitched.Url, ordered.Count, failed, null);
    }

    /// <summary>Download a produced (blob) movie URL to the user's device.</summary>
    public async Task DownloadAsync(string url, string fileName)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        await _js.InvokeVoidAsync("PageToMovieMedia.downloadFromUrlAsync", url,
            string.IsNullOrWhiteSpace(fileName) ? "movie-in-my-voice.mp4" : fileName);
    }

    /// <summary>
    /// Overlay the cloned voice across the movie, one continuous narration track per SCENE: stitch the
    /// scene's clips into a scene video, overlay the single scene voice track, and return one result
    /// per scene (final blob URL on success). Never throws for a single-scene failure — that scene is
    /// reported failed and the rest continue.
    /// </summary>
    public async Task<IReadOnlyList<SceneOverlayResult>> ApplyAcrossMovieAsync(
        string projectId, CancellationToken ct = default)
    {
        var results = new List<SceneOverlayResult>();
        var alignment = await _engine.GetVoiceAlignmentAsync(projectId, ct);
        if (alignment is null || alignment.SceneVoices.Count == 0)
            return results;

        // STT-verified (line ↔ window) pairs from the once-per-book phrase cache, keyed by scene. When
        // a line matches one of these, we trust its verified window outright instead of guessing.
        var phrases = await _engine.GetVoiceCapturePhrasesAsync(projectId, ct);
        var confidentByScene = BuildConfidentByScene(phrases);

        // ── Pass 1: stitch + silence-detect every scene, and learn the original narrator's words/second
        //    from the scenes we're confident about (detected windows line up 1:1 with the lines). ──
        var (prepared, wpsSamples) = await PrepareScenesAsync(projectId, alignment, ct);

        // The narrator's pace: median of the confident samples, else a sensible narration default.
        var speakerWps = MedianSpeakerWps(wpsSamples);
        await LogSpeakerPaceAsync(speakerWps, wpsSamples.Count);

        // ── Pass 2: place each line where the original spoke, at the learned pace, then overlay. ──
        await OverlayPreparedScenesAsync(projectId, prepared, confidentByScene, speakerWps, results, ct);
        return results;
    }

    private static Dictionary<int, List<VoiceCapturePhrase>> BuildConfidentByScene(VoiceCapturePhrases? phrases) =>
        phrases?.Phrases
            .Where(pp => pp.Confident)
            .GroupBy(pp => pp.Scene)
            .ToDictionary(g => g.Key, g => g.OrderBy(pp => pp.WindowStartSec).ToList())
            ?? new Dictionary<int, List<VoiceCapturePhrase>>();

    private async Task<(List<PreparedScene> Prepared, List<double> WpsSamples)> PrepareScenesAsync(
        string projectId, ProjectVoiceAlignment alignment, CancellationToken ct)
    {
        var prepared = new List<PreparedScene>();
        var wpsSamples = new List<double>();
        foreach (var sv in alignment.SceneVoices.OrderBy(v => v.Scene))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                prepared.Add(await PrepareOneSceneAsync(projectId, sv, wpsSamples, ct));
            }
            catch (Exception ex)
            {
                prepared.Add(new PreparedScene(sv.Scene) { Error = ex.Message });
            }
        }
        return (prepared, wpsSamples);
    }

    private async Task<PreparedScene> PrepareOneSceneAsync(
        string projectId, SceneVoiceTrack sv, List<double> wpsSamples, CancellationToken ct)
    {
        var clipUrls = await _stitch.CollectClipUrlsAsync(projectId, sv.Scene, ct: ct);
        if (clipUrls.Count == 0)
            return new PreparedScene(sv.Scene) { Error = "no clips on disk for scene" };

        var (sceneVideoUrl, stitchError) = await ResolveSceneVideoUrlAsync(clipUrls, ct);
        if (stitchError is not null)
            return new PreparedScene(sv.Scene) { Error = stitchError };

        // Mixed scene (mom baked in) or nothing to voice → passthrough with original audio kept.
        var lines = sv.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.VoiceAudioRelativePath))
            .OrderBy(l => l.Index)
            .ToList();
        if (sv.HasOtherSpeakers || lines.Count == 0)
            return new PreparedScene(sv.Scene) { SceneVideoUrl = sceneVideoUrl, Passthrough = true };

        var detect = await _js.InvokeAsync<JsSpeechDetectResult>(
            "PageToMovieFfmpeg.detectSpeechSegmentsAsync", ct, sceneVideoUrl!, new { });
        var windows = FilterSpeechWindows(detect);

        CollectWpsSamples(windows, lines, wpsSamples);

        return new PreparedScene(sv.Scene)
        {
            SceneVideoUrl = sceneVideoUrl,
            Lines = lines,
            Windows = windows,
            SceneDur = detect?.TotalSec ?? 0,
        };
    }

    private async Task<(string? Url, string? Error)> ResolveSceneVideoUrlAsync(
        IReadOnlyList<string> clipUrls, CancellationToken ct)
    {
        if (clipUrls.Count == 1)
            return (clipUrls[0], null);

        var stitched = await _stitch.ConcatAsync(clipUrls, ct);
        if (!stitched.Success || string.IsNullOrWhiteSpace(stitched.Url))
            return (null, stitched.Error ?? "scene stitch failed");
        return (stitched.Url, null);
    }

    private static List<JsSpeechWindow> FilterSpeechWindows(JsSpeechDetectResult? detect) =>
        (detect?.Segments ?? new List<JsSpeechWindow>())
            .Where(w => w.EndSec - w.StartSec >= 0.15)
            .OrderBy(w => w.StartSec)
            .ToList();

    private static void CollectWpsSamples(
        List<JsSpeechWindow> windows, List<SceneVoiceLine> lines, List<double> wpsSamples)
    {
        // Confident scene: windows line up 1:1 → each pair is a plausible words/second sample.
        if (windows.Count == lines.Count)
        {
            for (var j = 0; j < lines.Count; j++)
            {
                var dur = windows[j].EndSec - windows[j].StartSec;
                if (dur < 0.2) continue;
                var wps = WordCount(lines[j].Text) / dur;
                if (wps is >= 1.0 and <= 5.0) wpsSamples.Add(wps); // plausible narration pace
            }
        }
    }

    private static int WordCount(string? t) =>
        string.IsNullOrWhiteSpace(t) ? 1 : Math.Max(1, t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    private static double MedianSpeakerWps(List<double> wpsSamples)
    {
        var speakerWps = 2.3;
        if (wpsSamples.Count > 0)
        {
            wpsSamples.Sort();
            speakerWps = wpsSamples[wpsSamples.Count / 2];
        }
        return speakerWps;
    }

    private async Task LogSpeakerPaceAsync(double speakerWps, int sampleCount)
    {
        try { await _js.InvokeVoidAsync("console.log", $"[dub] learned speaker pace: {speakerWps:0.00} words/sec (from {sampleCount} confident window(s))"); }
        catch { /* logging only */ }
    }

    private async Task OverlayPreparedScenesAsync(
        string projectId,
        List<PreparedScene> prepared,
        Dictionary<int, List<VoiceCapturePhrase>> confidentByScene,
        double speakerWps,
        List<SceneOverlayResult> results,
        CancellationToken ct)
    {
        foreach (var p in prepared)
        {
            ct.ThrowIfCancellationRequested();
            if (p.Error is not null)
            {
                results.Add(new SceneOverlayResult(p.Scene, false, null, p.Error));
                continue;
            }
            if (p.Passthrough || p.Lines is null)
            {
                results.Add(new SceneOverlayResult(p.Scene, true, p.SceneVideoUrl, null));
                continue;
            }

            try
            {
                results.Add(await OverlayOnePreparedSceneAsync(projectId, p, confidentByScene, speakerWps, ct));
            }
            catch (Exception ex)
            {
                results.Add(new SceneOverlayResult(p.Scene, false, null, ex.Message));
            }
        }
    }

    private async Task<SceneOverlayResult> OverlayOnePreparedSceneAsync(
        string projectId,
        PreparedScene p,
        Dictionary<int, List<VoiceCapturePhrase>> confidentByScene,
        double speakerWps,
        CancellationToken ct)
    {
        var lines = p.Lines!;
        var windows = p.Windows ?? new List<JsSpeechWindow>();

        // STT-verified windows for this scene (consumed as matched, in order).
        var sceneConfident = confidentByScene.TryGetValue(p.Scene, out var cs)
            ? new List<VoiceCapturePhrase>(cs)
            : new List<VoiceCapturePhrase>();

        var (segs, confirmedUsed) = await BuildOverlaySegmentsAsync(
            projectId, lines, windows, p.SceneDur, speakerWps, sceneConfident);

        if (confirmedUsed > 0)
            await LogConfirmedPlacementAsync(p.Scene, confirmedUsed);

        if (segs.Count == 0)
            return new SceneOverlayResult(p.Scene, true, p.SceneVideoUrl, "voice audio not synced");

        // Mute the original clip audio and lay the placed + stretched lines onto silence
        // (muteBase) — one narrator (you), timed to where the original spoke, no double voice.
        var overlay = await _js.InvokeAsync<JsOverlayResult>(
            "PageToMovieFfmpeg.overlayVoiceSegmentsAsync",
            ct, p.SceneVideoUrl ?? "", segs.ToArray(), new { muteBase = true });

        if (overlay is { Success: true } && !string.IsNullOrWhiteSpace(overlay.Url))
            return new SceneOverlayResult(p.Scene, true, overlay.Url, null);
        else
            return new SceneOverlayResult(p.Scene, false, null, overlay?.Error ?? "overlay failed");
    }

    private async Task<(List<object> Segs, int ConfirmedUsed)> BuildOverlaySegmentsAsync(
        string projectId,
        List<SceneVoiceLine> lines,
        List<JsSpeechWindow> windows,
        double sceneDur,
        double speakerWps,
        List<VoiceCapturePhrase> sceneConfident)
    {
        var words = lines.Select(l => WordCount(l.Text)).ToList();
        var totalWords = (double)Math.Max(1, words.Sum());
        var totalSpeech = windows.Sum(w => Math.Max(0, w.EndSec - w.StartSec));
        var segs = new List<object>();
        var confirmedUsed = 0;
        double cumWords = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var lineUrl = await _media.GetLocalBlobUrlAsync(projectId, lines[i].VoiceAudioRelativePath ?? "");
            if (!string.IsNullOrWhiteSpace(lineUrl))
            {
                var (startSec, endSec, confirmed) = PlaceLine(
                    lines[i], i, words, windows, lines.Count, totalWords, totalSpeech,
                    sceneDur, speakerWps, cumWords, sceneConfident);
                if (confirmed)
                    confirmedUsed++;
                segs.Add(new { audioUrl = lineUrl, startSec, endSec });
            }
            cumWords += words[i];
        }
        return (segs, confirmedUsed);
    }

    private static (double StartSec, double EndSec, bool Confirmed) PlaceLine(
        SceneVoiceLine line,
        int i,
        List<int> words,
        List<JsSpeechWindow> windows,
        int lineCount,
        double totalWords,
        double totalSpeech,
        double sceneDur,
        double speakerWps,
        double cumWords,
        List<VoiceCapturePhrase> sceneConfident)
    {
        var confirmed = TakeConfidentMatch(sceneConfident, line.Text);
        if (confirmed is not null)
        {
            // Scribe verified this exact line is in this window → trust it outright.
            return (confirmed.WindowStartSec, confirmed.WindowEndSec, true);
        }

        double startSec;
        // WHERE the dialog starts: the real 1:1 window when detection agrees, else the
        // word-weighted slice of the combined speech timeline (or the scene if none).
        if (windows.Count == lineCount)
            startSec = windows[i].StartSec;
        else if (windows.Count > 0 && totalSpeech > 0.1)
            startSec = SpeechTimeToRealTime(windows, cumWords / totalWords * totalSpeech);
        else
            startSec = cumWords / totalWords * (sceneDur > 0 ? sceneDur : lineCount * 3.0);

        // HOW LONG the line should take at the narrator's LEARNED pace — the calibration
        // target the browser stretches the clone toward (clone → speaker pace). Immune to
        // inflated windows (trailing silence), which is what broke placement before.
        var endSec = startSec + Math.Max(0.4, words[i] / speakerWps);
        return (startSec, endSec, false);
    }

    /// <summary>A point on the concatenated-speech timeline → real clip time (walks the windows).</summary>
    private static double SpeechTimeToRealTime(List<JsSpeechWindow> windows, double speechT)
    {
        if (windows.Count == 0) return speechT;
        double acc = 0;
        foreach (var w in windows)
        {
            var dur = Math.Max(0, w.EndSec - w.StartSec);
            if (speechT <= acc + dur) return w.StartSec + (speechT - acc);
            acc += dur;
        }
        return windows[^1].EndSec;
    }

    private async Task LogConfirmedPlacementAsync(int scene, int confirmedUsed)
    {
        try { await _js.InvokeVoidAsync("console.log", $"[dub] scene {scene:D2}: {confirmedUsed} line(s) placed from STT-verified windows"); }
        catch { /* logging only */ }
    }

    /// <summary>A scene after Pass 1: stitched video + detected windows (or a passthrough/error), held
    /// so Pass 2 can place lines using the movie-wide learned speaker pace.</summary>
    private sealed class PreparedScene
    {
        public PreparedScene(int scene) => Scene = scene;
        public int Scene { get; }
        public string? SceneVideoUrl { get; set; }
        public bool Passthrough { get; set; }
        public string? Error { get; set; }
        public List<SceneVoiceLine>? Lines { get; set; }
        public List<JsSpeechWindow>? Windows { get; set; }
        public double SceneDur { get; set; }
    }

    /// <summary>Find (and consume) a confident phrase whose text matches the line, so each verified
    /// window is used at most once per scene.</summary>
    private static VoiceCapturePhrase? TakeConfidentMatch(List<VoiceCapturePhrase> pool, string? lineText)
    {
        var target = NormText(lineText);
        if (target.Length == 0) return null;
        for (var i = 0; i < pool.Count; i++)
        {
            if (NormText(pool[i].Text) == target)
            {
                var m = pool[i];
                pool.RemoveAt(i);
                return m;
            }
        }
        return null;
    }

    private static string NormText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var toks = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", toks);
    }

    private sealed class JsOverlayResult
    {
        public bool Success { get; set; } = false;
        public string? Url { get; set; } = null;
        public string? Error { get; set; } = null;
    }

}
