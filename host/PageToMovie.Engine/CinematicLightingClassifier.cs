using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Scene lighting SSoT: light sources, shadows, volume, time-of-day / mood.
/// Does not own emulsion, palette, or film stock — that is <see cref="ColorPaletteGradingClassifier"/>.
/// </summary>
public sealed class CinematicLightingClassifier
{
    public const string PromptVersion = "v2_light_only";

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
        You are an expert film cinematographer and lighting director specifying scene lighting.

        Your task: Given a scene's location, time of day, and emotional beats, generate a single concise cinematic lighting description (15–60 words) that locks light across all shots in the scene.

        RULES (HARD):
        1. Light only: key light SOURCES, shadow quality, volumetric effects (haze, dust, fog), and time-of-day / mood.
           - Example (Gothic/Night): "Chiaroscuro flickering candlelight with deep obsidian shadows and cool-gray volumetric fog."
           - Example (Warm/Day): "Warm golden-hour sunlight at low angle, high-contrast shadows."
           - Example (Interior/Night Standoff): "Single harsh shaft of moonlight cutting a pitch-black room, ultra-cool cobalt shadows."
        2. Do NOT say "color grade" or "color grading". Do NOT name film stock, emulsion, or a color palette. Grade owns those.
        3. Keep the scene's time-of-day and location mood intact.
        4. Do NOT include camera resolution, fps, or negative tags.

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
        var token = await ClassifierTextDirectiveRunner.ClassifyAsync(
            IsEnabled, onProgress,
            $"AI Cinematic Lighting: Analyzing lighting & mood for Scene {scene.GetValueOrDefault("scene_number")}…",
            _chat, _log, scene, SystemPrompt(),
            () => ClassifierPromptParts.BuildSceneUserPrompt(scene, "RENDER STYLE LOCK", includeSampleBeats: true),
            model, _opts.CinematicLightingClassifyModel, "cinematic_lighting", PromptVersion,
            "lighting_token", ChatCallModes.CinematicLightingClassify, "cinematic lighting", ct)
            .ConfigureAwait(false);
        return SanitizeLightingToken(token);
    }

    /// <summary>
    /// Drop leftover grade/stock clauses a model still attached despite the prompt.
    /// Lighting must not ship a second grade.
    /// </summary>
    public static string? SanitizeLightingToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return token;
        var t = token.Trim();
        t = StripGradeStockClauses(t);
        return string.IsNullOrWhiteSpace(t) ? null : t.Trim(' ', ',', ';', '-', '.');
    }

    private static string StripGradeStockClauses(string text)
    {
        // Clause-level only — do not grow a stock-name list. Grade owns emulsion names.
        var t = CommonRegex.Replace(
            text,
            @"(?:,|;|\s+)?(?:with\s+)?(?:a\s+)?(?:warm\s+|cool\s+|amber\s+)?(?:color|colour)\s+grad(?:e|ing)\b(?:\s+\w+){0,6}",
            "",
            RegexOptions.IgnoreCase);
        t = CommonRegex.Replace(
            t,
            @"(?:,|;|\s+)?(?:on\s+)?(?:[A-Za-z0-9]+\s+)*(?:film\s+stock|emulsion)\b",
            "",
            RegexOptions.IgnoreCase);
        return CommonRegex.Replace(t, @"\s{2,}", " ").Trim();
    }
}
