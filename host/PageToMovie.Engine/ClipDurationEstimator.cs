using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation;
using PageToMovie.Core.Models;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Cost-aware clip length estimates from dialogue + action (no API calls).
/// Prefer tight clips: pay for speech + short action, not long empty holds.
/// </summary>
public static class ClipDurationEstimator
{
    /// <summary>API / product floor (seconds). Short dialogue can be this tight.</summary>
    public const int MinSeconds = 3;

    /// <summary>Soft max for a single clip (cost + model comfort).</summary>
    public const int MaxSeconds = 10;

    /// <summary>Absolute max if action is huge.</summary>
    public const int AbsMaxSeconds = 12;

    /// <summary>Words per second for spoken dialogue (~156 wpm — natural narration pace).</summary>
    public const double DialogueWordsPerSecond = TextMetrics.DialogueWordsPerSecond;

    /// <summary>
    /// Lead-in before speech so lip-sync / video-extend does not clip the first word
    /// (common on the first spoken clip after a silent establish).
    /// </summary>
    public const double SpeechHeadSeconds = 0.40;

    /// <summary>
    /// Pad after speech so the line can land and leave a short breath before the next clip
    /// (matches silence-trim speech breath tail; not multi-second dead air).
    /// </summary>
    public const double SpeechTailSeconds = 0.50;

    /// <summary>
    /// Gap between two speakers' lines in a single two-hander clip — the beat where the camera
    /// pans / the second speaker turns in before answering. Added once per additional spoken line
    /// so a cross-speaker clip is budgeted for the hand-off, not just the raw words.
    /// </summary>
    public const double InterSpeakerGapSeconds = 0.40;

    /// <summary>
    /// Extra headroom under <see cref="MaxSeconds"/> when packing monologue splits so lip-sync
    /// / model end-trim does not cut the last words (speech + visual head still fit).
    /// </summary>
    public const double DialogueModelPaddingSeconds = 1.5;

    /// <summary>Minimum visual beat with no dialogue (also <c>hold</c> duration).</summary>
    public const int ActionOnlyMinSeconds = 3;

    /// <summary>
    /// Max for ordinary silent action (no dialogue). Prevents long empty holds when visual
    /// prose is wordy or scene budget padding used to stretch silent clips.
    /// </summary>
    public const int SilentActionMaxSeconds = 5;

    /// <summary>Max for establishing / open-room silent beats.</summary>
    public const int EstablishingMaxSeconds = 5;

    /// <summary>
    /// Cap word count used when estimating silent-action length so wardrobe/location
    /// boilerplate does not inflate duration.
    /// </summary>
    public const int SilentVisualWordCap = 20;

    /// <summary>
    /// Resolves effective clip-duration bounds for a specific video model from
    /// <see cref="SupportedModelCatalog"/>, falling back to this class's own defaults for any field
    /// the model hasn't overridden (or when the model isn't found in the catalog at all). Callers that
    /// don't know the target model yet get identical behavior to today by simply not calling this.
    /// </summary>
    public static (int MinSeconds, int MaxSeconds, int AbsMaxSeconds) ResolveBoundsForModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException(
                "Video model is required for clip duration bounds. " +
                "Open Settings → Studio coverage and choose a video model.");

        var entry = SupportedModelCatalog.Find(modelId.Trim(), ModelCapability.Video)
            ?? throw new InvalidOperationException(
                $"Video model '{modelId}' is not in models_catalog.json. " +
                "Unknown models have no duration capabilities.");

        if (!entry.Enabled)
            throw new InvalidOperationException(
                $"Video model '{entry.Id}' is disabled in the catalog.");

        if (entry.MinClipDurationSeconds is not { } min
            || entry.MaxClipDurationSeconds is not { } max)
        {
            throw new InvalidOperationException(
                $"Video model '{entry.Id}' is missing minClipDurationSeconds/maxClipDurationSeconds in models_catalog.json. " +
                "Add duration bounds to the catalog — do not invent defaults.");
        }

