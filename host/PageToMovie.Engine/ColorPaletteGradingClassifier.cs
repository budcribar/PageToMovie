using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Engine.ModelExecution;

namespace PageToMovie.Engine.ModelBacked;

public sealed record ColorGradingDirective(
    string FilmStock,
    string ColorPalette,
    string GradingPrompt);

/// <summary>
/// AI Classifier acting as a Master Colorist & Film Stock Director.
/// Assigns film stock emulsion characteristics (e.g. Kodak Vision3 500T, Fuji Eterna),
/// shadow/highlight color palettes, and color grading prompts per scene.
/// </summary>
public sealed class ColorPaletteGradingClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<ColorPaletteGradingClassifier> _log;

    public ColorPaletteGradingClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<ColorPaletteGradingClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyColorPaletteGradingWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert Master Colorist and Film Stock Director defining the color grading for a scene.

        Your task: Given a scene's setting, period style lock, and mood, define the color grading and film stock look.

        DIRECTIVES TO ASSIGN:
        1. film_stock: Emulsion and grain spec (e.g. "Kodak Vision3 500T 5219 film stock, subtle 35mm grain", "Fuji Eterna 500T desaturated stock", "Technicolor 3-strip vibrant emulsion").
        2. color_palette: Color palette balance (e.g. "Desaturated cool-teal shadow tones with warm amber candle highlights", "Monochromatic sepia tones with deep charcoal shadows").
        3. grading_prompt: Concise 10–20 word description of the look. Describe the look ONLY —
           do NOT prefix it with "Color grading:" or any other label. The caller supplies the
           label. (e.g. "Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights").

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "film_stock": "Kodak Vision3 500T 5219 film stock, subtle 35mm grain",
          "color_palette": "Desaturated cool-teal shadow tones with warm amber candle highlights",
          "grading_prompt": "Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights"
        }
        """;

    public async Task<ColorGradingDirective?> ClassifySceneColorGradingAsync(
        Dictionary<string, object?> scene,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled) return null;

        onProgress?.Invoke($"AI Master Colorist: Determining film stock & color palette for Scene {scene.GetValueOrDefault("scene_number")}…");

        try
        {
            var userPrompt = BuildUserPrompt(scene);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.ColorPaletteGradingClassifyModel;
            var pipeline = new ValidatedModelOperation<Stage2DirectiveInput, ColorGradingDirective>(
                new Stage2DirectiveOperation(_chat, "color_palette_grading", PromptVersion),
                new JsonColorDirectiveParser(), new ColorDirectiveValidator(),
                new DirectiveTerminalFallback<Stage2DirectiveInput, ColorGradingDirective>(), new ModelOperationOptions { CorrectiveMaxAttempts = 1 });
            var result = await pipeline.ExecuteAsync(new(SystemPrompt(), userPrompt, effectiveModel, ChatCallModes.ColorPaletteGradingClassify), ct).ConfigureAwait(false);
            return result.Value;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI color palette grading classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        if (scene.TryGetValue("render_style_lock", out var rsl))
            sb.AppendLine($"STYLE LOCK: {rsl}");

        ClassifierPromptParts.AppendSampleBeats(sb, scene);

        return sb.ToString();
    }

    private ColorGradingDirective? ParseColorResponse(string rawJson)
    {
        try
        {
            var cleaned = ClassifierJsonParser.StripFences(rawJson);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var stock = root.TryGetProperty("film_stock", out var fs) ? fs.GetString() ?? "" : "";
            var palette = root.TryGetProperty("color_palette", out var cp) ? cp.GetString() ?? "" : "";
            var prompt = root.TryGetProperty("grading_prompt", out var gp) ? gp.GetString() ?? "" : "";

            // Look only, no label — same contract the system prompt asks the model for. The tag
            // Stage 2 wraps this in is what names the field.
            if (string.IsNullOrWhiteSpace(prompt) && !string.IsNullOrWhiteSpace(stock))
                prompt = $"{stock}, {palette}".TrimEnd(',', ' ');

            return !string.IsNullOrWhiteSpace(stock) || !string.IsNullOrWhiteSpace(palette)
                ? new ColorGradingDirective(stock, palette, prompt)
                : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI color palette grading response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
