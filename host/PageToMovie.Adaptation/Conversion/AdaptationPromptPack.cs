using System.Reflection;
using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Loads the book → Fountain prompt pack. Prefers embedded resource in this assembly,
/// optional override via <c>PAGETOMOVIE_PROMPTS_DIR</c> (same env as Engine PromptFiles).
/// </summary>
public static class AdaptationPromptPack
{
    public const string BookToFountainRelativePath = "prompts/book_to_fountain.txt";
    public const string EmbeddedLogicalName = "PageToMovie.Adaptation.Prompts.book_to_fountain.txt";

    public const string ReskinRelativePath = "prompts/fountain_reskin.txt";
    public const string ReskinEmbeddedLogicalName = "PageToMovie.Adaptation.Prompts.fountain_reskin.txt";

    public const string EmbellishRelativePath = "prompts/embellish_scene.txt";
    public const string EmbellishEmbeddedLogicalName = "PageToMovie.Adaptation.Prompts.embellish_scene.txt";

    public const string TrimRelativePath = "prompts/trim_scene.txt";
    public const string TrimEmbeddedLogicalName = "PageToMovie.Adaptation.Prompts.trim_scene.txt";

    public const string BookToIndexRelativePath = "prompts/book_to_index.txt";
    public const string BookToIndexEmbeddedLogicalName = "PageToMovie.Adaptation.Prompts.book_to_index.txt";

    /// <summary>
    /// Injected when no artificial runtime target is set (product default).
    /// Natural = stage fully (max base for later Fit length); no fake padding.
    /// </summary>
    public const string UnlimitedRuntimeDirective =
        "unlimited / natural length — stage the whole story as a full draft the operator can " +
        "trim later; finish on the book's real ending; do NOT invent incidents or pad with " +
        "filler; do NOT collapse a long book into a short montage by summarizing major episodes";