        var abs = entry.AbsMaxClipDurationSeconds ?? max;
        return (min, max, abs);
    }

    /// <summary>
    /// Resolves the actual generation-time duration for a specific video model, snapping to the
    /// nearest value in <see cref="SupportedModelEntry.AllowedDurationsSeconds"/> when the model
    /// only accepts a discrete set (e.g. Veo 3.1's documented 4/6/8 seconds only — clamping a
    /// planned "7" into a min/max range still isn't an accepted duration). Falls back to a plain
    /// min/max clamp via <see cref="ResolveBoundsForModel"/> for any model without a declared
    /// discrete set, so this is a safe drop-in replacement for a bare Math.Clamp call everywhere.
    /// </summary>
    /// <param name="isExtensionMode">
    /// True for image-to-video / reference-conditioned / video-extend calls, which some providers
    /// (Grok) cap tighter than a fresh text-to-video clip — applies
    /// <see cref="SupportedModelEntry.MaxExtensionSeconds"/> as an additional ceiling on top of the
    /// normal result when the model declares one.
    /// </param>
    public static int ResolveActualDurationForModel(
        string? modelId, int requestedSeconds, bool isExtensionMode = false)
    {
        // ResolveBoundsForModel throws on unknown/empty/incomplete catalog rows.
        var (min, max, _) = ResolveBoundsForModel(modelId);
        var entry = SupportedModelCatalog.Find(modelId!.Trim(), ModelCapability.Video)!;

        int resolved;
        if (entry.AllowedDurationsSeconds is { Count: > 0 } allowed)
        {
            resolved = allowed
                .OrderBy(d => Math.Abs(d - requestedSeconds))
                .ThenBy(d => d)
                .First();
        }
        else
        {
            resolved = Math.Clamp(requestedSeconds, min, max);
        }

        if (isExtensionMode)
        {
            // Primary gate: supportsVideoContinue. maxExtensionSeconds is only meaningful when
            // continue is enabled — require a positive value then; do not invent a default.
            if (!entry.SupportsVideoContinue)
                throw new InvalidOperationException(
                    $"Video model '{entry.Id}' does not support video continue/extend " +
                    "(supportsVideoContinue=false).");
            if (entry.MaxExtensionSeconds is not { } extMax || extMax <= 0)
                throw new InvalidOperationException(
                    $"Video model '{entry.Id}' supports continue but has no positive " +
                    "maxExtensionSeconds in models_catalog.json.");
            if (resolved > extMax)
                resolved = extMax;
        }

        return resolved;
    }

    /// <summary>
    /// Resolves the max duration for a clip that will be generated as an extend-from-previous
    /// continuation, for planning purposes (e.g. deciding how many beats can be coalesced into one
    /// clip before it needs its own fresh cut). Falls back to <paramref name="fallbackMax"/> — the
    /// model's normal fresh-generation max — for any model without a declared tighter extension
    /// cap, matching today's behavior for every model except Grok.
    /// </summary>
    public static int ResolveExtensionMaxForModel(string? modelId, int fallbackMax)
    {
        // Unknown/empty model throws. No continue → 0 (never read maxExtensionSeconds).
        // Continue models must declare a positive maxExtensionSeconds in the catalog.
        _ = ResolveBoundsForModel(modelId);
        var entry = SupportedModelCatalog.Find(modelId!.Trim(), ModelCapability.Video)!;
        if (!entry.SupportsVideoContinue)
            return 0;
        if (entry.MaxExtensionSeconds is not { } ext || ext <= 0)
            throw new InvalidOperationException(
                $"Video model '{entry.Id}' supports continue but has no positive " +
                "maxExtensionSeconds in models_catalog.json.");
        return ext;
    }

    /// <summary>
    /// Estimate duration for a planned beat (Stage 2). Optional bounds (typically from
    /// <see cref="ResolveBoundsForModel"/>) clamp against the actually-selected video model's
    /// own limits instead of the global Grok-shaped defaults; omitted, behavior is unchanged.
    /// </summary>
    public static int EstimateForBeat(
        Dictionary<string, object?> beat,
        int minSeconds = MinSeconds,
        int maxSeconds = MaxSeconds,
        int absMaxSeconds = AbsMaxSeconds)
    {
        if (beat is null)
            return minSeconds;
        var dialogue = Coerce(beat, "dialogue");
        var visual = Coerce(beat, "visual_event");
        var actionClass = Coerce(beat, "action_class").ToLowerInvariant();
        var delivery = Coerce(beat, "delivery").ToLowerInvariant();
        return Estimate(dialogue, visual, actionClass, delivery, minSeconds, maxSeconds, absMaxSeconds);
    }

    /// <summary>
    /// Estimate from a blueprint clip element at gen time. Optional bounds (typically from
    /// <see cref="ResolveBoundsForModel"/>) let the caller clamp against the actually-selected video
    /// model's own limits instead of the global Grok-shaped defaults; omitted, behavior is unchanged.
    /// </summary>
    public static int EstimateForClip(
        JsonElement clipEl,
        int minSeconds = MinSeconds,
        int maxSeconds = MaxSeconds,
        int absMaxSeconds = AbsMaxSeconds)
    {
        if (clipEl.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return MinSeconds;

        var visual = clipEl.TryGetProperty("visual_prompt", out var vp)
            ? vp.GetString() ?? ""
            : "";
        // Stage 2's planned duration_seconds (number or numeric string), via the shared reader.
        var planned = 0;
        if (ClipDuration.TryReadNumericSeconds(clipEl, out var plannedSeconds))
            planned = (int)Math.Round(plannedSeconds, MidpointRounding.AwayFromZero);

        // Prefer action_class from Stage 2 clip (older blueprints may omit it).
        var actionClass = "";
        if (clipEl.TryGetProperty("action_class", out var acEl) &&
            acEl.ValueKind == JsonValueKind.String)
            actionClass = (acEl.GetString() ?? "").Trim().ToLowerInvariant();

        // Single source of truth for what this clip says — primary line PLUS any second speaker's
        // line (two-hander). Sizing off only the primary is what cut the teacher's answer.
        var lines = ClipSpokenLines.FromClipElement(clipEl);
        if (lines.Count > 0)
        {
            // Never under-run speech need — under-planned clips rush and clip the first word.
            // Budget for EVERY spoken line (+ inter-speaker gap), capped only at the model max.
            return EstimateSpokenLinesSeconds(lines, visual, actionClass, minSeconds, maxSeconds);
        }

        // Silent: honor Stage 2 planned duration within the class-aware cap (not a flat 5s).
        if (planned > 0)
        {
            var silentMax = SilentMaxForActionClass(actionClass, absMaxSeconds);
            return Math.Clamp(planned, ActionOnlyMinSeconds, silentMax);
        }
        return Estimate("", visual, actionClass: actionClass, "none", minSeconds, maxSeconds, absMaxSeconds);
    }

    /// <summary>
    /// Total clip length for one or more spoken lines (a two-hander sums both), capped at the model
    /// max. Each line contributes its own <see cref="EstimateUncapped"/> speech time (with head/tail
    /// pauses); the first line also carries the clip's visual/action overhead, and each additional
    /// line adds an <see cref="InterSpeakerGapSeconds"/> hand-off gap. Single source of truth for the
    /// multi-line sizing math, shared by <see cref="EstimateForClip"/> and Stage 2's cross-speaker
    /// coalesce so a clip is planned for exactly the lines <see cref="ClipSpokenLines"/> reports.
    /// </summary>
    internal static int EstimateSpokenLinesSeconds(
        IReadOnlyList<ClipSpokenLines.SpokenLine> lines,
        string visual = "",
        string actionClass = "",
        int minSeconds = MinSeconds,
        int maxSeconds = MaxSeconds)
    {
        if (lines is null || lines.Count == 0)
            return minSeconds;

        double total = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            // Only the first line pays the visual/action overhead; the rest are pure speech + gap.
            var lineVisual = i == 0 ? visual : "";
            var lineClass = i == 0 ? actionClass : "dialogue";
            total += EstimateUncapped(lines[i].Dialogue, lineVisual, lineClass, lines[i].Delivery);
            if (i > 0) total += InterSpeakerGapSeconds;
        }

        var rounded = (int)Math.Round(total, MidpointRounding.AwayFromZero);
        return Math.Clamp(Math.Max(rounded, minSeconds), minSeconds, maxSeconds);
    }

    /// <summary>Upper bound for dialogue-free clips at gen time (matches <see cref="Estimate"/> caps).</summary>
    public static int SilentMaxForActionClass(string? actionClass, int absMaxSeconds = AbsMaxSeconds) =>
        (actionClass ?? "").Trim().ToLowerInvariant() switch
        {
            "big_action" => absMaxSeconds,
            "establishing" => EstablishingMaxSeconds,
            "hold" => ActionOnlyMinSeconds,
            _ => SilentActionMaxSeconds,
        };

    /// <summary>
    /// Adjusts duration based on strongly-typed <see cref="PacingMood"/>.
    /// </summary>
    public static int AdjustForPacingMood(int baseSeconds, PacingMood mood, int minSeconds = MinSeconds, int maxSeconds = MaxSeconds)
    {
        var adjusted = mood switch
        {
            PacingMood.Slow => (int)Math.Round(baseSeconds * 1.3, MidpointRounding.AwayFromZero),
            PacingMood.Moderate => baseSeconds,
            PacingMood.Fast => (int)Math.Round(baseSeconds * 0.8, MidpointRounding.AwayFromZero),
            PacingMood.Frenetic => (int)Math.Round(baseSeconds * 0.6, MidpointRounding.AwayFromZero),
            _ => baseSeconds
        };
        return Math.Clamp(adjusted, minSeconds, maxSeconds);
    }

    /// <summary>
    /// Extra non-speech time to add on top of dialogue for a spoken clip. When the beat's
    /// visual/action text names a recognizable camera move or physical action (via
    /// <see cref="ActionConcurrencyAnalyzer"/>), uses the calibrated camera/action overheads from
    /// <see cref="ActionCameraOverheadLedger"/> — net of concurrency overlap (gamma) — instead of a
    /// flat guess. A whip pan (0.8s) and a crane/canopy shot (2.7s) no longer cost the same 0.6s.
    /// Falls back to the flat "short visual head" estimate (lip-sync / reaction buffer) when nothing
    /// specific is described, so a beat with no camera/action cues behaves exactly as before.
    /// </summary>
    private static double DialogueClipActionOverhead(string visual, string actionClass)
    {
        var flatFallback = actionClass is "big_action" ? 1.2 : 0.6;
        if (visual.Length == 0)
            return flatFallback;

        var concurrency = ActionConcurrencyAnalyzer.AnalyzeBeat(visual, null);
        var cameraDetected = concurrency.CameraId != "cam_push_in";
        var actionDetected = concurrency.ActionId != "act_generic_action";
        if (!cameraDetected && !actionDetected)
            return flatFallback;

        var ledger = new ActionCameraOverheadLedger();
        var camOverhead = cameraDetected ? ledger.GetOverheadSec(concurrency.CameraId, 0.0) : 0.0;
        var actOverhead = actionDetected ? ledger.GetOverheadSec(concurrency.ActionId, 0.0) : 0.0;
        var netActionOverhead = (1.0 - concurrency.OverlapRatioGamma) * actOverhead;
        return camOverhead + netActionOverhead;
    }

    /// <summary>
    /// Spoken-line duration: the larger of the words-per-second and syllables-per-second
    /// estimates, floored to a minimum beat, with a slight VO / internal-narration speed-up.
    /// Shared by <see cref="Estimate"/> / <see cref="EstimateBreakdown"/>.
    /// (<see cref="EstimateUncapped"/> intentionally uses a words-only variant.)
    /// </summary>
    private static double EstimateSpeechSeconds(string dlg, string delivery)
    {
        var words = CountWords(dlg);
        var syllables = CountSyllables(dlg);
        var speechFromWords = SpeechHeadSeconds + words / DialogueWordsPerSecond + SpeechTailSeconds;
        var speechFromSyllables = SpeechHeadSeconds + syllables / 4.2 + SpeechTailSeconds;
        var speech = Math.Max(speechFromWords, speechFromSyllables);
        // Very short lines still need a beat
        speech = Math.Max(1.8, speech);
        // VO can be slightly snappier
        if (delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought")
            speech *= 0.95;
        return speech;
    }

    /// <summary>
    /// Silent (no-dialogue) clip length derived from the visual/action word count, clamped
    /// per action class. Shared by <see cref="Estimate"/> / <see cref="EstimateBreakdown"/> /
    /// <see cref="EstimateUncapped"/>.
    /// </summary>
    private static double EstimateSilentActionSeconds(string visual, string actionClass)
    {
        // Cap words so long establish copy / wardrobe restatements do not buy empty seconds
        var aw = Math.Min(CountWords(visual), SilentVisualWordCap);
        return actionClass switch
        {
            "big_action" => Math.Clamp(4.5 + aw / 8.0, 5, AbsMaxSeconds),
            "establishing" => Math.Clamp(3.5 + aw / 10.0, ActionOnlyMinSeconds, EstablishingMaxSeconds),
            "hold" => ActionOnlyMinSeconds,
            _ => Math.Clamp(3.0 + aw / 12.0, ActionOnlyMinSeconds, SilentActionMaxSeconds),
        };
    }

    public static (double SpeechSec, double ActionSec) EstimateBreakdown(
        string? dialogue,
        string? visualOrAction,
        string actionClass = "",
        string delivery = "none")
    {
        var dlg = (dialogue ?? "").Trim();
        var visual = (visualOrAction ?? "").Trim();
        actionClass = (actionClass ?? "").Trim().ToLowerInvariant();
        delivery = (delivery ?? "none").Trim().ToLowerInvariant();
        if (delivery.Length == 0) delivery = "none";

        double speech = 0;
        if (dlg.Length > 0)
            speech = EstimateSpeechSeconds(dlg, delivery);

        double action = 0;
        if (dlg.Length == 0)
            action = EstimateSilentActionSeconds(visual, actionClass);
        else
            action = DialogueClipActionOverhead(visual, actionClass);

        return (Math.Round(speech, 1), Math.Round(action, 1));
    }

    public static int Estimate(
        string? dialogue,
        string? visualOrAction,
        string actionClass = "",
        string delivery = "none",
        int minSeconds = MinSeconds,
        int maxSeconds = MaxSeconds,
        int absMaxSeconds = AbsMaxSeconds)
    {
        var dlg = (dialogue ?? "").Trim();
        var visual = (visualOrAction ?? "").Trim();
        // Callers may pass mixed-case labels from JSON / blueprints
        actionClass = (actionClass ?? "").Trim().ToLowerInvariant();
        delivery = (delivery ?? "none").Trim().ToLowerInvariant();
        if (delivery.Length == 0) delivery = "none";

        double speech = 0;
        if (dlg.Length > 0)
            speech = EstimateSpeechSeconds(dlg, delivery);

        double action = 0;
        if (dlg.Length == 0)
            action = EstimateSilentActionSeconds(visual, actionClass);
        else
            // Dialogue clip: calibrated camera/action overhead when the beat names something
            // specific, else the flat "short visual head" buffer (lip-sync / reaction under the line)
            action = DialogueClipActionOverhead(visual, actionClass);

        var total = speech + action;
        if (total <= 0)
            total = ActionOnlyMinSeconds;

        var rounded = (int)Math.Round(total, MidpointRounding.AwayFromZero);
        if (dlg.Length == 0)
        {
            var silentMax = actionClass switch
            {
                "big_action" => absMaxSeconds,
                "establishing" => EstablishingMaxSeconds,
                "hold" => ActionOnlyMinSeconds,
                _ => SilentActionMaxSeconds,
            };
            return Math.Clamp(rounded, ActionOnlyMinSeconds, silentMax);
        }

        return Math.Clamp(rounded, minSeconds, maxSeconds);
    }

    /// <summary>
    /// Same as <see cref="Estimate"/> but without the model max clamp — used to detect
    /// monologues that would be crushed into <see cref="MaxSeconds"/>.
    /// </summary>
    public static double EstimateUncapped(
        string? dialogue,
        string? visualOrAction,
        string actionClass = "",
        string delivery = "none")
    {
        var dlg = (dialogue ?? "").Trim();
        var visual = (visualOrAction ?? "").Trim();
        actionClass = (actionClass ?? "").Trim().ToLowerInvariant();
        delivery = (delivery ?? "none").Trim().ToLowerInvariant();
        if (delivery.Length == 0) delivery = "none";

        // NOTE: intentionally a words-only speech estimate (no syllable-based max) — this uncapped
        // variant only needs to spot lines that would overflow the model max, so it stays simpler
        // than EstimateSpeechSeconds by design.
        double speech = 0;
        if (dlg.Length > 0)
        {
            var words = CountWords(dlg);
            speech = SpeechHeadSeconds + words / DialogueWordsPerSecond + SpeechTailSeconds;
            speech = Math.Max(1.8, speech);
            if (delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought")
                speech *= 0.95;
        }

        double action = 0;
        if (dlg.Length == 0)
            action = EstimateSilentActionSeconds(visual, actionClass);
        else
            action = DialogueClipActionOverhead(visual, actionClass);

        var total = speech + action;
        if (total <= 0)
            total = ActionOnlyMinSeconds;
        return total;
    }

    /// <summary>
    /// True when spoken dialogue (+ visual head) needs more time than the model clip max
    /// after packing padding — monologue should be split into multiple beats/clips.
    /// </summary>
    public static bool DialogueExceedsModelMax(
        string? dialogue,
        string delivery = "spoken_on_camera",
        int modelMaxSeconds = MaxSeconds,
        double paddingSeconds = DialogueModelPaddingSeconds)
    {
        if (string.IsNullOrWhiteSpace(dialogue))
            return false;
        var budget = Math.Max(MinSeconds, modelMaxSeconds - paddingSeconds);
        var need = EstimateUncapped(dialogue, visualOrAction: "", actionClass: "dialogue", delivery: delivery);
        return need > budget + 0.01;
    }

    /// <summary>
    /// Split dialogue so each piece fits under the model max with padding.
    /// Prefers sentence / clause boundaries; falls back to word packs for run-ons.
    /// </summary>
    public static IReadOnlyList<string> SplitDialogueToFitModelMax(
        string? dialogue,
        string delivery = "spoken_on_camera",
        int modelMaxSeconds = MaxSeconds,
        double paddingSeconds = DialogueModelPaddingSeconds)
    {
        var text = (dialogue ?? "").Trim();
        if (text.Length == 0)
            return Array.Empty<string>();
        if (!DialogueExceedsModelMax(text, delivery, modelMaxSeconds, paddingSeconds))
            return new[] { text };

        var budget = Math.Max(MinSeconds, modelMaxSeconds - paddingSeconds);
        var units = SegmentDialogueUnits(text);
        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            var s = current.ToString().Trim();
            current.Clear();
            if (s.Length > 0)
                chunks.Add(s);
        }

        foreach (var unit in units)
        {
            var u = unit.Trim();
            if (u.Length == 0) continue;

            // Unit alone still too long → pack by words
            if (EstimateUncapped(u, "", "dialogue", delivery) > budget)
            {
                Flush();
                foreach (var piece in PackByWords(u, delivery, budget))
                    chunks.Add(piece);
                continue;
            }

            var trial = current.Length == 0 ? u : current + " " + u;
            if (current.Length > 0 &&
                EstimateUncapped(trial, "", "dialogue", delivery) > budget)
            {
                Flush();
                current.Append(u);
            }
            else
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(u);
            }
        }

        Flush();
        return chunks.Count > 0 ? chunks : new[] { text };
    }

    /// <summary>
    /// Expand story beats: long monologue / dialogue becomes multiple beats that each fit
    /// the video model max. Action beats and short lines pass through. Preserves stable
    /// <c>beat_id</c> roots and uses <c>#pNofM</c> for split parts (does not renumber to b1…).
    /// </summary>
    public static List<Dictionary<string, object?>> ExpandLongDialogueBeats(
        IReadOnlyList<Dictionary<string, object?>>? beats,
        int modelMaxSeconds = MaxSeconds,
        double paddingSeconds = DialogueModelPaddingSeconds)
    {
        var result = new List<Dictionary<string, object?>>();
        if (beats is null || beats.Count == 0)
            return result;

        foreach (var beat in beats)
        {
            if (beat is null)
                continue;

            var dialogue = Coerce(beat, "dialogue");
            if (string.IsNullOrWhiteSpace(dialogue) &&
                beat.TryGetValue("audio", out var a0) && a0 is Dictionary<string, object?> audio0)
                dialogue = Coerce(audio0, "dialogue");

            var delivery = Coerce(beat, "delivery");
            if (string.IsNullOrWhiteSpace(delivery) &&
                beat.TryGetValue("audio", out var a1) && a1 is Dictionary<string, object?> audio1)
                delivery = Coerce(audio1, "delivery");
            if (string.IsNullOrWhiteSpace(delivery))
                delivery = "spoken_on_camera";

            var sourceId = Coerce(beat, "beat_id");
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                sourceId = PageToMovie.Core.Utils.StableBeatId.ForContent(
                    "",
                    string.IsNullOrWhiteSpace(dialogue) ? "action" : "dialogue",
                    Coerce(beat, "speaker"),
                    string.IsNullOrWhiteSpace(dialogue) ? Coerce(beat, "visual_event") : dialogue);
            }

            if (string.IsNullOrWhiteSpace(dialogue) ||
                !DialogueExceedsModelMax(dialogue, delivery, modelMaxSeconds, paddingSeconds))
            {
                result.Add(CloneBeatWithId(beat, sourceId, dialogueOverride: null, partIndex: 0, partCount: 1));
                continue;
            }

            var parts = SplitDialogueToFitModelMax(dialogue, delivery, modelMaxSeconds, paddingSeconds);
            var root = PageToMovie.Core.Utils.StableBeatId.Root(sourceId);
            for (var p = 0; p < parts.Count; p++)
            {
                var partId = PageToMovie.Core.Utils.StableBeatId.ForPart(root, p, parts.Count);
                var copy = CloneBeatWithId(beat, partId, parts[p], p, parts.Count);
                copy["source_beat_ids"] = new List<object?> { root };
                result.Add(copy);
            }
        }

        return result;
    }

    /// <summary>
    /// Allocate one duration per beat from content (not forced scene-budget padding).
    /// Optional scene target may add at most 1s to non-hold silent clips (never dialogue, never hold).
    /// Optional bounds (typically from <see cref="ResolveBoundsForModel"/>) clamp against the
    /// actually-selected video model's own limits instead of the global Grok-shaped defaults.
    /// </summary>
    public static List<int> AllocateForBeats(
        IReadOnlyList<Dictionary<string, object?>> beats,
        int? sceneTargetSeconds = null,
        int minSeconds = MinSeconds,
        int maxSeconds = MaxSeconds,
        int absMaxSeconds = AbsMaxSeconds)
    {
        if (beats is null || beats.Count == 0)
            return new List<int>();
        // Null list entries are treated as empty action beats (not skipped — preserve index alignment)
        var durs = beats.Select(b => EstimateForBeat(b!, minSeconds, maxSeconds, absMaxSeconds)).ToList();

        if (sceneTargetSeconds is int target && target > durs.Sum() + 2)
        {
            // Sharply limited pad: silent empty holds must not absorb monologue-scale scene budget
            var need = Math.Min(target - durs.Sum(), beats.Count); // at most +1s per beat overall
            var actionIdx = new List<int>();
            for (var i = 0; i < beats.Count; i++)
            {
                if (beats[i] is null) continue;
                var dlg = Coerce(beats[i]!, "dialogue");
                if (!string.IsNullOrWhiteSpace(dlg)) continue;
                var ac = Coerce(beats[i]!, "action_class").ToLowerInvariant();
                if (ac is "hold") continue; // never pad micro-beats
                var maxFor = SilentPadCap(ac, absMaxSeconds);
                if (durs[i] < maxFor)
                    actionIdx.Add(i);
            }

            var guard = 0;
            while (need > 0 && actionIdx.Count > 0 && guard++ < 50)
            {
                var progressed = false;
                foreach (var i in actionIdx)
                {
                    if (need <= 0) break;
                    var ac = beats[i] is null ? "" : Coerce(beats[i]!, "action_class").ToLowerInvariant();
                    var maxFor = SilentPadCap(ac, absMaxSeconds);
                    if (durs[i] >= maxFor) continue;
                    durs[i]++;
                    need--;
                    progressed = true;
                }
                if (!progressed) break;
            }
        }

        return durs;
    }

    private static int SilentPadCap(string actionClass, int absMaxSeconds = AbsMaxSeconds) =>
        actionClass switch
        {
            "big_action" => absMaxSeconds,
            "establishing" => EstablishingMaxSeconds,
            "hold" => ActionOnlyMinSeconds,
            _ => SilentActionMaxSeconds,
        };

    public static int CountWords(string text) => TextMetrics.CountWords(text);

    public static int CountSyllables(string text) => TextMetrics.CountSyllables(text);

    /// <summary>
    /// Sentence / clause units for packing (keeps trailing punctuation on the unit).
    /// </summary>
    internal static List<string> SegmentDialogueUnits(string text)
    {
        text = CommonRegex.Replace(text.Trim(), @"\s+", " ");
        if (text.Length == 0)
            return new List<string>();

        // Split after . ! ? or ; or em/en dash phrases, keep delimiter on left piece.
        var parts = CommonRegex.Split(
            text,
            @"(?<=[.!?;])\s+|(?<=\u2014|\u2013|--)\s+");
        var list = parts
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return list.Count > 0 ? list : new List<string> { text };
    }

    private static List<string> PackByWords(string text, string delivery, double budgetSeconds)
    {
        var words = CommonRegex.Matches(text, @"[\p{L}\p{N}']+|[^\s\p{L}\p{N}]+")
            .Select(m => m.Value)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();
        if (words.Count == 0)
            return new List<string> { text.Trim() };

        var chunks = new List<string>();
        var current = new List<string>();

        string Join(List<string> ws)
        {
            var sb = new StringBuilder();
            var suppressNextSpace = false;
            foreach (var w in ws)
            {
                // Em-dash/en-dash/hyphen tokens attach directly to both neighbors (source text has
                // no surrounding space, e.g. "nights—every") — unlike trailing punctuation (.!?;,:),
                // which only suppresses its OWN leading space, a dash also suppresses the space
                // before the word that follows it.
                var isDash = CommonRegex.IsMatch(w, @"^[—–-]+$");
                var noLeadingSpace = suppressNextSpace || isDash || CommonRegex.IsMatch(w, @"^[.!?;,:]+$");
                if (sb.Length == 0 || noLeadingSpace)
                    sb.Append(w);
                else
                    sb.Append(' ').Append(w);
                suppressNextSpace = isDash;
            }
            return sb.ToString().Trim();
        }

        foreach (var w in words)
        {
            current.Add(w);
            var trial = Join(current);
            if (EstimateUncapped(trial, "", "dialogue", delivery) > budgetSeconds && current.Count > 1)
            {
                current.RemoveAt(current.Count - 1);
                chunks.Add(Join(current));
                current.Clear();
                current.Add(w);
            }
        }

        if (current.Count > 0)
            chunks.Add(Join(current));

        // Merge orphan trailing words (<= 2 words, e.g. "it") into preceding chunk if budget allows
        if (chunks.Count > 1 && CountWords(chunks[^1]) <= 2)
        {
            var combined = $"{chunks[^2]} {chunks[^1]}";
            if (EstimateUncapped(combined, "", "dialogue", delivery) <= budgetSeconds + 1.5)
            {
                chunks[^2] = combined;
                chunks.RemoveAt(chunks.Count - 1);
            }
        }

        return chunks;
    }

    private static Dictionary<string, object?> CloneBeatWithId(
        Dictionary<string, object?> source,
        string beatId,
        string? dialogueOverride,
        int partIndex,
        int partCount)
    {
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in source)
        {
            if (v is Dictionary<string, object?> nested)
                copy[k] = new Dictionary<string, object?>(nested, StringComparer.OrdinalIgnoreCase);
            else if (v is List<object?> list)
                copy[k] = list.ToList();
            else
                copy[k] = v;
        }

        copy["beat_id"] = beatId;
        if (dialogueOverride is not null)
        {
            copy["dialogue"] = dialogueOverride;
            if (copy.TryGetValue("audio", out var a) && a is Dictionary<string, object?> audio)
                audio["dialogue"] = dialogueOverride;

            var words = CountWords(dialogueOverride);
            copy["time_weight"] = Math.Clamp(words / 8.0, 0.5, 4.0);

            if (partCount > 1)
            {
                if (partIndex > 0)
                    copy["continuity"] = "continuous_from_previous_beat";
                // Keep visual_event stable (e.g. "NARRATOR speaks.") for monologue parts
            }
        }

        return copy;
    }

    private static string Coerce(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString()?.Trim() ?? "" : "";
}
