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
    /// Cut the grade / film-stock phrase a model attached despite the prompt, and no more than
    /// that. Splits on the clauses the model already wrote, and inside a clause cuts back only to
    /// the connector that introduced the phrase, so "...hard shadows on Kodak film stock" keeps
    /// its shadows. Deliberately not a regex: the greedy form guessed how far back the phrase
    /// reached and once left nothing but "Warm golden".
    /// Returns null when nothing survives — no lighting directive beats a mangled fragment.
    /// </summary>
    public static string? SanitizeLightingToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return token;
        var kept = token
            .Split(ClauseSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TrimGradeOrStockPhrase)
            .Select(clause => clause.Trim(' ', ',', ';', '-', '.'))
            .Where(clause => clause.Length > 0)
            .ToList();
        return kept.Count == 0 ? null : string.Join(", ", kept) + ".";
    }

    private static readonly char[] ClauseSeparators = [',', ';'];

    /// <summary>
    /// Grade owns emulsion, palette, and stock. Phrase-level, not a stock-name list: these are the
    /// handoff words, and naming one makes the rest of that phrase Grade's to write.
    /// </summary>
    private static readonly string[] GradeOrStockPhrases =
        ["color grad", "colour grad", "film stock", "emulsion"];

    /// <summary>Words that introduce the offending phrase and are cut along with it.</summary>
    private static readonly string[] PhraseConnectors =
        ["with", "in", "on", "using", "shot", "captured", "filmed", "graded", "and", "plus", "a", "the"];

    /// <summary>
    /// Everything from the word that introduces the phrase ("...<b>on</b> Kodak film stock") to the
    /// end of the clause is Grade's. Walking back is bounded by the clause, and a phrase with no
    /// introducing word — the clause is about the stock — takes the clause with it.
    /// </summary>
    private static string TrimGradeOrStockPhrase(string clause)
    {
        var hit = GradeOrStockPhrases
            .Select(phrase => clause.IndexOf(phrase, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (hit < 0)
            return clause;

        var cut = hit;
        while (true)
        {
            var previous = PreviousWordStart(clause, cut);
            if (previous < 0)
                return "";
            var word = clause[previous..cut].Trim();
            cut = previous;
            if (PhraseConnectors.Contains(word, StringComparer.OrdinalIgnoreCase))
                break;
        }
        var kept = clause[..cut].Trim();
        // "Shot on Kodak film stock" keeps nothing but its lead-in — that is not lighting either.
        return kept.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => PhraseConnectors.Contains(w.Trim('.', ','), StringComparer.OrdinalIgnoreCase))
            ? ""
            : kept;
    }

    /// <summary>Start index of the word ending at <paramref name="before"/>, or -1 at the front.</summary>
    private static int PreviousWordStart(string clause, int before)
    {
        var end = before;
        while (end > 0 && char.IsWhiteSpace(clause[end - 1]))
            end--;
        if (end <= 0)
            return -1;
        var start = end;
        while (start > 0 && !char.IsWhiteSpace(clause[start - 1]))
            start--;
        return start;
    }

}