    private static readonly Assembly ThisAssembly = typeof(AdaptationPromptPack).Assembly;
    private static readonly Regex TokenPattern = new(@"\{\{([A-Z0-9_]+)\}\}", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>Optional directory of loose prompt files (overrides embed).</summary>
    public static string? PromptsDirOverride { get; set; }

    public static async Task<string> LoadBookToFountainSystemPromptAsync(
        int? totalRuntimeMinutes = null,
        string? fallbackBody = null,
        CancellationToken ct = default,
        AdaptationPromptTokens? tokens = null)
    {
        ct.ThrowIfCancellationRequested();

        string body;
        try
        {
            body = await ReadBookToFountainBodyAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(fallbackBody))
        {
            body = fallbackBody;
        }

        tokens ??= AdaptationPromptTokens.Default(totalRuntimeMinutes);
        if (tokens.TotalRuntimeMinutes is null && totalRuntimeMinutes is not null)
            tokens = CloneWithRuntime(tokens, totalRuntimeMinutes);
        return ApplyPromptTokens(body, tokens);
    }

    /// <summary>Backward-compatible entry used by older call sites.</summary>
    public static string ApplyRuntimeTokens(string body, int? totalRuntimeMinutes) =>
        ApplyPromptTokens(body, AdaptationPromptTokens.Default(totalRuntimeMinutes));

    /// <summary>
    /// Replace every known token with a concrete value. Throws if any <c>{{TOKEN}}</c> remains
    /// so the model never sees unresolved placeholders.
    /// </summary>
    public static string ApplyPromptTokens(string body, AdaptationPromptTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        body ??= "";

        var rtm = tokens.TotalRuntimeMinutes;
        var unlimited = rtm is null or <= 0;
        var minutes = unlimited ? 0 : Math.Clamp(rtm.GetValueOrDefault(), 1, 180);

        var directive = unlimited
            ? UnlimitedRuntimeDirective
            : $"Target about {minutes} minutes of finished film. Keep the adaptation tight; do not pad beyond that budget.";

        var medium = string.IsNullOrWhiteSpace(tokens.VisualMedium) ? "auto" : tokens.VisualMedium.Trim();
        var mediumDirective = string.Equals(medium, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto — infer from the source (picture-book art vs photoreal vs live-action). " +
              "Write the chosen medium into VISION_META; do not invent a medium that fights the text."
            : $"{medium} — lock the adaptation and VISION_META to this medium; " +
              "do not switch to a conflicting style.";

        // Scene band: open guidance when not specified (preferred product default).
        string sceneMinText;
        string sceneMaxText;
        string sceneBandPhrase;
        if (tokens.SceneCountMin is int sMin && tokens.SceneCountMax is int sMax && sMin > 0 && sMax >= sMin)
        {
            sceneMinText = sMin.ToString();
            sceneMaxText = sMax.ToString();
            sceneBandPhrase = $"{sMin}–{sMax}";
        }
        else
        {
            sceneMinText = "as few as the story needs (no artificial floor)";
            sceneMaxText = "only as many as needed for the runtime target (no artificial ceiling)";
            sceneBandPhrase = "whatever the story and runtime require (no fixed scene-count band)";
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RUNTIME_TARGET_DIRECTIVE"] = directive,
            ["TOTAL_RUNTIME_MINUTES"] = unlimited
                ? "unlimited (natural length)"
                : minutes.ToString(),
            ["VISUAL_MEDIUM"] = mediumDirective,
            ["DRAFT_DATE"] = string.IsNullOrWhiteSpace(tokens.DraftDate)
                ? DateTime.UtcNow.ToString("M/d/yyyy")
                : tokens.DraftDate.Trim(),
            ["MAX_DIALOGUE_WORDS"] = tokens.MaxDialogueWords.ToString(),
            ["MAX_SPEAKING_CAST"] = tokens.MaxSpeakingCast.ToString(),
            ["BODY_WORDS_PER_MINUTE"] = tokens.BodyWordsPerMinute.ToString(),
            ["MIN_AUDIO_CUES_PER_SCENE"] = tokens.MinAudioCuesPerScene.ToString(),
            ["MIN_AUDIO_CUES_AT_PEAK"] = tokens.MinAudioCuesAtPeak.ToString(),
            ["VO_MAX_SENTENCES"] = tokens.VoMaxSentences.ToString(),
            ["SCENE_COUNT_MIN"] = sceneMinText,
            ["SCENE_COUNT_MAX"] = sceneMaxText,
            // Used in checklist lines like "{{SCENE_COUNT_MIN}}–{{SCENE_COUNT_MAX}}"
            // after individual replaces; also support a combined token if templates add it.
            ["SCENE_COUNT_BAND"] = sceneBandPhrase,
        };

        // Legacy prose that assumed a pure numeric TOTAL_RUNTIME_MINUTES.
        if (unlimited)
        {
            body = CommonRegex.Replace(
                body,
                @"in roughly \{\{TOTAL_RUNTIME_MINUTES\}\} minutes\s+of finished film",
                "at natural length with no artificial minute target",
                RegexOptions.IgnoreCase);
            body = CommonRegex.Replace(
                body,
                @"Target about \{\{TOTAL_RUNTIME_MINUTES\}\} minutes of finished film\.?",
                "Runtime target: " + UnlimitedRuntimeDirective + ".",
                RegexOptions.IgnoreCase);
            body = CommonRegex.Replace(
                body,
                @"Target ~\{\{TOTAL_RUNTIME_MINUTES\}\} minutes of finished film\.?",
                "Runtime target: " + UnlimitedRuntimeDirective + ".",
                RegexOptions.IgnoreCase);
        }

        foreach (var (key, value) in map)
            body = body.Replace("{{" + key + "}}", value, StringComparison.Ordinal);

        ThrowIfUnresolvedTokens(
            body,
            "book_to_fountain prompt still has unresolved tokens after substitution",
            extraSuffix: ". Add them to AdaptationPromptTokens / ApplyPromptTokens, or remove them from the prompt.",
            sort: true);
        return body;
    }

    public static async Task<string> ReadBookToFountainBodyAsync(CancellationToken ct = default) =>
        await ReadPromptBodyAsync(BookToFountainRelativePath, EmbeddedLogicalName, ct).ConfigureAwait(false);

    /// <summary>
    /// Fountain → Fountain "re-skin" system prompt with the target medium resolved.
    /// Descriptive layer only; dialogue / cues / scene count preserved.
    /// </summary>
    public static async Task<string> BuildReskinSystemPromptAsync(string? visualMedium, CancellationToken ct = default) =>
        await BuildVisualMediumPromptAsync(
            ReskinRelativePath,
            ReskinEmbeddedLogicalName,
            visualMedium,
            autoDirective: "auto — keep the medium the source already implies; do not switch styles",
            leftoverPromptName: "fountain_reskin",
            ct).ConfigureAwait(false);

    /// <summary>
    /// Fountain → Fountain "embellish" system prompt with the target medium resolved.
    /// Enriches the descriptive layer only; dialogue / cues / scene count preserved.
    /// </summary>
    public static async Task<string> BuildEmbellishSystemPromptAsync(string? visualMedium, CancellationToken ct = default) =>
        await BuildVisualMediumPromptAsync(
            EmbellishRelativePath,
            EmbellishEmbeddedLogicalName,
            visualMedium,
            autoDirective: "auto — enrich in the medium the source already implies; do not switch styles",
            leftoverPromptName: "embellish_scene",
            ct).ConfigureAwait(false);

    /// <summary>
    /// Fountain → Fountain "trim" system prompt with the runtime targets resolved. Structure may shrink
    /// (condense/merge/cut) toward the target; never expands.
    /// </summary>
    public static async Task<string> BuildTrimSystemPromptAsync(int targetMinutes, int naturalMinutes, CancellationToken ct = default)
    {
        var body = await ReadPromptBodyAsync(TrimRelativePath, TrimEmbeddedLogicalName, ct).ConfigureAwait(false);
        body = body.Replace("{{TARGET_MINUTES}}", Math.Max(1, targetMinutes).ToString(), StringComparison.Ordinal);
        body = body.Replace("{{NATURAL_MINUTES}}", Math.Max(1, naturalMinutes).ToString(), StringComparison.Ordinal);
        ThrowIfUnresolvedTokens(body, "trim_scene prompt still has unresolved tokens");
        return body;
    }

    private static async Task<string> BuildVisualMediumPromptAsync(
        string relativePath,
        string logicalName,
        string? visualMedium,
        string autoDirective,
        string leftoverPromptName,
        CancellationToken ct)
    {
        var body = await ReadPromptBodyAsync(relativePath, logicalName, ct).ConfigureAwait(false);
        var medium = string.IsNullOrWhiteSpace(visualMedium) ? "auto" : visualMedium.Trim();
        var directive = string.Equals(medium, "auto", StringComparison.OrdinalIgnoreCase)
            ? autoDirective
            : medium;
        body = body.Replace("{{VISUAL_MEDIUM}}", directive, StringComparison.Ordinal);
        ThrowIfUnresolvedTokens(body, leftoverPromptName + " prompt still has unresolved tokens");
        return body;
    }

    private static void ThrowIfUnresolvedTokens(
        string body,
        string messagePrefix,
        string extraSuffix = ".",
        bool sort = false)
    {
        IEnumerable<string> tokens = TokenPattern.Matches(body)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);
        if (sort)
            tokens = tokens.OrderBy(s => s, StringComparer.Ordinal);
        var leftovers = tokens.ToList();
        if (leftovers.Count == 0)
            return;
        throw new InvalidOperationException(
            messagePrefix + ": " +
            string.Join(", ", leftovers.Select(t => "{{" + t + "}}")) +
            extraSuffix);
    }

    private static async Task<string> ReadPromptBodyAsync(string relativePath, string logicalName, CancellationToken ct = default)
    {
        var fromOverride = await TryReadOverrideFileAsync(relativePath, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(fromOverride))
            return fromOverride;

        using var stream = ThisAssembly.GetManifestResourceStream(logicalName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var available = string.Join(", ", ThisAssembly.GetManifestResourceNames()
            .Where(n => n.Contains("Prompt", StringComparison.OrdinalIgnoreCase)));
        throw new InvalidOperationException(
            $"Prompt not embedded: {relativePath}. " +
            $"Available: {(string.IsNullOrEmpty(available) ? "(none — rebuild Adaptation with prompts/)" : available)}. " +
            "Or set PAGETOMOVIE_PROMPTS_DIR to a folder with the .txt file.");
    }

    public static Task<string> LoadBookToIndexSystemPromptAsync(CancellationToken ct = default) =>
        ReadPromptBodyAsync(BookToIndexRelativePath, BookToIndexEmbeddedLogicalName, ct);

    private static AdaptationPromptTokens CloneWithRuntime(AdaptationPromptTokens t, int? minutes) =>
        new()
        {
            TotalRuntimeMinutes = minutes,
            VisualMedium = t.VisualMedium,
            DraftDate = t.DraftDate,
            MaxDialogueWords = t.MaxDialogueWords,
            MaxSpeakingCast = t.MaxSpeakingCast,
            BodyWordsPerMinute = t.BodyWordsPerMinute,
            MinAudioCuesPerScene = t.MinAudioCuesPerScene,
            MinAudioCuesAtPeak = t.MinAudioCuesAtPeak,
            VoMaxSentences = t.VoMaxSentences,
            SceneCountMin = t.SceneCountMin,
            SceneCountMax = t.SceneCountMax,
        };

    private static async Task<string?> TryReadOverrideFileAsync(string relativePath, CancellationToken ct = default)
    {
        var dir = PromptsDirOverride
                  ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_PROMPTS_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var full = Path.Combine(dir, Path.GetFileName(relativePath));
        return File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : null;
    }
}
