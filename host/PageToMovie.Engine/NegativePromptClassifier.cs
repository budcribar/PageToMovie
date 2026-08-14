using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// AI Classifier acting as a Period Visual Continuity Guard.
/// Generates context-aware, era-specific anachronism negative prompts
/// (e.g., "no modern wristwatches, no electric lamps, no plastic, no zippers").
/// </summary>
public sealed class NegativePromptClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<NegativePromptClassifier> _log;

    public NegativePromptClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<NegativePromptClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyNegativePromptWithChat && _chat.IsConfigured;

    public const string SystemPrompt = """
        You are an expert film historian and Period Visual Continuity Guard preventing anachronisms in video generation.

        Your task: Given a scene's setting, period style, and location, generate a comma-separated list of 5–15 era-specific negative prompt tokens preventing period violations and visual glitches.

        RULES (HARD):
        1. Identify the Era:
           - If 19th Century / Gothic: "no modern wristwatches, no electric light bulbs, no plastic, no zippers, no modern cars, no printed logos, no denim, no asphalt, no modern hair gel"
           - If Medieval / Fantasy: "no modern clothes, no eyeglasses, no metal buttons, no paved roads, no power lines, no modern buildings"
           - If Sci-Fi / Future: "no primitive wooden furniture, no candles, no horses, no vintage cars"
        2. Keep tokens concise, negative, comma-separated.
        3. Do NOT include positive prompt descriptions.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "negative_tokens": "no modern wristwatches, no electric lamps, no plastic, no sneakers, no zippers, no printed text"
        }
        """;

    public Task<string?> ClassifySceneNegativeAsync(Dictionary<string, object?> scene, Action<string>? onProgress = null, CancellationToken ct = default, string? model = null) =>
        ClassifierTextDirectiveRunner.ClassifyAsync(
            IsEnabled, onProgress,
            $"AI Period Guard: Generating anachronism negatives for Scene {scene.GetValueOrDefault("scene_number")}…",
            new ClassifierDirectiveRun(
                _chat, _log, scene,
                new ClassifierDirectiveSpec(
                    SystemPrompt,
                    () => ClassifierPromptParts.BuildSceneUserPrompt(scene, "RENDER STYLE / PERIOD LOCK", includeSampleBeats: false),
                    model, _opts.NegativePromptClassifyModel, "negative_prompt", PromptVersion,
                    "negative_tokens", ChatCallModes.NegativePromptClassify, "negative prompt"),
                ct));
}
