using Microsoft.JSInterop;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Services;

/// <summary>
/// Builds the once-per-book voice-capture phrase cache: for each narrator-only scene, stitch the
/// clips, detect speech windows, and for each window extract its audio and run it through Scribe
/// (STT) to VERIFY it contains the expected blueprint line. Confident (verified) windows become both
/// capture material and a trusted line↔window mapping for the dub overlay. Result persists to the
/// project as <c>assets/voice_capture/phrases.json</c>.
///
/// This is deliberately the slow, one-time path (one STT call per window). The capture UI and the
/// dub overlay read the cached result; they never re-run this.
/// </summary>
public sealed class ClientVoiceCaptureService
{
    private readonly IJSRuntime _js;
    private readonly EngineApiClient _engine;
    private readonly ClientVideoStitchService _stitch;

    public ClientVoiceCaptureService(IJSRuntime js, EngineApiClient engine, ClientVideoStitchService stitch)
    {
        _js = js;
        _engine = engine;
        _stitch = stitch;
    }

    /// <summary>Word-overlap (transcript vs expected line) at/above which a window is "confident".</summary>
    public const double ConfidenceThreshold = 0.7;

    /// <summary>
    /// Run the verification pass and save the phrase cache. Returns the built set (also persisted).
    /// Scans every scene that has this character's lines, including mixed scenes. STT keeps only
    /// windows that match those lines — other speakers stay unmatched. Solo-only skipped Teacher
    /// in first-person stories (kids in every scene).
    /// </summary>
    /// <param name="charKey">Which character to build phrases for. Null/omitted defaults to the
    /// narrator.</param>
    public async Task<VoiceCapturePhrases?> BuildPhrasesAsync(
        string projectId, Action<string>? onProgress = null, string? charKey = null, CancellationToken ct = default)
    {
        var wantKey = string.IsNullOrWhiteSpace(charKey) ? "Character_Narrator" : charKey.Trim();
        var scenes = await LoadTargetScenesAsync(projectId, wantKey, ct);
        if (scenes.Count == 0)
        {
            onProgress?.Invoke("No spoken lines for this character in the shot plan.");
            return null;
        }

        var phrases = new VoiceCapturePhrases
        {
            ProjectId = projectId,
            ConfidenceThreshold = ConfidenceThreshold,
            CharKey = wantKey,
        };

        var parentId = await TryParentProjectIdAsync(projectId, ct);
        foreach (var sc in ScenesWithTargetLines(scenes))
            await ProcessSoloSceneAsync(phrases, sc, parentId, onProgress, ct);

        if (CountConfident(phrases) == 0)
        {
            onProgress?.Invoke("Using lines from the screenplay — record them in your voice.");
            AddScriptFallbackPhrases(phrases, scenes);
        }

        RankConfidentPhrases(phrases);
        var confidentCount = CountConfident(phrases);
        onProgress?.Invoke($"Verified {confidentCount} of {phrases.Phrases.Count} phrase(s).");
        await _engine.SaveVoiceCapturePhrasesAsync(projectId, phrases, ct);
        return phrases;
    }

    private async Task ProcessSoloSceneAsync(
        VoiceCapturePhrases phrases,
        EngineApiClient.NarratorSceneLinesDto sc,
        string? parentProjectId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expectedLines = ExpectedNonEmptyLines(sc);
        if (expectedLines.Count == 0)
            return;

        onProgress?.Invoke($"Scanning scene {sc.Scene:D2}…");

        var clipUrls = await _stitch.CollectClipUrlsAsync(phrases.ProjectId, sc.Scene, ct: ct);
        if (clipUrls.Count == 0 && !string.IsNullOrWhiteSpace(parentProjectId))
            clipUrls = await _stitch.CollectClipUrlsAsync(parentProjectId, sc.Scene, ct: ct);
        if (clipUrls.Count == 0)
            return;

        var sceneVideoUrl = await ResolveSceneVideoUrlAsync(clipUrls, ct);
        if (sceneVideoUrl is null)
            return;

        var detect = await _js.InvokeAsync<JsSpeechDetectResult>(
            "PageToMovieFfmpeg.detectSpeechSegmentsAsync", ct, sceneVideoUrl, new { });
        var windows = FilterMinDurationWindows(detect);
        var perWindow = await MatchWindowsFirstPassAsync(
            sceneVideoUrl, windows, expectedLines, sc.Scene, onProgress, ct);
        MergeMatchedWindows(phrases, sc, perWindow);
    }

