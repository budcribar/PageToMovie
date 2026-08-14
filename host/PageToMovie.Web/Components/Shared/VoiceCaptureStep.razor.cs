using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Shared;

public partial class VoiceCaptureStep
{

    /// <summary>The film to build the narrator's cloned voice for. Supplied by the host.</summary>
    [Parameter, EditorRequired] public string ProjectId { get; set; } = "";

    /// <summary>Which character this recording becomes the voice of. Defaults to the narrator —
    /// pass a different character key when the host resolved a non-narrator target (e.g. the story
    /// has no narrator and the user picked a speaking character instead).</summary>
    [Parameter] public string CharKey { get; set; } = "Character_Narrator";

    /// <summary>Optional label for empty-state copy ("Teacher" not "Character_Teacher").</summary>
    [Parameter] public string? CharacterLabel { get; set; }

    /// <summary>Fired once the cloned voice has been built and applied — host advances to the next step.</summary>
    [Parameter] public EventCallback OnComplete { get; set; }

    internal const int MaxPhrases = 8;

    internal string? _projectId;
    internal bool _loading = true;
    internal bool _busy;
    internal bool _done;
    internal string? _error;
    internal string? _status;

    internal List<VoiceCapturePhrase> _phrases = new();
    internal int _i;

    // Scene stitched video URLs, cached so we only stitch a scene once.
    internal readonly Dictionary<int, string> _sceneUrls = new();

    internal string? _originalUrl;   // extracted original segment (Listen)
    internal string? _takeUrl;       // user's recording as a data: URL
    internal int? _score;
    internal List<double>? _regions; // per-word rhythm match (0..1), for colouring

    internal bool _recording;
    internal bool _listening; // playing the original + scrolling it so the user sees the narrator's pace
    internal int _light; // 0 = off, 1 = red, 2 = yellow, 3 = green (go)
    internal double _ballDurationSec = 3;
    internal int _recordSession;
    internal int _teleSession; // bumped to (re)start the teleprompter scroll (Listen and on "Go")
    internal bool _teleStartPending; // set when a scroll should start on the next render (spans in DOM)
    internal bool _renderNarratorWave; // draw the narrator strip on the next render
    internal bool _renderYouWave;      // draw the "you" strip (coloured by match) on the next render

    internal readonly List<string> _kept = new(); // kept take data URLs

    internal VoiceCapturePhrase CurrentPhrase => _phrases[Math.Clamp(_i, 0, _phrases.Count - 1)];

    protected override async Task OnInitializedAsync()
    {
        // Ensure the login session is loaded before any identity-gated call.
        try { await Session.EnsureHydratedAsync(); } catch { /* ignore */ }
        _projectId = ProjectId;
        _loading = false;
        if (!string.IsNullOrEmpty(_projectId))
            await LoadPhrasesAsync();
    }

    internal async Task LoadPhrasesAsync()
    {
        _phrases = new(); _i = 0; _sceneUrls.Clear();
        _originalUrl = null; _takeUrl = null; _score = null; _regions = null;
        _kept.Clear(); _done = false; _error = null; _status = null;
        if (string.IsNullOrEmpty(_projectId)) { StateHasChanged(); return; }
        try
        {
            var data = await Engine.GetVoiceCapturePhrasesAsync(_projectId);
            // Ignore a cache built for a different character — old caches (before CharKey existed)
            // are implicitly the narrator's.
            var charKey = data?.CharKey;
            var cachedKey = string.IsNullOrWhiteSpace(charKey) ? "Character_Narrator" : charKey.Trim();
            if (string.Equals(cachedKey, CharKey, StringComparison.OrdinalIgnoreCase))
                _phrases = SelectPool(data?.Phrases);
        }
        catch (Exception ex) { _error = ex.Message; }
        if (_phrases.Count > 0) await PreparePhraseAsync();
        else await PreparePhrasesAsync();
        StateHasChanged();
    }

    internal static List<VoiceCapturePhrase> SelectPool(List<VoiceCapturePhrase>? all) =>
        (all ?? new())
            .Where(p => p.Confident)
            .OrderBy(p => p.Rank < 0 ? int.MaxValue : p.Rank)
            .ThenByDescending(p => p.DurationSec)
            .Take(MaxPhrases)
            .ToList();

    internal async Task PreparePhrasesAsync()
    {
        if (string.IsNullOrEmpty(_projectId)) return;
        _busy = true; _error = null; _status = $"Finding {Who()} dialogue…";
        StateHasChanged();
        try
        {
            var built = await Capture.BuildPhrasesAsync(_projectId, s => { _status = s; _ = InvokeAsync(StateHasChanged); }, CharKey);
            _phrases = SelectPool(built?.Phrases);
            if (_phrases.Count == 0)
                _status = string.IsNullOrWhiteSpace(_status)
                    ? $"Couldn't find {Who()} dialogue to match — make sure this movie's clips are in your media folder."
                    : _status;
            else { _i = 0; await PreparePhraseAsync(); }
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; StateHasChanged(); }
    }

