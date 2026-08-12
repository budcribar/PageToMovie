using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;
namespace PageToMovie.Engine.ModelExecution;

public sealed record DialogueNormalizationChange(
    int TokenIndex,
    string Original,
    string Normalized,
    string Rule);

public sealed record DialogueComparisonForm(
    string Original,
    IReadOnlyList<string> Tokens,
    IReadOnlyList<DialogueNormalizationChange> Changes);

public sealed record DialogueNormalizationOptions
{
    public bool NormalizeHistoricalForms { get; init; }
}

/// <summary>
/// Produces an auditable comparison copy. It never returns replacement dialogue for generation.
/// Historical lexical modernization is opt-in and is not used for source-fidelity validation.
/// </summary>
public static class DialogueComparisonNormalizer
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly IReadOnlyDictionary<string, string> HistoricalForms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["thou"] = "you", ["thee"] = "you", ["thy"] = "your", ["thine"] = "yours",
            ["art"] = "are", ["hast"] = "have", ["hath"] = "has", ["doth"] = "does",
            ["dost"] = "do", ["shalt"] = "shall", ["wilt"] = "will",
        };
    private static readonly IReadOnlyDictionary<string, string> RegionalSpellings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["colour"] = "color", ["colours"] = "colors",
            ["favourite"] = "favorite", ["favourites"] = "favorites",
            ["favour"] = "favor", ["favours"] = "favors",
            ["honour"] = "honor", ["honours"] = "honors",
            ["neighbour"] = "neighbor", ["neighbours"] = "neighbors",
            ["behaviour"] = "behavior", ["behaviours"] = "behaviors",
            ["theatre"] = "theater", ["centre"] = "center", ["metre"] = "meter",
            ["defence"] = "defense", ["offence"] = "offense", ["licence"] = "license",
            ["travelled"] = "traveled", ["travelling"] = "traveling",
            ["cancelled"] = "canceled", ["cancelling"] = "canceling",
            ["labelled"] = "labeled", ["labelling"] = "labeling",
            ["organise"] = "organize", ["organised"] = "organized", ["organising"] = "organizing",
            ["recognise"] = "recognize", ["recognised"] = "recognized", ["recognising"] = "recognizing",
        };

    public static DialogueComparisonForm Normalize(
        string? text,
        DialogueNormalizationOptions? options = null)
    {
        var original = text ?? "";
        options ??= new DialogueNormalizationOptions();
        var tokens = new List<string>();
        var changes = new List<DialogueNormalizationChange>();

        foreach (Match match in Words.Matches(original))
        {
            var raw = match.Value;
            var normalized = raw.Replace('’', '\'').ToLowerInvariant();
            var spelling = NormalizeRegionalSpelling(normalized);
            if (!string.Equals(normalized, spelling, StringComparison.Ordinal))
                changes.Add(new(tokens.Count, raw, spelling, "regional_spelling"));
            normalized = spelling;

            if (options.NormalizeHistoricalForms && HistoricalForms.TryGetValue(normalized, out var modern))
            {
                changes.Add(new(tokens.Count, raw, modern, "historical_comparison_form"));
                normalized = modern;
            }
            tokens.Add(normalized);
        }

        return new(original, tokens, changes);
    }

    public static IReadOnlyList<ModelValidationIssue> ValidateDialogueUnchanged(
        string original,
        string emitted)
    {
        if (string.Equals(original, emitted, StringComparison.Ordinal))
            return Array.Empty<ModelValidationIssue>();
        return
        [
            new ModelValidationIssue(
                "dialogue_mutated",
                "Generated dialogue differs from the immutable source dialogue.",
                "$.dialogue"),
        ];
    }

    private static string NormalizeRegionalSpelling(string value)
    {
        return RegionalSpellings.TryGetValue(value, out var normalized) ? normalized : value;
    }
}
