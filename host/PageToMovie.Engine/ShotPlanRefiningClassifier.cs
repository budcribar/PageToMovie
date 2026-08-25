using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// AI Shot-Plan Refiner for multi-clip scenes in Stage 2 planning.
/// Eliminates copy-pasted visual prompt stagnation across extended monologues
/// by assigning progressive camera framings and micro-action beats.
/// </summary>
public sealed class ShotPlanRefiningClassifier
{
    public const string PromptVersion = "v1_product";

    private const string KeyVisualPrompt = "visual_prompt";
    private const string KeyContinuation = "veo_continuation_source";
    private const string KeyContinuityRule = "continuity_rule";
    private const string ExtendPrevious = "extend_previous";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<ShotPlanRefiningClassifier> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    public ShotPlanRefiningClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<ShotPlanRefiningClassifier> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
        _errorLogger = errorLogger;
    }

    public bool IsEnabled => _opts.ClassifyShotPlanRefineWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film director and cinematographer refining shot plans for a multi-clip scene in a movie screenplay.

        Your task: Given a list of planned video clips for a scene, eliminate copy-pasted visual prompt stagnation across consecutive clips by generating progressive camera framings and micro-action beats.

        RULES (HARD):
        1. Preserve exact character identity tokens (e.g. Character_The_Narrator, Character_The_Old_Man) and location tokens (e.g. Loc_Old_Mans_Bedchamber).
        2. Do NOT invent unscripted major plot events or add unmentioned characters.
        3. Evolve the visual framing logically across clips:
           - Clip 1: Establishing / medium shot setting up the scene.
           - Mid clips: Dynamic shot progression (e.g. Extreme Close-Up on key prop/detail, Over-The-Shoulder, or reaction shot).
           - Later clips: Wide holding shot or intense reaction shot matching monologue climax.
        4. Continuation rules:
           - When changing to a distinct new camera angle/framing (e.g. close-up on detail), set veo_continuation_source to "none".
           - When continuing or holding the previous angle, set veo_continuation_source to "extend_previous".
        5. Framing & Headroom:
           - Maintain generous vertical headroom above characters' heads and hair across all framings (never crop foreheads, hair, or scalps).
           - Avoid edge-crowding phrases like "filling frame" or "tightly framed". Keep all subjects comfortably bounded in frame.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "refinements": [
            {
              "clip_number": 1,
              "visual_prompt": "INT. OLD MAN'S BEDCHAMBER - DAY. Wide establishing shot. Character_The_Narrator in doorway.",
              "veo_continuation_source": "none"
            },
            ...
          ]
        }
        """;

    public async Task<bool> RefinePlannedSceneAsync(
        Dictionary<string, object?> plannedScene,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled) return false;

        var clips = TryGetStagnantClips(plannedScene);
        if (clips is null) return false;

        onProgress?.Invoke($"AI Shot Refiner: Variating camera framing across {clips.Count} clips…");

        try
        {
            return await RefineClipsAsync(plannedScene, clips, model, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI shot plan refinement for scene {Scene}", plannedScene.GetValueOrDefault(JsonKeys.SceneNumber));
            return false;
        }
    }

    private async Task<bool> RefineClipsAsync(
        Dictionary<string, object?> plannedScene,
        List<Dictionary<string, object?>> clips,
        string? model,
        CancellationToken ct)
    {
        var userPrompt = BuildUserPrompt(plannedScene, clips);
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.ShotPlanRefineClassifyModel;
        var cacheKey = $"{userPrompt}|m:{effectiveModel}";
        var requestedIds = clips
            .Select(c => ToInt(c.GetValueOrDefault(JsonKeys.ClipNumber)).ToString())
            .ToList();

        // Cache only the first attempt's call — a coverage retry needs a fresh response,
        // not the same (possibly incomplete) cached text replayed again.
        var firstAttempt = true;
        var retry = await AiRetryPolicy.RunWithCoverageRetryAsync(
            requestedIds,
            missingIds =>
            {
                var allowCache = firstAttempt;
                firstAttempt = false;
                return CompleteCachedAsync(
                    userPrompt, requestedIds, missingIds, cacheKey, effectiveModel, allowCache, ct);
            },
            ParseRefinementsDict,
            maxAttempts: AiRetryPolicy.DefaultCoverageMaxAttempts,
            backoffBaseMs: AiRetryPolicy.DefaultCoverageBackoffMs,
            ct: ct,
            operationName: "stage2_shot_plan_refining",
            promptVersion: "1",
            model: effectiveModel).ConfigureAwait(false);

        if (_errorLogger is not null)
        {
            var sceneNum = ClassifierValueHelpers.ToIntOrNull(plannedScene.GetValueOrDefault(JsonKeys.SceneNumber));
            await _errorLogger.LogCoverageResultAsync(
                "shot_plan_refining_classifier", effectiveModel, ClassifierValueHelpers.ResolveProvider(effectiveModel), sceneNum,
                requestedIds, retry, ct).ConfigureAwait(false);
        }

        if (retry.Result is not { Count: > 0 } refDict) return false;

        foreach (var clip in clips)
        {
            var key = ToInt(clip.GetValueOrDefault(JsonKeys.ClipNumber)).ToString();
            if (refDict.TryGetValue(key, out var refTuple))
            {
                clip[KeyVisualPrompt] = refTuple.VisualPrompt;
                DropContinuityRuleIfNewlyExtending(clip, refTuple.Continuation);
                clip[KeyContinuation] = refTuple.Continuation;
            }
        }

        _log.LogInformation("AI Shot Refiner applied dynamic camera framings to {Count} clips in scene {Scene}",
            refDict.Count, plannedScene.GetValueOrDefault(JsonKeys.SceneNumber));
        return true;
    }

    /// <summary>
    /// This refiner picks continuation from camera framing alone — it never asks whether the clip's
    /// action opens where the previous clip's action left the cast, which is the check Stage 2 runs
    /// (and stamps as <c>continuity_rule</c>) when it decides a beat's cut_decision. So when the
    /// refiner turns a clip INTO a continuation, the stamp it inherited no longer describes the
    /// decision that stands, and leaving it would let ShotPlanLint report a checked continuation
    /// that nothing checked. Dropping it is the honest answer: the lint says "unchecked".
    /// </summary>
    private static void DropContinuityRuleIfNewlyExtending(Dictionary<string, object?> clip, string continuation)
    {
        var wasExtending = string.Equals(
            clip.GetValueOrDefault(KeyContinuation)?.ToString(), ExtendPrevious, StringComparison.OrdinalIgnoreCase);
        var nowExtending = string.Equals(continuation, ExtendPrevious, StringComparison.OrdinalIgnoreCase);
        if (nowExtending && !wasExtending)
            clip.Remove(KeyContinuityRule);
    }

    private static List<Dictionary<string, object?>>? TryGetStagnantClips(Dictionary<string, object?> plannedScene)
    {
        if (!plannedScene.TryGetValue("veo_clips", out var clipsObj) ||
            clipsObj is not List<object?> rawClips || rawClips.Count < 3)
        {
            return null; // Skip single/dual clip scenes (no stagnation risk)
        }

        var clips = rawClips.OfType<Dictionary<string, object?>>().ToList();
        if (clips.Count < 3) return null;

        // Check if prompts are copy-pasted/duplicated across clips
        var prompts = clips.Select(c => CoerceString(c.TryGetValue(KeyVisualPrompt, out var vp) ? vp : null)).ToList();
        var uniquePrompts = prompts.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (uniquePrompts > (clips.Count / 2))
        {
            // Scene already has sufficient visual diversity
            return null;
        }

        return clips;
    }

    private async Task<string> CompleteCachedAsync(
        string userPrompt,
        IReadOnlyList<string> requestedIds,
        IReadOnlyList<string> missingIds,
        string cacheKey,
        string? effectiveModel,
        bool allowCache,
        CancellationToken ct)
    {
        var focusedPrompt = AiRetryPolicy.FocusCoveragePrompt(userPrompt, requestedIds, missingIds);
        if (allowCache && Cache.TryGetValue(cacheKey, out var cachedResp))
            return cachedResp;
        var raw = await _chat.CompleteAsync(
            SystemPrompt(),
            focusedPrompt,
            effectiveModel ?? "",
            // 0, not 0.2 — see BeatPacingClassifier for why (cacheable categorical labeling).
            temperature: 0,
            ct: ct,
            mode: ChatCallModes.ShotPlanRefineClassify).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw))
            Cache[cacheKey] = raw;
        return raw;
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> clips)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault(JsonKeys.SceneNumber)}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine($"CHARACTERS ON SCREEN: {JsonSerializer.Serialize(scene.GetValueOrDefault("characters_on_screen"))}");
        var targetAspectRatio = scene.GetValueOrDefault("target_aspect_ratio")?.ToString();
        var visualMedium = scene.GetValueOrDefault("visual_medium")?.ToString();
        if (!string.IsNullOrWhiteSpace(targetAspectRatio) || !string.IsNullOrWhiteSpace(visualMedium))
        {
            if (!string.IsNullOrWhiteSpace(targetAspectRatio))
                sb.AppendLine($"TARGET ASPECT RATIO: {targetAspectRatio}");
            if (!string.IsNullOrWhiteSpace(visualMedium))
                sb.AppendLine($"VISUAL MEDIUM: {visualMedium}");
        }
        sb.AppendLine();
        sb.AppendLine("PLANNED CLIPS:");

        foreach (var c in clips)
        {
            var cNum = c.GetValueOrDefault(JsonKeys.ClipNumber);
            var dur = c.GetValueOrDefault("duration_seconds");
            var audio = c.TryGetValue("audio_payload", out var aObj) && aObj is Dictionary<string, object?> aDict
                ? CoerceString(aDict.GetValueOrDefault("dialogue"))
                : "";
            var prompt = c.GetValueOrDefault(KeyVisualPrompt);

            sb.AppendLine($"Clip {cNum} ({dur}s):");
            if (!string.IsNullOrWhiteSpace(audio))
                sb.AppendLine($"  Dialogue/VO: \"{audio}\"");
            sb.AppendLine($"  Current Prompt: {prompt}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pure parse: clip_number (as string, for <see cref="AiRetryPolicy.CheckCoverage"/>) → refinement.
    /// Applying the result to <c>clips</c> is the caller's job (<see cref="RefinePlannedSceneAsync"/>) —
    /// keeping parse/apply separate lets a coverage retry re-parse without re-mutating already-applied clips.
    /// </summary>
    private Dictionary<string, (string VisualPrompt, string Continuation)>? ParseRefinementsDict(string rawJson)
    {
        try
        {
            var cleaned = ClassifierJsonParser.StripFences(rawJson);
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("refinements", out var refArray) ||
                refArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var refDict = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in refArray.EnumerateArray())
            {
                if (item.TryGetProperty(JsonKeys.ClipNumber, out var cn) &&
                    item.TryGetProperty(KeyVisualPrompt, out var vp))
                {
                    var num = cn.GetInt32();
                    var prompt = vp.GetString() ?? "";
                    var cont = item.TryGetProperty("veo_continuation_source", out var cs)
                        ? cs.GetString() ?? "none"
                        : "none";
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        refDict[num.ToString()] = (prompt, cont);
                    }
                }
            }

            return refDict.Count > 0 ? refDict : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI shot plan refiner response JSON: {RawJson}", rawJson);
            return null;
        }
    }

    private static string CoerceString(object? val) => val?.ToString() ?? "";
    private static int ToInt(object? val) => val switch
    {
        int i => i,
        long l => (int)l,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
        string s when int.TryParse(s, out var p) => p,
        _ => 0,
    };
}