    internal async Task PreparePhraseAsync()
    {
        _originalUrl = null;
        _takeUrl = null;
        _score = null; _regions = null;
        StateHasChanged();

        var p = CurrentPhrase;
        _ballDurationSec = p.DurationSec >= 0.4 ? p.DurationSec : ClientVoiceCaptureService.EstimatePhraseDurationSec(p.Text);
        try
        {
            var sceneUrl = await GetSceneUrlAsync(p.Scene);
            if (string.IsNullOrEmpty(sceneUrl))
                return;
            var res = await Js.InvokeAsync<ExtractUrlResult>(
                "PageToMovieFfmpeg.extractAudioSegmentToUrlAsync", sceneUrl, p.WindowStartSec, p.WindowEndSec);
            if (res is { Success: true }) { _originalUrl = res.Url; _renderNarratorWave = true; }
        }
        catch
        {
            // Script-only phrases have no original clip — record without Listen.
        }
        StateHasChanged();
    }

    internal Task<string?> GetSceneUrlAsync(int scene) =>
        ScenePlaybackSupport.GetSceneUrlAsync(Stitch, _projectId ?? "", scene, _sceneUrls);

    internal async Task ListenAsync()
    {
        if (string.IsNullOrEmpty(_originalUrl) || _listening || _recording || _light > 0) return;
        _listening = true;
        _teleSession++;
        _teleStartPending = true; // the scroll starts in OnAfterRenderAsync, once the spans render
        StateHasChanged();
        try { await Js.InvokeAsync<bool>("PageToMovieFfmpeg.playAudioAsync", _originalUrl); }
        catch { /* ignore playback errors */ }
        _listening = false;
        await InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_teleStartPending)
        {
            _teleStartPending = false;
            var (starts, ends) = BuildWordTimeline(SplitWords(CurrentPhrase.Text), _ballDurationSec);
            try { await Js.InvokeVoidAsync("PageToMovieFfmpeg.startWordTeleprompter", starts, ends, _ballDurationSec); }
            catch { /* ignore */ }
        }
        if (_renderNarratorWave && !string.IsNullOrEmpty(_originalUrl))
        {
            _renderNarratorWave = false;
            try { await Js.InvokeVoidAsync("PageToMovieFfmpeg.renderWaveformAsync", "ptm-wave-narrator", _originalUrl, (object?)null); }
            catch { /* ignore */ }
        }
        if (_renderYouWave && !string.IsNullOrEmpty(_takeUrl))
        {
            _renderYouWave = false;
            try { await Js.InvokeVoidAsync("PageToMovieFfmpeg.renderWaveformAsync", "ptm-wave-you", _takeUrl, _regions); }
            catch { /* ignore */ }
        }
    }

    internal async Task PlayYouAsync()
    {
        if (string.IsNullOrEmpty(_takeUrl) || _busy || _recording || _light > 0 || _listening) return;
        try { await Js.InvokeAsync<bool>("PageToMovieFfmpeg.playAudioAsync", _takeUrl); }
        catch { /* ignore playback errors */ }
    }

    internal async Task PlayBothAsync()
    {
        if (string.IsNullOrEmpty(_takeUrl) || string.IsNullOrEmpty(_originalUrl) || _busy || _recording || _light > 0 || _listening) return;
        try { await Js.InvokeAsync<bool>("PageToMovieFfmpeg.playOverlayAsync", _originalUrl, _takeUrl); }
        catch { /* ignore playback errors */ }
    }

    internal (double[] starts, double[] ends) BuildWordTimeline(List<string> disp, double durSec)
    {
        var n = disp.Count;
        var starts = new double[n];
        var ends = new double[n];
        if (n == 0) return (starts, ends);

        var words = CurrentPhrase.Words;
        if (words is null || words.Count == 0)
        {
            EqualSplitTimeline(starts, ends, n, durSec);
            return (starts, ends);
        }

        var (knownStart, knownEnd) = MapKnownTimes(disp, words, n);
        InterpolateStarts(starts, knownStart, n, durSec);
        ClampStarts(starts, n, durSec);
        ComputeEnds(starts, ends, knownEnd, n, durSec);
        return (starts, ends);
    }

    static void EqualSplitTimeline(double[] starts, double[] ends, int n, double durSec)
    {
        for (var i = 0; i < n; i++) { starts[i] = durSec * i / n; ends[i] = durSec * (i + 1) / n; }
    }

    static (double?[] knownStart, double?[] knownEnd) MapKnownTimes(
        List<string> disp, List<VoiceCaptureWord> words, int n)
    {
        var knownStart = new double?[n];
        var knownEnd = new double?[n];
        var si = 0;
        for (var di = 0; di < n; di++)
        {
            var dt = NormTok(disp[di]);
            if (dt.Length == 0) continue;
            for (var j = si; j < words.Count && j <= si + 4; j++)
            {
                if (NormTok(words[j].Text) == dt)
                {
                    knownStart[di] = Math.Max(0, words[j].StartSec);
                    knownEnd[di] = Math.Max(words[j].StartSec, words[j].EndSec);
                    si = j + 1;
                    break;
                }
            }
        }
        return (knownStart, knownEnd);
    }

    static void InterpolateStarts(double[] starts, double?[] knownStart, int n, double durSec)
    {
        int prevIdx = -1; double prevTime = 0;
        for (var di = 0; di < n; di++)
        {
            if (knownStart[di] is not double kt) continue;
            for (var g = prevIdx + 1; g < di; g++)
                starts[g] = prevTime + (kt - prevTime) * (g - prevIdx) / (di - prevIdx);
            starts[di] = kt; prevIdx = di; prevTime = kt;
        }
        for (var g = prevIdx + 1; g < n; g++)
            starts[g] = prevTime + (durSec - prevTime) * (g - prevIdx) / (n - prevIdx);
    }

    static void ClampStarts(double[] starts, int n, double durSec)
    {
        double last = 0;
        for (var i = 0; i < n; i++)
        {
            if (starts[i] < last) starts[i] = last;
            if (starts[i] > durSec) starts[i] = durSec;
            last = starts[i];
        }
    }

    static void ComputeEnds(double[] starts, double[] ends, double?[] knownEnd, int n, double durSec)
    {
        for (var i = 0; i < n; i++)
        {
            var cap = i + 1 < n ? starts[i + 1] : durSec;
            var e = knownEnd[i] ?? cap;
            if (e < starts[i]) e = starts[i];
            if (e > cap) e = cap;
            ends[i] = e;
        }
    }

    internal static string NormTok(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    internal async Task RecordAsync()
    {
        _error = null; _status = null; _takeUrl = null; _score = null; _regions = null;

        // Red → yellow → green. Recording (and the ball) start on green, so you're not caught mid-click.
        _light = 1; StateHasChanged(); await Task.Delay(650); // red
        _light = 2; StateHasChanged(); await Task.Delay(650); // yellow
        _light = 3; StateHasChanged();                        // green = go

        try
        {
            await Js.InvokeAsync<object>("PageToMovieVoiceCapture.start");
            _recording = true;
            _teleSession++;             // fresh teleprompter element on "Go"
            _teleStartPending = true;   // its paced scroll starts in OnAfterRenderAsync
            var session = ++_recordSession;
            StateHasChanged();
            _ = ClearLightAsync();
            _ = AutoStopAsync(session, (int)((_ballDurationSec + 1.0) * 1000));
        }
        catch (Exception ex) { _error = "Microphone failed: " + ex.Message; _recording = false; _light = 0; }
    }

    internal async Task ClearLightAsync()
    {
        await Task.Delay(650); // let the green light linger a beat once recording is live
        _light = 0;
        await InvokeAsync(StateHasChanged);
    }

    internal async Task AutoStopAsync(int session, int delayMs)
    {
        await Task.Delay(delayMs);
        if (_recording && session == _recordSession)
            await InvokeAsync(StopAndScoreAsync);
    }

    internal async Task StopAndScoreAsync()
    {
        if (!_recording) return;
        _busy = true;
        _recording = false;
        _light = 0;
        _recordSession++; // invalidate any pending auto-stop
        StateHasChanged();
        try
        {
            var result = await Js.InvokeAsync<VoiceCaptureStopResult>("PageToMovieVoiceCapture.stop");
            if (result is null || !result.Ok || string.IsNullOrEmpty(result.Base64))
            {
                _status = result?.Error ?? "No audio captured.";
                return;
            }
            var mime = MimeFor(result.FileName);
            _takeUrl = $"data:{mime};base64,{result.Base64}";
            StateHasChanged();

            if (!string.IsNullOrEmpty(_originalUrl))
            {
                // Real per-word boundaries (same timeline the teleprompter uses), not an equal split —
                // a word next to a comma pause is short, one that absorbs the pause is long, and equal
                // division scored each word against the wrong slice of audio once pacing was uneven.
                var words = SplitWords(CurrentPhrase.Text);
                var (wStarts, wEnds) = BuildWordTimeline(words, _ballDurationSec);
                var boundaries = new double[words.Count * 2];
                for (var wi = 0; wi < words.Count; wi++)
                {
                    boundaries[wi * 2] = _ballDurationSec > 0 ? wStarts[wi] / _ballDurationSec : 0;
                    boundaries[wi * 2 + 1] = _ballDurationSec > 0 ? wEnds[wi] / _ballDurationSec : 1;
                }
                var s = await Js.InvokeAsync<RhythmResult>(
                    "PageToMovieFfmpeg.analyzeRhythmMatchAsync", _originalUrl, _takeUrl, boundaries);
                if (s is { Success: true }) { _score = s.Score; _regions = s.Regions; _renderYouWave = true; }
            }
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; StateHasChanged(); }
    }

    internal async Task KeepAndNextAsync()
    {
        if (!string.IsNullOrEmpty(_takeUrl)) _kept.Add(_takeUrl);
        if (_i + 1 < _phrases.Count)
        {
            _i++;
            await PreparePhraseAsync();
        }
        else
        {
            await FinishAsync();
        }
    }

    internal async Task FinishAsync()
    {
        if (_kept.Count == 0 || string.IsNullOrEmpty(_projectId)) return;
        _busy = true; _error = null; _status = "Building your voice from your best takes…";
        StateHasChanged();
        try
        {
            // Trim each take's ragged edge silence, then rejoin with a natural ~0.4 s pause between
            // sentences — a clean clone sample that still has real between-sentence dead air.
            var bytes = await Js.InvokeAsync<byte[]>("PageToMovieFfmpeg.buildCloneSampleAsync", (object)_kept.ToArray(), 0.4);
            if (bytes is null || bytes.Length < 512) { _error = "Couldn't assemble your takes."; return; }

            var rel = "assets/characters/" + CharKey + "/voice_clone_sample.wav";
            if (!MediaFolder.IsConnected) await MediaFolder.TryReconnectAsync();
            try { await MediaFolder.SaveBytesAsync(_projectId, rel, bytes, promptToConnectFolder: false); } catch { /* best effort */ }

            await using var ms = new MemoryStream(bytes);
            await Engine.UploadVoiceCloneSampleAsync(_projectId, CharKey, ms, "voice_clone_sample.wav");

            _status = "Applying your voice…";
            StateHasChanged();
            var apply = await Engine.ApplyVoiceCloneAsync(_projectId, CharKey);
            _done = true;
            if (apply.Ok)
                _status = apply.UsedMock ? "Demo voice applied. You can make the movie." : "Voice applied. You can make the movie.";
            else
                _status = "Takes saved. " + (apply.Error ?? apply.Message ?? "Could not apply the voice.");
            StateHasChanged();
            if (apply.Ok)
                await OnComplete.InvokeAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; StateHasChanged(); }
    }

    internal static string MimeFor(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            _ => "audio/webm",
        };

    internal static List<string> SplitWords(string? t) =>
        string.IsNullOrWhiteSpace(t) ? new() : t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    internal string WordStyle(int wordIndex)
    {
        if (_recording || _regions is not { Count: > 0 } r) return "";
        var m = r[Math.Min(wordIndex, r.Count - 1)];
        int rr, gg, bb;
        if (m >= 0.7) { rr = 52; gg = 199; bb = 89; }
        else if (m >= 0.5) { rr = 255; gg = 204; bb = 0; }
        else { rr = 255; gg = 59; bb = 48; }
        return $"background: rgba({rr},{gg},{bb},.32); border-radius:4px; padding:0 3px;";
    }

    internal string Who()
    {
        if (!string.IsNullOrWhiteSpace(CharacterLabel))
            return CharacterLabel.Trim() + "'s";
        var bare = CastKindClassifier.StripPrefix(CharKey);
        if (string.IsNullOrWhiteSpace(bare) ||
            bare.Contains("narrator", StringComparison.OrdinalIgnoreCase))
            return "this character's";
        return bare.Replace('_', ' ') + "'s";
    }

    internal string LightPrompt
    {
        get
        {
            if (_light == 1) return "Get ready…";
            if (_light == 2) return "Set…";
            if (_light == 3) return "Go — read it!";
            if (_listening) return "This is how the narrator reads it — watch the words";
            return "● Read the word at the line";
        }
    }

    internal static string ScoreLabel(int s)
    {
        if (s >= 80) return "Great match!";
        if (s >= 60) return "Nice!";
        if (s >= 40) return "Good — try the rhythm again";
        return "Give it another go";
    }

    internal static string ScoreClass(int s)
    {
        if (s >= 60) return "text-success";
        if (s >= 40) return "text-warning";
        return "text-muted";
    }

    private sealed class ExtractUrlResult { public bool Success { get; set; } public string? Url { get; set; } public string? Error { get; set; } }
    private sealed class RhythmResult { public bool Success { get; set; } public int Score { get; set; } public List<double>? Regions { get; set; } public string? Error { get; set; } }
    private sealed class VoiceCaptureStopResult { public bool Ok { get; set; } public string? Error { get; set; } public string? Base64 { get; set; } public string? FileName { get; set; } }
}
