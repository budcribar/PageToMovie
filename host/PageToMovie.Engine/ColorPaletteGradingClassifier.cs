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
/// Emulsion / palette SSoT. Does not own light sources, shadows, or volume —
/// that is <see cref="CinematicLightingClassifier"/>.
/// </summary>
public sealed class ColorPaletteGradingClassifier
{
    public const string PromptVersion = "v2_grade_only";

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
        You are an expert Master Colorist and Film Stock Director. You own emulsion and palette only.

        Your task: Given a scene's setting, period style lock, and mood, define the film-stock look and color palette.

        DIRECTIVES TO ASSIGN:
        1. film_stock: Emulsion and grain spec (e.g. "Kodak Vision3 500T 5219 film stock, subtle 35mm grain", "Fuji Eterna 500T desaturated stock", "Technicolor 3-strip vibrant emulsion").
        2. color_palette: Color palette balance (e.g. "Desaturated cool-teal shadow tones with warm amber candle highlights", "Monochromatic sepia tones with deep charcoal shadows").
        3. grading_prompt: Concise 10–20 word description of the emulsion/palette look ONLY —
           do NOT prefix it with "Color grading:" or any other label. The caller supplies the
           <Grade> tag. (e.g. "Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights").

        Do NOT describe light sources, shadows, volumetric effects, or time-of-day lighting — Lighting owns those.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "film_stock": "Kodak Vision3 500T 5219 film stock, subtle 35mm grain",
          "color_palette": "Desaturated cool-teal shadow tones with warm amber candle highlights",
          "grading_prompt": "Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights"
        }
        """;

    /// <summary>
    /// Strip leftover prose labels ("Color grading:", "Grade:") so <c>&lt;Grade&gt;</c> is the only name.
    /// </summary>
    public static string StripGradeLabel(string? gradingPrompt)
    {
        var grade = (gradingPrompt ?? "").Trim();
        if (grade.Length == 0)
            return "";
        var label = GradeProseLabels.FirstOrDefault(l => grade.StartsWith(l, StringComparison.OrdinalIgnoreCase));
        return label is null ? grade : grade[label.Length..].Trim();
    }

    private static readonly string[] GradeProseLabels = ["Color grading:", "Colour grading:", "Grade:"];

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
            var color = result.Value;
            if (color is null)
                return null;
            var look = StripGradeLabel(color.GradingPrompt);
            if (string.IsNullOrWhiteSpace(look) && !string.IsNullOrWhiteSpace(color.FilmStock))
                look = $"{color.FilmStock}, {color.ColorPalette}".TrimEnd(',', ' ');
            return color with { GradingPrompt = look };
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
}
