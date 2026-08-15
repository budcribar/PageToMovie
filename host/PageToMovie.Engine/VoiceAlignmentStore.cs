using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Reads/writes the per-project voice-substitution alignment file and holds the pure
/// blueprint→dialogue-line association and segment-matching logic used by the movie-wide
/// voice-substitution job.
///
/// Persistence lives at <c>&lt;project&gt;/assets/alignment/voice_alignment.json</c>: a project
/// data file (like the QA / revoice sidecars) so it travels with the project on export/import.
///
/// The association step is deliberate, not guesswork — it reads each clip's already-known
/// speaker + dialogue straight from the Stage 2 blueprint (<c>audio_payload.speaker</c> /
/// <c>audio_payload.dialogue</c>, with root-level fallbacks). The matching step maps
/// client-detected non-silent windows onto those known lines by order/count, so the free
/// silence-detection path never has to "recognize" who is talking.
/// </summary>
public sealed class VoiceAlignmentStore
{
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;

    private readonly ProjectStore _projects;
    private readonly ILogger<VoiceAlignmentStore> _log;

    public VoiceAlignmentStore(
        ProjectStore projects,
        ILogger<VoiceAlignmentStore>? log = null)
    {
        _projects = projects;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceAlignmentStore>.Instance;
    }

    /// <summary>Project-relative path of the alignment file (single source of the naming).</summary>
    public const string RelativePath = "assets/alignment/voice_alignment.json";

    /// <summary>Same folder as alignment — Dialogue Timing review cache.</summary>
    public const string DialogueTimingRelativePath = "assets/alignment/dialogue_timing.json";

    public string AlignmentPath(string projectId) =>
        Path.Combine(
            _projects.GetProjectDir(projectId),
            "assets", "alignment", "voice_alignment.json");

    public string DialogueTimingPath(string projectId) =>
        Path.Combine(
            _projects.GetProjectDir(projectId),
            "assets", "alignment", "dialogue_timing.json");

    public Task<ProjectVoiceAlignment?> LoadAsync(string projectId, CancellationToken ct = default) =>
        StreamJsonStore.LoadAsync<ProjectVoiceAlignment>(AlignmentPath(projectId), JsonOpts, ct);

    public Task<DialogueTimingDoc?> LoadDialogueTimingAsync(string projectId, CancellationToken ct = default) =>
        StreamJsonStore.LoadAsync<DialogueTimingDoc>(DialogueTimingPath(projectId), JsonOpts, ct);

    /// <summary>
    /// Easy Start catalog gate: a public title is pickable only after admin Dialogue Timing
    /// (or measured/manual alignment) is complete — not estimate-only.
    /// </summary>
    public async Task<bool> IsEasyStartTimingCompleteAsync(string projectId, CancellationToken ct = default)
    {
        var alignment = await LoadAsync(projectId, ct).ConfigureAwait(false);
        var timing = await LoadDialogueTimingAsync(projectId, ct).ConfigureAwait(false);
        return VoiceSubstitutionOverlayGate.CanOverlay(alignment, timing);
    }

    public async Task SaveAsync(string projectId, ProjectVoiceAlignment alignment, CancellationToken ct = default)
    {
        alignment.ProjectId = projectId;
        alignment.GeneratedAtUtc = DateTime.UtcNow;
        await StreamJsonStore.SaveAsync(AlignmentPath(projectId), alignment, JsonOpts, ct: ct).ConfigureAwait(false);
        _log.LogInformation("Saved voice alignment for {Project} ({Clips} clip(s))", projectId, alignment.Clips.Count);
    }

    // ── Association: blueprint → dialogue lines ──────────────────────────────────────────────

    /// <summary>One spoken line associated with the character that says it (from the blueprint).</summary>
    public readonly record struct DialogueLine(string CharacterKey, string Text);

    /// <summary>All dialogue lines for one clip, in on-screen order.</summary>
    public sealed class ClipDialogueLines
    {
        public int Scene { get; init; }
        public int Clip { get; init; }
        public double PlannedDurationSeconds { get; init; }
        public List<DialogueLine> Lines { get; init; } = new();
    }

