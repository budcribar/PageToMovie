using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Adaptation;

/// <summary>
/// Pure text metrics used by adaptation density and (via Engine wrappers) clip duration.
/// Kept free of Engine so Adaptation never depends on Stage‑2 / model catalog code.
/// </summary>
public static class TextMetrics
{
    /// <summary>Words per second for spoken dialogue (~156 wpm — natural narration pace).</summary>
    /// <remarks>Matches <c>ClipDurationEstimator.DialogueWordsPerSecond</c> in Engine.</remarks>
    public const double DialogueWordsPerSecond = 2.6;

    private static readonly Regex WordCountRegex = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled, CommonRegex.Timeout);

    public static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return WordCountRegex.Matches(text).Count;
    }

    public static int CountSyllables(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var total = 0;
        var len = text.Length;
        var wordLen = 0;
        var syllables = 0;
        var inVowelGroup = false;

        for (var i = 0; i < len; i++)
        {
            var ch = text[i];
            if (IsSyllableWordChar(ch))
            {
                wordLen++;
                NoteVowelGroup(ch, ref syllables, ref inVowelGroup);
            }
            else if (wordLen > 0)
            {
                total += SyllablesForWord(wordLen, syllables);
                wordLen = 0;
                syllables = 0;
                inVowelGroup = false;
            }
        }

        if (wordLen > 0)
            total += SyllablesForWord(wordLen, syllables);

        return total;
    }

    private static bool IsSyllableWordChar(char ch) =>
        char.IsLetter(ch) || char.IsDigit(ch) || ch == '\'';

    private static bool IsEnglishVowel(char ch)
    {
        var lower = char.ToLowerInvariant(ch);
        return lower is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
    }

    private static void NoteVowelGroup(char ch, ref int syllables, ref bool inVowelGroup)
    {
        var isV = IsEnglishVowel(ch);
        if (isV && !inVowelGroup)
        {
            syllables++;
            inVowelGroup = true;
        }
        else if (!isV)
        {
            inVowelGroup = false;
        }
    }

    private static int SyllablesForWord(int wordLen, int syllables) =>
        wordLen <= 3 ? 1 : Math.Max(1, syllables);
}
