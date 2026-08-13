using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    public Task<string?> ClassifySceneLightingAsync(Dictionary<string, object?> scene, Action<string>? onProgress = null, CancellationToken ct = default, string? model = null) =>
        ClassifierTextDirectiveRunner.ClassifyAsync(IsEnabled, onProgress, $"AI Cinematic Lighting: Analyzing lighting & mood for Scene {scene.GetValueOrDefault("scene_number")}…", _chat, _log, scene, SystemPrompt(), () => ClassifierPromptParts.BuildSceneUserPrompt(scene, "RENDER STYLE LOCK", includeSampleBeats: true), model, _opts.CinematicLightingClassifyModel, "cinematic_lighting", PromptVersion, "lighting_token", ChatCallModes.CinematicLightingClassify, "cinematic lighting", ct);
}