    private async Task<string?> ResolveSceneVideoUrlAsync(IReadOnlyList<string> clipUrls, CancellationToken ct)
    {
        if (clipUrls.Count == 1)
            return clipUrls[0];

        var stitched = await _stitch.ConcatAsync(clipUrls, ct);
        if (!stitched.Success || string.IsNullOrWhiteSpace(stitched.Url))
            return null;
        return stitched.Url;
    }

    private async Task<List<WindowMatch>> MatchWindowsFirstPassAsync(
        string sceneVideoUrl,
        List<JsSpeechWindow> windows,
        List<string> expectedLines,
        int scene,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var perWindow = new List<WindowMatch>();
        for (var wi = 0; wi < windows.Count; wi++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"Scene {scene:D2}: verifying phrase {wi + 1}/{windows.Count}…");
            var match = await MatchWindowAsync(sceneVideoUrl, windows[wi], expectedLines, ct);
            if (match is not null)
                perWindow.Add(match);
        }
        return perWindow;
    }

    private async Task<WindowMatch?> MatchWindowAsync(
        string sceneVideoUrl,
        JsSpeechWindow w,
        IReadOnlyList<string> expectedLines,
        CancellationToken ct)
    {
        byte[]? audio;
        try
        {
            audio = await _js.InvokeAsync<byte[]>(
                "PageToMovieFfmpeg.extractAudioSegmentAsync", ct, sceneVideoUrl, w.StartSec, w.EndSec);
        }
        catch
        {
            return null;
        }
        if (audio is null || audio.Length < 256)
            return null;

        var transcript = await _engine.TranscribeSegmentAsync(audio, "segment.wav", ct);
        var heard = (transcript?.Text ?? "").Trim();
        if (heard.Length == 0)
            return null;

        var timedWords = CollectTimedWords(transcript);
        var bestLine = BestMatchingLine(expectedLines, heard);
        return new WindowMatch(w.StartSec, w.EndSec, bestLine, heard, timedWords);
    }

    private static string BestMatchingLine(IReadOnlyList<string> expectedLines, string heard)
    {
        var bestLine = "";
        var bestScore = 0.0;
        foreach (var line in expectedLines)
        {
            var s = WordOverlap(line, heard);
            if (s > bestScore)
            {
                bestScore = s;
                bestLine = line;
            }
        }
        return bestLine;
    }

    private static void MergeMatchedWindows(
        VoiceCapturePhrases phrases,
        EngineApiClient.NarratorSceneLinesDto sc,
        List<WindowMatch> perWindow)
    {
        const double maxMergeGapSec = 1.5;
        var idx = 0;
        while (idx < perWindow.Count)
        {
            if (string.IsNullOrEmpty(perWindow[idx].Line))
            {
                idx++;
                continue;
            }

            var startI = idx;
            var endI = FindMergeEnd(perWindow, startI, maxMergeGapSec);
            var first = perWindow[startI];
            var last = perWindow[endI];
            var heard = JoinHeard(perWindow, startI, endI);
            var mergedWords = MergeWindowWords(perWindow, startI, endI, first.StartSec);
            var score = WordOverlap(first.Line, heard);

            phrases.Phrases.Add(new VoiceCapturePhrase
            {
                Scene = sc.Scene,
                Clip = 0,
                WindowStartSec = first.StartSec,
                WindowEndSec = last.EndSec,
                Text = first.Line,
                TranscribedText = heard,
                MatchScore = Math.Round(score, 3),
                Confident = score >= ConfidenceThreshold,
                Words = mergedWords.Count > 0 ? mergedWords : null,
            });
            idx = endI + 1;
        }
    }

    private static int FindMergeEnd(List<WindowMatch> perWindow, int startI, double maxMergeGapSec)
    {
        var endI = startI;
        while (endI + 1 < perWindow.Count &&
               perWindow[endI + 1].Line == perWindow[startI].Line &&
               perWindow[endI + 1].StartSec - perWindow[endI].EndSec < maxMergeGapSec)
            endI++;
        return endI;
    }

    private static string JoinHeard(List<WindowMatch> perWindow, int startI, int endI)
    {
        var parts = new List<string>();
        for (var k = startI; k <= endI; k++)
            parts.Add(perWindow[k].Heard);
        return string.Join(" ", parts);
    }

    private static List<VoiceCaptureWord> MergeWindowWords(
        List<WindowMatch> perWindow, int startI, int endI, double firstStart)
    {
        var mergedWords = new List<VoiceCaptureWord>();
        for (var k = startI; k <= endI; k++)
        {
            var off = perWindow[k].StartSec - firstStart;
            foreach (var wd in perWindow[k].Words)
                mergedWords.Add(new VoiceCaptureWord
                {
                    Text = wd.Text,
                    StartSec = wd.StartSec + off,
                    EndSec = wd.EndSec + off,
                });
        }
        return mergedWords;
    }

    private async Task<List<EngineApiClient.NarratorSceneLinesDto>> LoadTargetScenesAsync(
        string projectId, string wantKey, CancellationToken ct)
    {
        var scenes = await _engine.GetNarratorLinesAsync(projectId, wantKey, ct);
        if (scenes is { Count: > 0 })
            return scenes;

        // Blueprint speaker spelling can differ from the cast key; walk every line and match loosely.
        var all = await _engine.GetDialogueLinesAsync(projectId, ct);
        var mapped = new List<EngineApiClient.NarratorSceneLinesDto>();
        foreach (var sc in all)
        {
            var mine = sc.Lines
                .Where(l => CastKindClassifier.SameCharacter(l.Speaker, wantKey) && !string.IsNullOrWhiteSpace(l.Text))
                .Select(l => l.Text.Trim())
                .ToList();
            if (mine.Count == 0) continue;
            var others = sc.Lines.Any(l =>
                !CastKindClassifier.SameCharacter(l.Speaker, wantKey) && !string.IsNullOrWhiteSpace(l.Text));
            mapped.Add(new EngineApiClient.NarratorSceneLinesDto
            {
                Scene = sc.Scene,
                HasOtherSpeakers = others,
                Lines = mine,
            });
        }
        return mapped;
    }

    private async Task<string?> TryParentProjectIdAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var dto = await _engine.GetProjectsAsync(ct);
            var hit = dto?.Projects?.FirstOrDefault(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (hit is null && string.Equals(dto?.Active?.Id, projectId, StringComparison.OrdinalIgnoreCase))
                hit = dto?.Active;
            return string.IsNullOrWhiteSpace(hit?.ParentProjectId) ? null : hit.ParentProjectId.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Easy Start forks skip video files. When no clip can be STT-matched, still give the user
    /// this character's screenplay lines so they can record a clone sample.
    /// </summary>
    internal static void AddScriptFallbackPhrases(
        VoiceCapturePhrases phrases, List<EngineApiClient.NarratorSceneLinesDto> scenes)
    {
        foreach (var sc in ScenesWithTargetLines(scenes))
        {
            foreach (var raw in ExpectedNonEmptyLines(sc))
            {
                if (phrases.Phrases.Exists(p =>
                        p.Scene == sc.Scene &&
                        string.Equals(p.Text, raw, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var dur = EstimatePhraseDurationSec(raw);
                phrases.Phrases.Add(new VoiceCapturePhrase
                {
                    Scene = sc.Scene,
                    Clip = 0,
                    WindowStartSec = 0,
                    WindowEndSec = dur,
                    Text = raw,
                    MatchScore = 1,
                    Confident = true,
                });
            }
        }
    }

    internal static double EstimatePhraseDurationSec(string text)
    {
        var n = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Clamp(n * 0.4, 2.0, 8.0);
    }

    /// <summary>Every scene that has at least one target line — mixed scenes included.</summary>
    internal static List<EngineApiClient.NarratorSceneLinesDto> ScenesWithTargetLines(
        List<EngineApiClient.NarratorSceneLinesDto> scenes)
    {
        var list = scenes
            .Where(s => s.Lines.Exists(l => !string.IsNullOrWhiteSpace(l)))
            .ToList();
        list.Sort(CompareSceneNumber);
        return list;
    }

    private static int CompareSceneNumber(
        EngineApiClient.NarratorSceneLinesDto a,
        EngineApiClient.NarratorSceneLinesDto b) => a.Scene.CompareTo(b.Scene);

    private static List<string> ExpectedNonEmptyLines(EngineApiClient.NarratorSceneLinesDto sc)
    {
        var expectedLines = new List<string>();
        foreach (var t in sc.Lines)
        {
            var line = (t ?? "").Trim();
            if (line.Length > 0)
                expectedLines.Add(line);
        }
        return expectedLines;
    }

    private static List<JsSpeechWindow> FilterMinDurationWindows(JsSpeechDetectResult? detect)
    {
        var windows = new List<JsSpeechWindow>();
        var segments = detect?.Segments;
        if (segments is null)
            return windows;
        foreach (var w in segments)
        {
            if (w.EndSec - w.StartSec >= 0.4)
                windows.Add(w);
        }
        windows.Sort(CompareWindowStart);
        return windows;
    }

    private static int CompareWindowStart(JsSpeechWindow a, JsSpeechWindow b) =>
        a.StartSec.CompareTo(b.StartSec);

    private static List<VoiceCaptureWord> CollectTimedWords(EngineApiClient.TranscriptDto? transcript)
    {
        var timedWords = new List<VoiceCaptureWord>();
        var words = transcript?.Words;
        if (words is null)
            return timedWords;
        foreach (var tw in words)
        {
            if (string.IsNullOrWhiteSpace(tw.Text))
                continue;
            if (string.Equals(tw.Type, "spacing", StringComparison.OrdinalIgnoreCase))
                continue;
            timedWords.Add(new VoiceCaptureWord
            {
                Text = tw.Text.Trim(),
                StartSec = Math.Max(0, tw.Start),
                EndSec = Math.Max(0, tw.End),
            });
        }
        return timedWords;
    }

    private static void RankConfidentPhrases(VoiceCapturePhrases phrases)
    {
        var confident = new List<VoiceCapturePhrase>();
        foreach (var p in phrases.Phrases)
        {
            if (p.Confident)
                confident.Add(p);
        }
        confident.Sort(CompareDurationDesc);
        for (var i = 0; i < confident.Count; i++)
            confident[i].Rank = i;
    }

    private static int CompareDurationDesc(VoiceCapturePhrase a, VoiceCapturePhrase b) =>
        b.DurationSec.CompareTo(a.DurationSec);

    private static int CountConfident(VoiceCapturePhrases phrases)
    {
        var n = 0;
        foreach (var p in phrases.Phrases)
        {
            if (p.Confident)
                n++;
        }
        return n;
    }

    /// <summary>Fraction of the expected line's words that appear in the transcript (0..1).</summary>
    private static double WordOverlap(string expected, string heard)
    {
        var e = Tokenize(expected);
        if (e.Count == 0) return 0;
        var h = new HashSet<string>(Tokenize(heard));
        var hit = CountHits(e, h);
        return (double)hit / e.Count;
    }

    private static int CountHits(List<string> expected, HashSet<string> heard) =>
        expected.Count(heard.Contains);

    private static List<string> Tokenize(string s)
    {
        var chars = new char[(s ?? "").Length];
        var i = 0;
        foreach (var c in (s ?? "").ToLowerInvariant())
            chars[i++] = char.IsLetterOrDigit(c) ? c : ' ';
        return new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>One detected window's STT result: its time span, best-matched line, transcript, and word timings.</summary>
    private sealed record WindowMatch(double StartSec, double EndSec, string Line, string Heard, List<VoiceCaptureWord> Words);
}
