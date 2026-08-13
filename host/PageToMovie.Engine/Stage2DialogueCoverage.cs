using System.Text;
using PageToMovie.Engine.ModelExecution;

namespace PageToMovie.Engine;

/// <summary>
/// Stage-2 dialogue-coverage gate — the middle layer of the three-stage "no spoken line silently
/// vanishes" guard:
///   1. Stage 1 (book→Fountain) verify re-joins narration split by a real blank line.
///   2. <b>This</b> — every spoken line in the approved screenplay must survive Stage-2 shot
///      planning into some clip's <c>audio_payload</c>.
///   3. Per-clip verify checks the rendered clip actually speaks its planned lines.
///
/// Why the middle layer is not redundant: the Stage-1 verify only validates the Fountain (upstream
/// of planning), and the per-clip verify validates a rendered clip against <i>its own</i> payload —
/// so a line dropped or silenced <i>during</i> planning (a bad silent-beat classification, a
/// coalesce/fold that eats a line, a beat dropped from the plan) is invisible to both. Only a
/// screenplay→plan comparison catches it.
///
/// This is a <b>detect + surface</b> gate, deliberately NOT an auto-restorer. Stage 2 is our own
/// deterministic code, so the right response to a drop is to fix the offending transform — guided by
/// the <c>generation_errors</c> row this gate logs (which names the scene + beat) — not to paper over
/// it here, which would hide the planner bug the North Star wants fixed.
/// </summary>
internal static class Stage2DialogueCoverage
{
    /// <summary>
    /// v1 severity is <see cref="ModelValidationSeverity.Warning"/>: it surfaces every genuinely
    /// missing spoken line in the Stage-2 manifest and logs it to <c>generation_errors</c> (the
    /// learning signal) WITHOUT hard-failing planning on a normalization false positive. Promote to
    /// <see cref="ModelValidationSeverity.Error"/> — the existing plan-issue pipeline then throws
    /// before the blueprint is written — once telemetry confirms it is not noisy.
    /// </summary>
    public const ModelValidationSeverity GapSeverity = ModelValidationSeverity.Warning;

    /// <summary>Cap on individually-emitted issues / logged ids so a pathological plan can't explode them.</summary>
    private const int MaxReported = 50;

    /// <summary>A screenplay dialogue line that never reached a clip's spoken audio.</summary>
    public readonly record struct Gap(int Scene, string BeatId, string Speaker, string Dialogue, string Diagnosis);

    public sealed class Report
    {
        public int ExpectedLines { get; init; }
        public int CoveredLines { get; init; }
        public IReadOnlyList<Gap> Gaps { get; init; } = Array.Empty<Gap>();
        /// <summary>Validation issues (one per gap, capped) to fold into the Stage-2 plan-issue pipeline.</summary>
        public IReadOnlyList<ModelValidationIssue> Issues { get; init; } = Array.Empty<ModelValidationIssue>();
        /// <summary>Block for <c>stage2_meta["dialogue_coverage"]</c> — always written, even when clean.</summary>
        public Dictionary<string, object?> Meta { get; init; } = new();
        public bool HasGaps => Gaps.Count > 0;
    }

