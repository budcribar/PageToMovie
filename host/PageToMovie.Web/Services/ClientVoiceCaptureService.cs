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
    /// Solo scenes only for the target character (default: narrator) — mixed scenes keep original
    /// audio and aren't capture material.
    /// </summary>
    /// <param name="charKey">Which character to build phrases for. Null/omitted defaults to the
    /// narrator, matching the original behavior.</param>
    public async Task<VoiceCapturePhrases?> BuildPhrasesAsync(
        string projectId, Action<string>? onProgress = null, string? charKey = null, CancellationToken ct = default)
    {
        // Expected line texts for the target character + which scenes are theirs alone, straight
        // from the blueprint — no dub/TTS needed, so this runs standalone from the capture page.
        var scenes = await _engine.GetNarratorLinesAsync(projectId, charKey, ct);
        if (scenes is null || scenes.Count == 0)
            return null;

        var phrases = new VoiceCapturePhrases
        {
            ProjectId = projectId,
            ConfidenceThreshold = ConfidenceThreshold,
            CharKey = string.IsNullOrWhiteSpace(charKey) ? "Character_Narrator" : charKey.Trim(),
        };

        foreach (var sc in scenes.Where(s => !s.HasOtherSpeakers).OrderBy(s => s.Scene))
        {
            ct.ThrowIfCancellationRequested();
            var expectedLines = sc.Lines
                .Select(t => (t ?? "").Trim())
                .Where(t => t.Length > 0)
                .ToList();
            if (expectedLines.Count == 0) continue;

            onProgress?.Invoke($"Scanning scene {sc.Scene:D2}…");

            // Stitch the scene's clips into one video.
            var clipUrls = await _stitch.CollectClipUrlsAsync(projectId, sc.Scene, ct: ct);
            if (clipUrls.Count == 0) continue;
            string sceneVideoUrl;
            if (clipUrls.Count == 1)
            {
                sceneVideoUrl = clipUrls[0];
            }
            else
            {
                var stitched = await _stitch.ConcatAsync(clipUrls, ct);
                if (!stitched.Success || string.IsNullOrWhiteSpace(stitched.Url)) continue;
                sceneVideoUrl = stitched.Url;
            }

            // Detect speech windows.
            var detect = await _js.InvokeAsync<JsSpeechDetectResult>(
                "PageToMovieFfmpeg.detectSpeechSegmentsAsync", ct, sceneVideoUrl, new { });
            var windows = (detect?.Segments ?? new List<JsSpeechWindow>())
                .Where(w => w.EndSec - w.StartSec >= 0.4)
                .OrderBy(w => w.StartSec)
                .ToList();

            // First pass: extract + transcribe + best-line-match each detected window.
            var perWindow = new List<WindowMatch>();
            for (var wi = 0; wi < windows.Count; wi++)
            {
                ct.ThrowIfCancellationRequested();
                var w = windows[wi];
                onProgress?.Invoke($"Scene {sc.Scene:D2}: verifying phrase {wi + 1}/{windows.Count}…");

                byte[]? audio;
                try
                {
                    audio = await _js.InvokeAsync<byte[]>(
                        "PageToMovieFfmpeg.extractAudioSegmentAsync", ct, sceneVideoUrl, w.StartSec, w.EndSec);
                }
                catch { continue; }
                if (audio is null || audio.Length < 256) continue;

                var transcript = await _engine.TranscribeSegmentAsync(audio, "segment.wav", ct);
                var heard = (transcript?.Text ?? "").Trim();
                if (heard.Length == 0) continue;

                // Per-word timings (0-based within this window) so the teleprompter can copy the exact rhythm.
                var timedWords = (transcript?.Words ?? new())
                    .Where(tw => !string.IsNullOrWhiteSpace(tw.Text) &&
                                 !string.Equals(tw.Type, "spacing", StringComparison.OrdinalIgnoreCase))
                    .Select(tw => new VoiceCaptureWord { Text = tw.Text.Trim(), StartSec = Math.Max(0, tw.Start), EndSec = Math.Max(0, tw.End) })
                    .ToList();

                // Best-matching expected narrator line for this window.
                var bestLine = "";
                var bestScore = 0.0;
                foreach (var line in expectedLines)
                {
                    var s = WordOverlap(line, heard);
                    if (s > bestScore) { bestScore = s; bestLine = line; }
                }

                perWindow.Add(new WindowMatch(w.StartSec, w.EndSec, bestLine, heard, timedWords));
            }

            // Second pass: a line the narrator says with an internal pause (e.g. after a comma) gets split
            // into two speech windows, and matching each alone drops the short fragment — losing that word
            // from the captured audio. Rejoin consecutive windows that matched the SAME line (within a
            // short gap) into one phrase spanning the whole line, transcript and word-timings included.
            const double maxMergeGapSec = 1.5;
            var idx = 0;
            while (idx < perWindow.Count)
            {
                if (string.IsNullOrEmpty(perWindow[idx].Line)) { idx++; continue; }

                var startI = idx;
                var endI = idx;
                while (endI + 1 < perWindow.Count &&
                       perWindow[endI + 1].Line == perWindow[startI].Line &&
                       perWindow[endI + 1].StartSec - perWindow[endI].EndSec < maxMergeGapSec)
                    endI++;

                var first = perWindow[startI];
                var last = perWindow[endI];
                var heard = string.Join(" ", Enumerable.Range(startI, endI - startI + 1).Select(k => perWindow[k].Heard));
                var mergedWords = new List<VoiceCaptureWord>();
                for (var k = startI; k <= endI; k++)
                {
                    var off = perWindow[k].StartSec - first.StartSec; // re-base to the combined window start
                    foreach (var wd in perWindow[k].Words)
                        mergedWords.Add(new VoiceCaptureWord { Text = wd.Text, StartSec = wd.StartSec + off, EndSec = wd.EndSec + off });
                }
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

        // Rank confident phrases (longer = more capture material) and mark the selected pool.
        var confident = phrases.Phrases.Where(p => p.Confident).OrderByDescending(p => p.DurationSec).ToList();
        for (var i = 0; i < confident.Count; i++) confident[i].Rank = i;

        onProgress?.Invoke($"Verified {confident.Count} of {phrases.Phrases.Count} detected phrase(s).");
        await _engine.SaveVoiceCapturePhrasesAsync(projectId, phrases, ct);
        return phrases;
    }

    /// <summary>Fraction of the expected line's words that appear in the transcript (0..1).</summary>
    private static double WordOverlap(string expected, string heard)
    {
        var e = Tokenize(expected);
        if (e.Count == 0) return 0;
        var h = new HashSet<string>(Tokenize(heard));
        var hit = e.Count(w => h.Contains(w));
        return (double)hit / e.Count;
    }

    private static List<string> Tokenize(string s) =>
        new string((s ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>One detected window's STT result: its time span, best-matched line, transcript, and word timings.</summary>
    private sealed record WindowMatch(double StartSec, double EndSec, string Line, string Heard, List<VoiceCaptureWord> Words);
}
