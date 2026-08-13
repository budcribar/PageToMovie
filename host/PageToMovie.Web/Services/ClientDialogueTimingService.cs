using Microsoft.JSInterop;
using PageToMovie.Core.Models;

using PageToMovie.Core.Utils;
namespace PageToMovie.Web.Services;

/// <summary>
/// Runs the dialogue-timing review pass for a scene: stitch the scene, detect speech windows, run
/// each through Scribe (STT), and align the heard windows onto the blueprint's script lines so a
/// reviewer can see — per line — the script text, what was actually heard, the word timings, and the
/// window delineation, and where they disagree. Result is saved to the project's dialogue-timing
/// cache so the paid STT pass runs once per scene.
/// </summary>
public sealed class ClientDialogueTimingService
{
    private readonly IJSRuntime _js;
    private readonly EngineApiClient _engine;
    private readonly ClientVideoStitchService _stitch;

    public ClientDialogueTimingService(IJSRuntime js, EngineApiClient engine, ClientVideoStitchService stitch)
    {
        _js = js;
        _engine = engine;
        _stitch = stitch;
    }

    /// <summary>
    /// Analyze one scene against its script lines. Returns the reviewed scene (also worth saving via
    /// <see cref="EngineApiClient.SaveDialogueTimingSceneAsync"/>). Null if the scene can't be stitched.
    /// </summary>
    public async Task<DialogueTimingScene?> AnalyzeSceneAsync(
        string projectId, int scene, IReadOnlyList<EngineApiClient.DialogueLineDto> lines,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        onProgress?.Invoke($"Loading scene {scene:D2}…");
        var clipUrls = await _stitch.CollectClipUrlsAsync(projectId, scene, ct: ct);
        if (clipUrls.Count == 0) return null;

        string sceneVideoUrl;
        if (clipUrls.Count == 1) sceneVideoUrl = clipUrls[0];
        else
        {
            var stitched = await _stitch.ConcatAsync(clipUrls, ct);
            var stitchedUrl = stitched.Url;
            if (!stitched.Success || string.IsNullOrWhiteSpace(stitchedUrl)) return null;
            sceneVideoUrl = stitchedUrl;
        }

        onProgress?.Invoke($"Detecting speech in scene {scene:D2}…");
        var detect = await _js.InvokeAsync<JsSpeechDetectResult>(
            "PageToMovieFfmpeg.detectSpeechSegmentsAsync", ct, sceneVideoUrl, new { });
        var windows = (detect?.Segments ?? new List<JsSpeechWindow>())
            .Where(w => w.EndSec - w.StartSec >= 0.25)
            .OrderBy(w => w.StartSec)
            .ToList();

        // STT each window.
        var heardWindows = new List<HeardWindow>();
        for (var wi = 0; wi < windows.Count; wi++)
        {
            ct.ThrowIfCancellationRequested();
            var w = windows[wi];
            onProgress?.Invoke($"Scene {scene:D2}: transcribing window {wi + 1}/{windows.Count}…");

            byte[]? audio;
            try
            {
                audio = await _js.InvokeAsync<byte[]>(
                    "PageToMovieFfmpeg.extractAudioSegmentAsync", ct, sceneVideoUrl, w.StartSec, w.EndSec);
            }
            catch { continue; }
            if (audio is null || audio.Length < 256) continue;

            var transcript = await _engine.TranscribeSegmentAsync(audio, "segment.wav", ct);
            var heard = StripSoundEffectTags((transcript?.Text ?? "").Trim());
            var words = (transcript?.Words ?? new())
                .Where(tw => !string.IsNullOrWhiteSpace(tw.Text) &&
                             !string.Equals(tw.Type, "spacing", StringComparison.OrdinalIgnoreCase) &&
                             !SoundEffectTagRegex.IsMatch(tw.Text))
                .Select(tw => new VoiceCaptureWord { Text = tw.Text.Trim(), StartSec = Math.Max(0, tw.Start), EndSec = Math.Max(0, tw.End) })
                .ToList();
            // A window that was ONLY a sound-effect tag ("[outro jingle]") has nothing left to review —
            // drop it rather than surfacing an empty "heard but not in script" row.
            if (heard.Length == 0 && words.Count == 0) continue;
            heardWindows.Add(new HeardWindow(w.StartSec, w.EndSec, heard, words));
        }

        // Align: each script line takes its BEST-overlap unused window anywhere in the scene (order and
        // duplicates don't break it). Ties/no-overlap leave the line unmatched. Leftover windows are
        // surfaced as "heard but not in script" so extra/misheard speech is visible too.
        var rows = new List<DialogueTimingRow>();
        var used = new bool[heardWindows.Count];
        foreach (var line in lines)
        {
            var bestIdx = -1;
            var bestScore = 0.0;
            for (var j = 0; j < heardWindows.Count; j++)
            {
                if (used[j]) continue;
                var s = WordOverlap(line.Text, heardWindows[j].Heard);
                if (s > bestScore) { bestScore = s; bestIdx = j; }
            }

            if (bestIdx >= 0 && bestScore > 0)
            {
                used[bestIdx] = true;
                var hw = heardWindows[bestIdx];
                rows.Add(new DialogueTimingRow
                {
                    Clip = line.Clip,
                    Speaker = line.Speaker,
                    ScriptText = line.Text,
                    HeardText = hw.Heard,
                    WindowStartSec = hw.StartSec,
                    WindowEndSec = hw.EndSec,
                    MatchScore = Math.Round(bestScore, 3),
                    Words = hw.Words,
                });
            }
            else
            {
                // No window matched this script line — surface the gap.
                rows.Add(new DialogueTimingRow
                {
                    Clip = line.Clip,
                    Speaker = line.Speaker,
                    ScriptText = line.Text,
                    HeardText = "",
                    MatchScore = 0,
                });
            }
        }

        // Leftover heard windows never matched to a script line — extra/misheard speech.
        for (var j = 0; j < heardWindows.Count; j++)
        {
            if (used[j] || string.IsNullOrWhiteSpace(heardWindows[j].Heard)) continue;
            var hw = heardWindows[j];
            rows.Add(new DialogueTimingRow
            {
                Clip = 0,
                Speaker = "",
                ScriptText = "",
                HeardText = hw.Heard,
                WindowStartSec = hw.StartSec,
                WindowEndSec = hw.EndSec,
                MatchScore = 0,
                Words = hw.Words,
            });
        }

        return new DialogueTimingScene
        {
            Scene = scene,
            SceneDurationSec = detect?.TotalSec ?? 0,
            Rows = rows,
        };
    }

    /// <summary>Fraction of the script line's words that appear in the heard transcript (0..1).</summary>
    private static double WordOverlap(string script, string heard)
    {
        var e = Tokenize(script);
        if (e.Count == 0) return 0;
        var h = new HashSet<string>(Tokenize(heard));
        return (double)e.Count(w => h.Contains(w)) / e.Count;
    }

    private static List<string> Tokenize(string s) =>
        new string((s ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>Matches STT non-speech annotations like "[laughs]", "[door closing]", "[outro jingle]"
    /// — Scribe (and Whisper-family models generally) emit bracketed tags for detected non-verbal
    /// sound, not spoken dialogue, so they don't belong in a script-vs-heard comparison.</summary>
    private static readonly System.Text.RegularExpressions.Regex SoundEffectTagRegex =
        new(@"\[[^\]]*\]", System.Text.RegularExpressions.RegexOptions.Compiled, CommonRegex.Timeout);

    private static string StripSoundEffectTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var stripped = SoundEffectTagRegex.Replace(text, " ");
        return CommonRegex.Replace(stripped, @"\s+", " ").Trim();
    }

    private sealed record HeardWindow(double StartSec, double EndSec, string Heard, List<VoiceCaptureWord> Words);
}
