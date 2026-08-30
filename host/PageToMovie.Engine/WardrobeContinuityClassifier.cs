using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// AI classifier that proposes <em>delta</em> wardrobe layers (coat for rain, nightshirt
/// at bed). Identity garments live on the Stage 1 list; this never replaces that list.
/// </summary>
public sealed class WardrobeContinuityClassifier
{
    public const string PromptVersion = "v2_delta";
    private const string SceneNumberKey = "scene_number";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<WardrobeContinuityClassifier> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    public WardrobeContinuityClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<WardrobeContinuityClassifier> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
        _errorLogger = errorLogger;
    }

    public bool IsEnabled => _opts.ClassifyWardrobeContinuityWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film Costume Department Supervisor managing wardrobe continuity across scenes.

        The CURRENT WARDROBE list per character is the single source of truth (signature garments
        plus any scene sticky items). Your job is a DELTA: name only extra layers the beats
        actually require. Do not invent a second outfit.

        RULES (HARD):
        1. Keep every identity garment already listed. Do NOT drop, replace, or omit a signature
           piece unless a beat wardrobe_remove says so (those removals are applied elsewhere).
        2. You MAY add a layer the beats need:
           - Going to bed / asleep: add nightwear (e.g. "loose white cotton nightshirt").
           - Rain / travel / outdoor cold: add outerwear (e.g. "wool walking coat").
           - Do not add a trench coat, waistcoat, or nightshirt just because the setting is
             outdoor / day / night if the current list already covers the beat.
        3. attire = only the added layer(s), 2–12 words. If no extra layer is needed, repeat
           the current list (never substitute a different outfit).
        4. Do NOT omit any character keys provided in the prompt.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "wardrobe": [
            {
              "character_key": "Character_The_Narrator",
              "attire": "wool walking coat"
            },
            ...
          ]
        }
        """;

    public async Task<Dictionary<string, string>?> ClassifySceneWardrobeAsync(
        Dictionary<string, object?> scene,
        List<string> cast,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null,
        Dictionary<string, object?>? charSeeds = null)
    {
        if (!IsEnabled || cast.Count == 0) return null;

        onProgress?.Invoke($"AI Costume Supervisor: Determining attire for {cast.Count} character(s) in Scene {scene.GetValueOrDefault(SceneNumberKey)}…");

        try
        {
            var userPrompt = BuildUserPrompt(scene, cast, charSeeds);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.WardrobeContinuityClassifyModel;
            var requestedIds = cast.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

            var retry = await AiRetryPolicy.RunWithCoverageRetryAsync(
                requestedIds,
                missingIds => _chat.CompleteAsync(
                    SystemPrompt(),
                    AiRetryPolicy.FocusCoveragePrompt(userPrompt, requestedIds, missingIds),
                    effectiveModel,
                    // 0, not 0.2 — see BeatPacingClassifier for why (cacheable categorical labeling).
                    temperature: 0,
                    ct: ct,
                    mode: ChatCallModes.WardrobeContinuityClassify),
                ParseWardrobeResponse,
                maxAttempts: AiRetryPolicy.DefaultCoverageMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultCoverageBackoffMs,
                ct: ct,
                operationName: "stage2_wardrobe_continuity",
                promptVersion: "1",
                model: effectiveModel).ConfigureAwait(false);

            if (_errorLogger is not null)
            {
                var sceneNum = ClassifierValueHelpers.ToIntOrNull(scene.GetValueOrDefault(SceneNumberKey));
                await _errorLogger.LogCoverageResultAsync(
                    "wardrobe_continuity_classifier", effectiveModel, ClassifierValueHelpers.ResolveProvider(effectiveModel), sceneNum,
                    requestedIds, retry, ct).ConfigureAwait(false);
            }

            return retry.Result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI wardrobe continuity classification for scene {Scene}", scene.GetValueOrDefault(SceneNumberKey));
            return null;
        }
    }

    internal static string BuildUserPrompt(
        Dictionary<string, object?> scene,
        List<string> cast,
        Dictionary<string, object?>? charSeeds = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault(SceneNumberKey)}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine($"CHARACTERS ON SCREEN: {string.Join(", ", cast)}");
        AppendCurrentWardrobe(sb, scene, cast, charSeeds);
        ClassifierPromptParts.AppendSampleBeats(sb, scene);
        return sb.ToString();
    }

    private static void AppendCurrentWardrobe(
        System.Text.StringBuilder sb,
        Dictionary<string, object?> scene,
        List<string> cast,
        Dictionary<string, object?>? charSeeds)
    {
        sb.AppendLine("CURRENT WARDROBE (identity — do not drop unless a beat removes it):");
        var any = false;
        foreach (var key in cast)
        {
            var items = WardrobeState.IdentityItems(key, charSeeds, scene);
            if (items.Count == 0)
                continue;
            sb.AppendLine($"  - {key}: {string.Join(", ", items)}");
            any = true;
        }

        if (!any)
            sb.AppendLine("  (none listed — do not invent a full replacement outfit)");
    }

    private Dictionary<string, string>? ParseWardrobeResponse(string rawJson)
    {
        try
        {
            var cleaned = ClassifierJsonParser.StripFences(rawJson);
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("wardrobe", out var wArray) ||
                wArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in wArray.EnumerateArray())
            {
                if (item.TryGetProperty("character_key", out var ck) &&
                    item.TryGetProperty("attire", out var att))
                {
                    var key = ck.GetString() ?? "";
                    var attire = att.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(attire))
                    {
                        result[key] = attire;
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI wardrobe response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
