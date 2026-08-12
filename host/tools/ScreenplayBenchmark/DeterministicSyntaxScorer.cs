using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PageToMovie.Engine;
using PageToMovie.Fountain;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;

using PageToMovie.Core.Utils;
namespace ScreenplayBenchmark;

public sealed class DeterministicSyntaxResult
{
    public double OverallSyntaxScore { get; set; } // 0 - 100
    public double FormatComplianceScore { get; set; } // 0 - 100
    public double SceneBudgetScore { get; set; } // 0 - 100
    public double DialoguePacingScore { get; set; } // 0 - 100
    public double CharacterDisambiguationScore { get; set; } // 0 - 100
    public double MusicSpecScore { get; set; } // 0 - 100

    public int TotalSceneHeadings { get; set; }
    public int TotalDialogueBlocks { get; set; }
    public double AvgWordsPerDialogue { get; set; }
    public int MaxWordsInSingleDialogue { get; set; }
    public int LongMonologueCount { get; set; }
    public int GenericNumberedSpeakerCount { get; set; }
    public List<string> CharacterNamesFound { get; set; } = new();
    public List<string> AgeDisambiguatedCharacters { get; set; } = new();
    public List<string> DiagnosticWarnings { get; set; } = new();
}

public static class DeterministicSyntaxScorer
{
    private static readonly Regex AgeQualifierRegex = new(@"\b(YOUNG|OLD|ADULT|BOY|GIRL|CHILD|ELDER|TINY|BABY|SENIOR|TEEN|TEENAGER)\b|\bAGE\s*\d+\b|\b\d{1,2}s\b|\(\d{1,2}\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex GenericMusicPlaceholderRegex = new(@"\b(some music|background music|music plays|play music|generic music|music sound)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex TitlePageKeyRegex = new(@"^(Title|Credit|Author|Authors|Source|Contact|Notes)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    public static DeterministicSyntaxResult Evaluate(string fountainText, List<string>? musicBedPrompts = null)
    {
        var result = new DeterministicSyntaxResult();
        if (string.IsNullOrWhiteSpace(fountainText))
        {
            result.DiagnosticWarnings.Add("Screenplay text is empty or null.");
            return result;
        }

        var parseResult = FountainParser.Parse(fountainText);
        var elements = parseResult.Elements;

        // 1. Format Compliance Score
        double formatScore = 100.0;
        var trimmedStart = fountainText.TrimStart();

        // Title Page Audit
        bool hasTitleHeader = CommonRegex.IsMatch(fountainText, @"^(Title|Title:|Credit:|Author:|Draft date:)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        bool hasFadeIn = trimmedStart.StartsWith("FADE IN:", StringComparison.OrdinalIgnoreCase);

        if (!hasFadeIn && !hasTitleHeader)
        {
            formatScore -= 10.0;
            result.DiagnosticWarnings.Add("Missing 'FADE IN:' starting transition or Fountain Title Page header.");
        }
        else if (hasTitleHeader)
        {
            // Title Page present
        }

        // Markdown Pollution Audit
        if (CommonRegex.IsMatch(fountainText, @"^\s*#{1,3}\s+", RegexOptions.Multiline))
        {
            formatScore -= 15.0;
            result.DiagnosticWarnings.Add("Markdown header syntax (# Scene) detected instead of clean Fountain headings.");
        }
        if (CommonRegex.IsMatch(fountainText, @"\*\*(INT\.|EXT\.)", RegexOptions.IgnoreCase))
        {
            formatScore -= 10.0;
            result.DiagnosticWarnings.Add("Bolded markdown scene headings (**INT...**) detected.");
        }

        // Script Colon Character Pattern Audit (e.g. Buster: "Hello" instead of Fountain CHARACTER
        // block, or a literal "Action: " label prefix — neither is valid Fountain). Excludes
        // standard Fountain title-page keys (Title:, Credit:, Author:, Source:, Notes:, ...) —
        // those are correct, required syntax, not a colon-dialogue formatting mistake. Confirmed
        // via real benchmark runs: every model's title page was previously being miscounted here.
        var colonCharacterMatches = CommonRegex.Matches(fountainText, @"^([A-Z][a-z0-9_]{1,15}):\s*\S+", RegexOptions.Multiline)
            .Where(m => !TitlePageKeyRegex.IsMatch(m.Groups[1].Value))
            .ToList();
        if (colonCharacterMatches.Count > 2)
        {
            formatScore -= 15.0;
            result.DiagnosticWarnings.Add($"Non-Fountain colon dialogue format ({colonCharacterMatches.Count} instances like 'Character:') detected.");
        }

        if (fountainText.Contains("[Page ", StringComparison.OrdinalIgnoreCase))
        {
            formatScore -= 15.0;
            result.DiagnosticWarnings.Add("Unstripped book page tags ([Page X]) detected in screenplay.");
        }

        if (fountainText.Contains("/*") && !fountainText.Contains("*/"))
        {
            formatScore -= 25.0;
            result.DiagnosticWarnings.Add("Unclosed boneyard comment block (/*) detected.");
        }

        var sceneHeadings = elements.Where(e => e.Type == FountainParser.ElementType.SceneHeading).ToList();
        result.TotalSceneHeadings = sceneHeadings.Count;
        if (result.TotalSceneHeadings == 0)
        {
            formatScore -= 50.0;
            result.DiagnosticWarnings.Add("No valid INT./EXT. scene headings detected by FountainParser.");
        }

        // Vague location language
        var vagueLocations = AdaptationFountain.FindVagueLocationHeadings(fountainText);
        if (vagueLocations.Count > 0)
        {
            formatScore -= Math.Min(20.0, vagueLocations.Count * 5.0);
            result.DiagnosticWarnings.Add($"Vague location heading(s) found: {string.Join("; ", vagueLocations.Take(3))}");
        }

        // Closing Transition Audit. Use the parsed Fountain elements rather than a raw-text
        // regex: Fountain permits forced transitions (>FADE OUT.) and centered end cards
        // (>THE END<), both of which are valid endings.
        var lastElement = elements.LastOrDefault();
        bool hasClosingTransition = lastElement is not null &&
            ((lastElement.Type == FountainParser.ElementType.Transition &&
              CommonRegex.IsMatch(lastElement.Text, @"^FADE\s+OUT\.?$", RegexOptions.IgnoreCase)) ||
             (lastElement.Type == FountainParser.ElementType.Centered &&
              CommonRegex.IsMatch(lastElement.Text, @"^THE\s+END$", RegexOptions.IgnoreCase)) ||
             CommonRegex.IsMatch(lastElement.Text, @"^THE\s+END$", RegexOptions.IgnoreCase));
        if (!hasClosingTransition)
        {
            formatScore -= 5.0;
            result.DiagnosticWarnings.Add("Missing 'FADE OUT.' or 'THE END' closing transition.");
        }

        result.FormatComplianceScore = Math.Max(0.0, Math.Min(100.0, formatScore));

        // 2. Scene Budget & Granularity Score
        // Soft target: 15 - 30 scenes for a standard adaptation.
        double budgetScore = 100.0;
        if (result.TotalSceneHeadings < 5)
        {
            budgetScore -= 40.0;
            result.DiagnosticWarnings.Add($"Too few scene headings ({result.TotalSceneHeadings}); story lacks visual scene progression.");
        }
        else if (result.TotalSceneHeadings > 45)
        {
            budgetScore -= Math.Min(50.0, (result.TotalSceneHeadings - 45) * 2.0);
            result.DiagnosticWarnings.Add($"Excessive scene count ({result.TotalSceneHeadings} scenes); high micro-scene density inflates video gen budget.");
        }
        result.SceneBudgetScore = Math.Max(0.0, budgetScore);

        // 3. Dialogue Pacing & Word Count Bounds
        var dialogueBlocks = elements.Where(e => e.Type == FountainParser.ElementType.Dialogue).ToList();
        result.TotalDialogueBlocks = dialogueBlocks.Count;
        if (dialogueBlocks.Count > 0)
        {
            var wordCounts = dialogueBlocks.Select(d => d.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length).ToList();
            result.AvgWordsPerDialogue = Math.Round(wordCounts.Average(), 1);
            result.MaxWordsInSingleDialogue = wordCounts.Max();
            result.LongMonologueCount = wordCounts.Count(w => w > 35);

            double pacingScore = 100.0;
            if (result.AvgWordsPerDialogue > 30.0)
            {
                pacingScore -= 20.0;
                result.DiagnosticWarnings.Add($"High average dialogue length ({result.AvgWordsPerDialogue} words/turn); speech beats risk clip overrun.");
            }

            if (result.LongMonologueCount > 0)
            {
                pacingScore -= Math.Min(30.0, result.LongMonologueCount * 5.0);
                result.DiagnosticWarnings.Add($"{result.LongMonologueCount} monologue turn(s) exceed 35 words without action line splits.");
            }
            result.DialoguePacingScore = Math.Max(0.0, pacingScore);
        }
        else
        {
            result.DialoguePacingScore = 80.0; // Text-only / silent scene screenplay
        }

        // 4. Character Age Disambiguation Score
        var charElements = elements.Where(e => e.Type == FountainParser.ElementType.Character).ToList();
        var charNames = charElements.Select(c => c.Text.Trim().Split('(')[0].Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.CharacterNamesFound = charNames;

        int genericCount = 0;
        int ageDisambiguatedCount = 0;
        foreach (var cName in charNames)
        {
            if (AdaptationFountain.IsGenericNumberedSpeaker(cName))
            {
                genericCount++;
            }

            if (AgeQualifierRegex.IsMatch(cName))
            {
                ageDisambiguatedCount++;
                result.AgeDisambiguatedCharacters.Add(cName);
            }
        }

        result.GenericNumberedSpeakerCount = genericCount;
        double charScore = 100.0;
        if (genericCount > 0)
        {
            charScore -= Math.Min(40.0, genericCount * 10.0);
            result.DiagnosticWarnings.Add($"{genericCount} generic numbered speaker(s) found (e.g. MAN 1, OFFICER 2); replace with proper names.");
        }

        if (ageDisambiguatedCount > 0)
        {
            // Bonus / full score for explicit age-qualification in character headers
            result.DiagnosticWarnings.Add($"Detected {ageDisambiguatedCount} age-qualified character header(s) (e.g. {string.Join(", ", result.AgeDisambiguatedCharacters.Take(3))}).");
        }
        result.CharacterDisambiguationScore = Math.Max(0.0, charScore);

        // 5. Music Specification Audit Score
        double musicScore = 100.0;
        var musicRegex = new Regex(@"\[\[(MUSIC|SOUND):\s*(.*?)\]\]|\((MUSIC|SOUND):\s*(.*?)\)|\b(MUSIC|SOUND):\s*(.*)", RegexOptions.IgnoreCase, CommonRegex.Timeout);
        var matches = musicRegex.Matches(fountainText);

        var instrumentalRegex = new Regex(@"\b(instrumental|no vocals|piano|orchestral|strings|acoustic|synth|percussion|ambient|melancholic|tempo|score|soundtrack|waltz|lullaby|cello|violin|horn|drums)\b", RegexOptions.IgnoreCase, CommonRegex.Timeout);

        if (matches.Count > 0)
        {
            int genericMusicCount = 0;
            int instrumentalCount = 0;

            foreach (Match m in matches)
            {
                var txt = m.Value;
                if (GenericMusicPlaceholderRegex.IsMatch(txt))
                    genericMusicCount++;
                if (instrumentalRegex.IsMatch(txt))
                    instrumentalCount++;
            }

            if (genericMusicCount > 0)
            {
                musicScore -= Math.Min(30.0, genericMusicCount * 10.0);
                result.DiagnosticWarnings.Add($"{genericMusicCount} generic music placeholder cue(s) found.");
            }

            if (instrumentalCount > 0)
            {
                result.DiagnosticWarnings.Add($"Detected {instrumentalCount} descriptive instrumental music/sound cue(s).");
            }
        }
        else
        {
            // Check if action lines mention sound/music atmosphere
            var atmosphericMatches = instrumentalRegex.Matches(fountainText);
            if (atmosphericMatches.Count >= 3)
            {
                musicScore = 95.0; // Good atmospheric sound/music description in action lines
            }
            else if (atmosphericMatches.Count > 0)
            {
                musicScore = 80.0;
            }
            else
            {
                musicScore = 70.0; // Minimal or missing music/sound specification
                result.DiagnosticWarnings.Add("No explicit music/sound design cues detected in screenplay.");
            }
        }

        result.MusicSpecScore = Math.Max(0.0, musicScore);

        // Composite C# Syntax Score. DialoguePacingScore is deliberately low-weight: it measures
        // raw pre-split dialogue length, but DialoguePacingSplitter already exists downstream to
        // split long turns into properly-paced clip beats at generation time — a long monologue
        // turn in the screenplay isn't the hard blocker this dimension used to treat it as.
        result.OverallSyntaxScore = Math.Round(
            (result.FormatComplianceScore * 0.30) +
            (result.SceneBudgetScore * 0.25) +
            (result.DialoguePacingScore * 0.05) +
            (result.CharacterDisambiguationScore * 0.25) +
            (result.MusicSpecScore * 0.15), 1);

        return result;
    }
}
