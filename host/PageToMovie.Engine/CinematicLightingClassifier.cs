using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Engine.ModelExecution;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// AI Classifier that generates rich, atmospheric lighting and mood color palettes
/// for scene shells (replacing generic static "consistent scene lighting" tokens).
/// </summary>
public sealed class CinematicLightingClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<CinematicLightingClassifier> _log;

    public CinematicLightingClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<CinematicLightingClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyCinematicLightingWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film cinematographer and lighting director specifying scene lighting and color palettes.

        Your task: Given a scene's location, time of day, and emotional beats, generate a single concise cinematic lighting and mood description string (15–60 words) that locks the lighting style across all shots in the scene.

        RULES (HARD):
        1. Concise & Filmic: Include key light sources, shadow quality, volumetric effects, and color temperature palette.
           - Example (Gothic/Night): "Chiaroscuro flickering candlelight with deep obsidian shadows and desaturated cool-gray volumetric fog."
           - Example (Warm/Day): "Warm golden-hour sunlight at low angle, high contrast shadows with warm amber color grade."
           - Example (Interior/Night Standoff): "Single harsh shaft of moonlight cutting pitch black room, ultra-cool cobalt shadows."
        2. Keep exact location mood intact.
        3. Do NOT include camera resolution, fps, or negative tags.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "lighting_token": "Chiaroscuro flickering candlelight with deep obsidian shadows and cool-gray volumetric fog"
        }
        """;

    public async Task<string?> ClassifySceneLightingAsync(
        Dictionary<string, object?> scene,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled) return null;

        onProgress?.Invoke($"AI Cinematic Lighting: Analyzing lighting & mood for Scene {scene.GetValueOrDefault("scene_number")}…");

        try
        {
            var userPrompt = BuildUserPrompt(scene);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.CinematicLightingClassifyModel;
            var pipeline = new ValidatedModelOperation<Stage2DirectiveInput, TextDirective>(
                new Stage2DirectiveOperation(_chat, "cinematic_lighting", PromptVersion),
                new JsonTextDirectiveParser("lighting_token"), new TextDirectiveValidator("lighting_token"),
                new DirectiveTerminalFallback<Stage2DirectiveInput, TextDirective>(), new ModelOperationOptions { CorrectiveMaxAttempts = 1 });
            var result = await pipeline.ExecuteAsync(new(SystemPrompt(), userPrompt, effectiveModel, ChatCallModes.CinematicLightingClassify), ct).ConfigureAwait(false);
            return result.Value?.Value;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI cinematic lighting classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        if (scene.TryGetValue("render_style_lock", out var rsl) && !string.IsNullOrWhiteSpace(rsl?.ToString()))
            sb.AppendLine($"RENDER STYLE LOCK: {rsl}");

        ClassifierPromptParts.AppendSampleBeats(sb, scene);

        return sb.ToString();
    }

    private string? ParseLightingResponse(string rawJson)
    {
        try
        {
            var cleaned = ClassifierJsonParser.StripFences(rawJson);
            using var doc = JsonDocument.Parse(cleaned);
            var token = doc.RootElement.GetStringProp("lighting_token").Trim();
            if (!string.IsNullOrWhiteSpace(token))
                return token;
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI cinematic lighting response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
