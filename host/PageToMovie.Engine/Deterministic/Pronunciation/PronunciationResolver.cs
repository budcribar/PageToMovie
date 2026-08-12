using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine.Deterministic.Pronunciation;

public sealed record PronunciationAnnotation(
    int Start,
    int Length,
    string Token,
    string SenseId,
    string Ipa,
    string Meaning,
    double Confidence,
    string Source);

public sealed record UnresolvedPronunciation(
    int Start,
    int Length,
    string Token,
    IReadOnlyList<string> CandidateSenseIds);

public sealed record PronunciationResolution(
    IReadOnlyList<PronunciationAnnotation> Annotations,
    IReadOnlyList<UnresolvedPronunciation> Unresolved,
    string LexiconVersion);

public sealed class PronunciationResolver
{
    private const string ResourceName = "PageToMovie.Pronunciation.heteronyms.en-US.json";
    private static readonly Regex TokenRegex = new(@"\b[\p{L}']+\b", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly HashSet<string> VerbCues = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "will", "would", "shall", "should", "can", "could", "may", "might",
        "must", "do", "does", "did", "please", "never"
    };
    private static readonly HashSet<string> NounCues = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "these", "those", "my", "your", "his",
        "her", "our", "their", "each", "every"
    };

    private readonly PronunciationLexicon _lexicon;
    private readonly Dictionary<string, PronunciationEntry> _entries;

    public static PronunciationResolver Default { get; } = LoadEmbedded();
    public string LexiconVersion => _lexicon.Version;

    public PronunciationResolver(PronunciationLexicon lexicon)
    {
        _lexicon = lexicon;
        _entries = lexicon.Entries.ToDictionary(entry => entry.Word, StringComparer.OrdinalIgnoreCase);
    }

    public PronunciationResolution Resolve(string? dialogue, string? sceneContext = null)
    {
        if (string.IsNullOrWhiteSpace(dialogue))
            return new([], [], LexiconVersion);

        var matches = TokenRegex.Matches(dialogue);
        var tokens = matches.Select(match => match.Value).ToArray();
        var context = $"{dialogue} {sceneContext}".ToLowerInvariant();
        var annotations = new List<PronunciationAnnotation>();
        var unresolved = new List<UnresolvedPronunciation>();

        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!_entries.TryGetValue(match.Value, out var entry) || entry.Senses.Count < 2)
                continue;

            var inferredPart = InferPartOfSpeech(tokens, index);
            var scored = entry.Senses
                .Select(sense => (Sense: sense, Score: Score(sense, context, inferredPart)))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Sense.Id, StringComparer.Ordinal)
                .ToArray();
            var best = scored[0];
            var second = scored[1];

            if (best.Score <= 0 || best.Score - second.Score < 2)
            {
                unresolved.Add(new UnresolvedPronunciation(
                    match.Index,
                    match.Length,
                    match.Value,
                    scored.Select(candidate => candidate.Sense.Id).ToArray()));
                continue;
            }

            var confidence = Math.Clamp(.55 + .08 * best.Score + .05 * (best.Score - second.Score), .55, .99);
            annotations.Add(new PronunciationAnnotation(
                match.Index,
                match.Length,
                match.Value,
                best.Sense.Id,
                best.Sense.Ipa,
                best.Sense.Label,
                Math.Round(confidence, 2),
                "deterministic"));
        }

        return new(annotations, unresolved, LexiconVersion);
    }

    public static string RenderPromptHints(PronunciationResolution resolution)
    {
        if (resolution.Annotations.Count == 0) return "";
        return string.Join("; ", resolution.Annotations.Select(annotation =>
            $"Pronounce '{annotation.Token}' as /{annotation.Ipa}/ ({annotation.Meaning})"));
    }

    private static readonly Regex HintTargetRegex = new(@"'([\p{L}][\p{L}']*)'", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// True when a pre-baked pronunciation hint is relevant to a spoken line — i.e. a word the hint
    /// targets (quoted like 'word') actually appears in the dialogue. A hint on a silent / no-dialogue
    /// beat, or for a word not in the line, only adds noise and should be dropped. When the hint names
    /// no quoted target word, relevance falls back to "there is dialogue".
    /// </summary>
    public static bool HintAppliesToDialogue(string? hint, string? dialogue)
    {
        if (string.IsNullOrWhiteSpace(hint) || string.IsNullOrWhiteSpace(dialogue))
            return false;

        var targets = HintTargetRegex.Matches(hint)
            .Select(m => m.Groups[1].Value)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();
        if (targets.Count == 0)
            return true; // no identifiable target word, but there is dialogue

        return targets.Any(w =>
            CommonRegex.IsMatch(dialogue, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase));
    }

    private static int Score(PronunciationSense sense, string context, string? inferredPart)
    {
        var score = 0;
        if (inferredPart is not null && sense.PartsOfSpeech.Contains(inferredPart, StringComparer.OrdinalIgnoreCase))
            score += 4;
        foreach (var cue in sense.Cues.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (CommonRegex.IsMatch(context, $@"\b{Regex.Escape(cue)}\b", RegexOptions.IgnoreCase))
                score += 2;
        }
        return score;
    }

    private static string? InferPartOfSpeech(IReadOnlyList<string> tokens, int index)
    {
        var previous = index > 0 ? tokens[index - 1] : "";
        if (VerbCues.Contains(previous)) return "verb";
        if (NounCues.Contains(previous)) return "noun";
        return null;
    }

    private static PronunciationResolver LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Missing embedded pronunciation lexicon: {ResourceName}");
        var lexicon = JsonSerializer.Deserialize<PronunciationLexicon>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("Pronunciation lexicon could not be parsed.");
        return new PronunciationResolver(lexicon);
    }
}

public sealed record PronunciationLexicon(
    string Version,
    string Language,
    IReadOnlyList<PronunciationEntry> Entries);

public sealed record PronunciationEntry(
    string Word,
    IReadOnlyList<PronunciationSense> Senses);

public sealed record PronunciationSense(
    string Id,
    string Ipa,
    string Label,
    IReadOnlyList<string> PartsOfSpeech,
    IReadOnlyList<string> Cues);