    /// <summary>
    /// Extract the ordered dialogue lines from a single blueprint clip element. Supports both the
    /// common single-line shape (<c>audio_payload.speaker</c> + <c>audio_payload.dialogue</c>) and a
    /// multi-line <c>audio_payload.lines[]</c>/<c>dialogue_lines[]</c> array when a blueprint carries
    /// one, so multi-speaker clips are handled generically. Text is sanitized the same way the
    /// speak-batch path sanitizes it, so what we align matches what gets synthesized.
    /// </summary>
    public static List<DialogueLine> BuildClipDialogueLines(JsonElement clipEl)
    {
        var lines = new List<DialogueLine>();

        JsonElement audio = default;
        var hasAudio = clipEl.TryGetProperty("audio_payload", out audio) && audio.ValueKind == JsonValueKind.Object;

        // 1. Explicit multi-line array (audio_payload.lines[] or clip.dialogue_lines[]).
        if (hasAudio && audio.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            AppendLineArray(linesEl, lines);
            if (lines.Count > 0) return lines;
        }
        if (clipEl.TryGetProperty("dialogue_lines", out var dl) && dl.ValueKind == JsonValueKind.Array)
        {
            AppendLineArray(dl, lines);
            if (lines.Count > 0) return lines;
        }

        // 2. Single line: audio_payload.dialogue (+ speaker), with root-level fallbacks.
        var speaker = ReadString(hasAudio ? audio : clipEl, "speaker")
                      ?? ReadString(clipEl, "speaker")
                      ?? "";
        var dialogue = ReadString(hasAudio ? audio : clipEl, "dialogue")
                       ?? ReadString(clipEl, "dialogue")
                       ?? "";

        dialogue = ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue);
        if (!string.IsNullOrWhiteSpace(dialogue))
            lines.Add(new DialogueLine(speaker.Trim(), dialogue.Trim()));