    /// <summary>
    /// Compare every spoken line in the Stage-1 model (<paramref name="stage1"/>, the approved
    /// screenplay's <c>story_beats</c>) against the spoken lines the Stage-2 <paramref name="plan"/>
    /// actually carries in each clip's <c>audio_payload</c>. Pure — reads both, mutates neither.
    /// </summary>
    public static Report Verify(Dictionary<string, object?> stage1, Dictionary<string, object?> plan)
    {
        // Actual side, per scene: a normalized blob of every spoken line the plan carries, plus the set
        // of beat ids that reached a clip (so a residual gap can say whether its beat was silenced in a
        // clip that exists vs dropped from the plan entirely).
        var actualBlob = new Dictionary<int, string>();
        var beatsWithClip = new Dictionary<int, HashSet<string>>();
        foreach (var scene in Stage2PlannerService.GetScenes(plan))
        {
            var sn = Stage2PlannerService.ToInt(scene.TryGetValue("scene_number", out var s) ? s : 0);
            var sb = new StringBuilder(" ");
            var beatIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in Stage2PlannerService.GetList(scene, "veo_clips").OfType<Dictionary<string, object?>>())
            {
                var ap = clip.TryGetValue("audio_payload", out var apv) && apv is Dictionary<string, object?> apd ? apd : null;
                // "What does this clip actually say?" is exactly ClipSpokenLines' job (delivery:"none"
                // and empty lines are already excluded there — i.e. a silenced clip contributes nothing).
                foreach (var line in ClipSpokenLines.FromBeat(ap))
                    sb.Append(Normalize(line.Dialogue)).Append(' ');
                var bid = clip.TryGetValue("stage1_beat_id", out var b) ? b?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(bid)) beatIds.Add(bid);
            }
            actualBlob[sn] = sb.ToString();
            beatsWithClip[sn] = beatIds;
        }

        var expected = 0;
        var covered = 0;
        var gaps = new List<Gap>();
        foreach (var scene in Stage2PlannerService.GetScenes(stage1))
        {
            var sn = Stage2PlannerService.ToInt(scene.TryGetValue("scene_number", out var s) ? s : 0);
            var blob = actualBlob.TryGetValue(sn, out var bl) ? bl : " ";
            var clipBeats = beatsWithClip.TryGetValue(sn, out var cb) ? cb : null;
            foreach (var beat in Stage2PlannerService.GetList(scene, "story_beats").OfType<Dictionary<string, object?>>())
            {
                var bid = (beat.TryGetValue("beat_id", out var bv) ? bv?.ToString() : null) ?? "";
                foreach (var (speaker, dialogue) in ExpectedSpokenLines(beat))
                {
                    // Sanitize the expected line the same way the plan's audio_payload is built
                    // (BuildAudioPayload → SanitizeSpokenDialogue), so number spell-out / abbreviation
                    // expansion is applied to BOTH sides and can't masquerade as a dropped line.
                    var needle = Normalize(ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue));
                    if (needle.Length == 0) continue; // nothing survives sanitize → nothing to speak
                    expected++;
                    if (blob.Contains(" " + needle + " ", StringComparison.Ordinal))
                    {
                        covered++;
                        continue;
                    }
                    var diag = clipBeats is not null && clipBeats.Contains(bid)
                        ? "beat_present_but_unspoken" // a clip exists for this beat but its audio is silent/other
                        : "beat_absent_from_plan";     // no clip carries this beat at all
                    gaps.Add(new Gap(sn, bid, speaker, Snippet(dialogue), diag));
                }
            }
        }

        var issues = gaps.Take(MaxReported).Select(g => new ModelValidationIssue(
            Code: "stage2_dialogue_coverage",
            Message: $"Scene {g.Scene}: screenplay line by {(string.IsNullOrWhiteSpace(g.Speaker) ? "a speaker" : g.Speaker)} " +
                     $"is never spoken in the shot plan ({g.Diagnosis}): \"{g.Dialogue}\"",
            Path: $"scenes[scene_number={g.Scene}].veo_clips",
            Severity: GapSeverity)).ToArray();

        var meta = new Dictionary<string, object?>
        {
            ["expected_lines"] = expected,
            ["covered_lines"] = covered,
            ["missing_lines"] = gaps.Count,
            ["missing"] = gaps.Take(MaxReported).Select(g => new Dictionary<string, object?>
            {
                ["scene"] = g.Scene,
                ["beat_id"] = g.BeatId,
                ["speaker"] = g.Speaker,
                ["dialogue"] = g.Dialogue,
                ["diagnosis"] = g.Diagnosis,
            }).Cast<object?>().ToList(),
        };

        return new Report { ExpectedLines = expected, CoveredLines = covered, Gaps = gaps, Issues = issues, Meta = meta };
    }

    /// <summary>
    /// The dialogue a Stage-1 <c>story_beat</c> is expected to have spoken — primary plus any
    /// secondary (two-hander) line — reading either the flat <c>speaker</c>/<c>dialogue</c> keys or a
    /// nested <c>audio</c> object, and (unlike the actual side) NOT filtered by delivery: a screenplay
    /// beat that carries dialogue is spoken by definition; a <c>delivery:"none"</c> on it is precisely
    /// the silencing this gate is meant to catch.
    /// </summary>
    private static IEnumerable<(string Speaker, string Dialogue)> ExpectedSpokenLines(Dictionary<string, object?> beat)
    {
        var nested = beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;
        string Pick(string key) => ((nested?.TryGetValue(key, out var nv) == true ? nv?.ToString() : null)
            ?? (beat.TryGetValue(key, out var bv) ? bv?.ToString() : null) ?? "").Trim();

        var primary = Pick("dialogue");
        if (primary.Length > 0)
            yield return (Pick("speaker"), primary);

        var secondary = ((beat.TryGetValue("secondary_dialogue", out var sd) ? sd?.ToString() : null) ?? "").Trim();
        if (secondary.Length > 0)
            yield return (((beat.TryGetValue("secondary_speaker", out var ss) ? ss?.ToString() : null) ?? "").Trim(), secondary);
    }

    /// <summary>
    /// Aggressive, sanitization-proof normalization for substring coverage: lowercase, every run of
    /// non-alphanumeric characters collapses to a single space. Both sides pass through this, and
    /// matches are checked with surrounding spaces so a short line can't match mid-word.
    /// </summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static string Snippet(string dialogue)
    {
        var d = dialogue.Trim();
        return d.Length <= 80 ? d : d[..77] + "…";
    }
}