        return lines;
    }

    private static void AppendLineArray(JsonElement arr, List<DialogueLine> into)
    {
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var text = ClipVideoPromptBuilder.SanitizeSpokenDialogue(ReadString(el, "dialogue", "text", "line") ?? "");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var spk = (ReadString(el, "speaker", "character", "char_key") ?? "").Trim();
            into.Add(new DialogueLine(spk, text.Trim()));
        }
    }

    /// <summary>
    /// Walk the whole blueprint and return the per-clip dialogue lines, optionally filtered to a
    /// single speaker (narrator-only). <paramref name="matchesCharacter"/> decides whether a line's
    /// speaker counts as the target character; when null, every line is kept.
    /// </summary>
    public static List<ClipDialogueLines> BuildDialogueLinesFromBlueprint(
        JsonElement blueprintRoot,
        Func<string, bool>? matchesCharacter = null)
    {
        var result = new List<ClipDialogueLines>();
        if (!blueprintRoot.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var s in scenes.EnumerateArray())
            AppendSceneClipDialogue(result, s, matchesCharacter);

        return result;
    }

    private static void AppendSceneClipDialogue(
        List<ClipDialogueLines> result, JsonElement s, Func<string, bool>? matchesCharacter)
    {
        var sn = ReadInt(s, "scene_number");
        if (sn <= 0) return;
        if (!s.TryGetProperty("veo_clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
            return;

        foreach (var c in clips.EnumerateArray())
            TryAddClipDialogue(result, sn, c, matchesCharacter);
    }

    private static void TryAddClipDialogue(
        List<ClipDialogueLines> result, int sn, JsonElement c, Func<string, bool>? matchesCharacter)
    {
        var cn = ReadInt(c, "clip_number");
        if (cn <= 0) return;

        var lines = BuildClipDialogueLines(c);
        if (matchesCharacter is not null)
            lines = lines.Where(l => matchesCharacter(l.CharacterKey)).ToList();
        if (lines.Count == 0) return;

        result.Add(new ClipDialogueLines
        {
            Scene = sn,
            Clip = cn,
            PlannedDurationSeconds = ReadDouble(c, "duration_seconds"),
            Lines = lines,
        });
    }

    /// <summary>
    /// Map detected non-silent speech windows onto the clip's known dialogue lines by order/count.
    ///
    /// * <c>speech.Count == lines.Count</c> → 1:1 in order (source = <paramref name="detectedSource"/>).
    /// * counts differ but at least one window was detected → take the overall speech span
    ///   [firstStart, lastEnd] and split it across the lines proportionally to each line's character
    ///   length (a rough but general time-fit; still marked with the detected source since the span
    ///   itself was measured).
    /// * nothing detected → spread the lines across the whole clip proportionally
    ///   (source = <see cref="SpeechTimestampSource.Estimate"/>), so a clip with no measurable silence
    ///   still yields usable placement.
    ///
    /// Windows shorter than <paramref name="minSegmentSec"/> are dropped before matching so a stray
    /// click of noise does not create a phantom line.
    /// </summary>
    public static List<SpeechSegment> MatchSegmentsToLines(
        IReadOnlyList<(double Start, double End)> speech,
        IReadOnlyList<DialogueLine> lines,
        double clipDurationSeconds,
        string detectedSource = SpeechTimestampSource.Silence,
        double minSegmentSec = 0.15)
    {
        var segments = new List<SpeechSegment>(lines.Count);
        if (lines.Count == 0) return segments;

        var windows = (speech ?? Array.Empty<(double, double)>())
            .Where(w => w.End - w.Start >= minSegmentSec)
            .OrderBy(w => w.Start)
            .ToList();

        // Fallback span: whole clip when duration known, else the detected envelope.
        double clipEnd;
        if (clipDurationSeconds > 0.01)
            clipEnd = clipDurationSeconds;
        else
            clipEnd = windows.Count > 0 ? windows[^1].End : lines.Count;

        if (windows.Count == lines.Count)
        {
            for (var i = 0; i < lines.Count; i++)
                segments.Add(NewSegment(i, lines[i], windows[i].Start, windows[i].End, detectedSource));
            return segments;
        }

        double spanStart, spanEnd;
        string source;
        if (windows.Count > 0)
        {
            spanStart = windows[0].Start;
            spanEnd = Math.Max(windows[^1].End, spanStart + 0.01);
            source = detectedSource;
        }
        else
        {
            spanStart = 0;
            spanEnd = Math.Max(clipEnd, 0.01);
            source = SpeechTimestampSource.Estimate;
        }

        // Proportional split by character length (min weight 1 so empty-ish lines still get a slice).
        var weights = lines.Select(l => (double)Math.Max(1, (l.Text ?? "").Trim().Length)).ToList();
        var totalWeight = weights.Sum();
        var totalSpan = Math.Max(0.01, spanEnd - spanStart);

        var cursor = spanStart;
        for (var i = 0; i < lines.Count; i++)
        {
            var slice = totalSpan * (weights[i] / totalWeight);
            var start = cursor;
            var end = i == lines.Count - 1 ? spanEnd : cursor + slice;
            segments.Add(NewSegment(i, lines[i], start, end, source));
            cursor = end;
        }

        return segments;
    }

    private static SpeechSegment NewSegment(int index, DialogueLine line, double start, double end, string source) =>
        new()
        {
            Index = index,
            CharacterKey = line.CharacterKey,
            DialogueText = line.Text,
            StartSec = Math.Round(Math.Max(0, start), 3),
            EndSec = Math.Round(Math.Max(start, end), 3),
            Source = source,
        };

    // ── Merge: apply client-detected timestamps onto a persisted clip alignment ──────────────

    /// <summary>
    /// Match a browser's freshly detected non-silent windows onto an existing clip alignment's known
    /// dialogue lines (reusing <see cref="MatchSegmentsToLines"/> — the single source of the matching
    /// logic) and write the resulting timestamps/source back onto the clip's segments, preserving each
    /// segment's character/text/audio path. Used by the persist-timestamps endpoint so a re-run can
    /// skip detection.
    /// </summary>
    public static void ApplyTimestamps(ClipSpeechAlignment clip, ClipTimestampUpdate update)
    {
        var dur = update.ClipDurationSeconds > 0.01 ? update.ClipDurationSeconds : clip.ClipDurationSeconds;
        if (dur > 0.01) clip.ClipDurationSeconds = dur;

        if (clip.Segments.Count == 0) return;

        var lines = clip.Segments
            .Select(s => new DialogueLine(s.CharacterKey, s.DialogueText))
            .ToList();
        var windows = (update.Windows ?? new List<SpeechWindow>())
            .Select(w => (w.StartSec, w.EndSec))
            .ToList();

        var matched = MatchSegmentsToLines(windows, lines, dur);
        for (var i = 0; i < clip.Segments.Count && i < matched.Count; i++)
        {
            clip.Segments[i].StartSec = matched[i].StartSec;
            clip.Segments[i].EndSec = matched[i].EndSec;
            clip.Segments[i].Source = matched[i].Source;
        }
    }

    /// <summary>
    /// Copy accepted Dialogue Timing windows onto the persisted alignment so overlay
    /// reads reviewed splice points (source = <see cref="SpeechTimestampSource.Manual"/>
    /// when the reviewer edited, otherwise keep a measured source).
    /// </summary>
    public static void ApplyReviewedTiming(ProjectVoiceAlignment alignment, DialogueTimingScene scene)
    {
        if (alignment is null || scene is null) return;
        foreach (var row in scene.Rows)
        {
            if (!row.Reviewed || row.WindowEndSec <= row.WindowStartSec) continue;
            var clipNo = row.Clip > 0 ? row.Clip : 1;
            var clip = alignment.Find(scene.Scene, clipNo);
            if (clip is null)
            {
                clip = new ClipSpeechAlignment
                {
                    Scene = scene.Scene,
                    Clip = clipNo,
                    ClipDurationSeconds = scene.SceneDurationSec,
                };
                alignment.Clips.Add(clip);
            }

            var seg = FindSegmentForRow(clip, row);
            var source = SegmentSourceAfterReview(seg, row);
            if (seg is null)
            {
                clip.Segments.Add(new SpeechSegment
                {
                    Index = clip.Segments.Count,
                    CharacterKey = row.Speaker ?? "",
                    DialogueText = row.ScriptText ?? "",
                    StartSec = row.WindowStartSec,
                    EndSec = row.WindowEndSec,
                    Source = source,
                });
            }
            else
            {
                seg.StartSec = row.WindowStartSec;
                seg.EndSec = row.WindowEndSec;
                seg.Source = source;
            }
        }
    }

    private static SpeechSegment? FindSegmentForRow(ClipSpeechAlignment clip, DialogueTimingRow row)
    {
        var text = (row.ScriptText ?? "").Trim();
        if (text.Length > 0)
        {
            var byText = clip.Segments.Find(s =>
                string.Equals((s.DialogueText ?? "").Trim(), text, StringComparison.OrdinalIgnoreCase));
            if (byText is not null) return byText;
        }
        return clip.Segments.Count == 1 ? clip.Segments[0] : null;
    }

    private static string SegmentSourceAfterReview(SpeechSegment? existing, DialogueTimingRow row)
    {
        if (existing is not null
            && SpeechTimestampSource.IsDetected(existing.Source)
            && !string.Equals(existing.Source, SpeechTimestampSource.Estimate, StringComparison.OrdinalIgnoreCase)
            && NearlyEqual(existing.StartSec, row.WindowStartSec)
            && NearlyEqual(existing.EndSec, row.WindowEndSec))
            return existing.Source;
        return SpeechTimestampSource.Manual;
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.02;

    // ── small JSON readers ───────────────────────────────────────────────────────────────────

    private static string? ReadString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }

    private static int ReadInt(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : 0;

    private static double ReadDouble(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : 0;
}
