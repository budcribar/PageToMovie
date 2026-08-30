using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Shared clause handling for the tag writers that take one kind of directive out of prose the
/// model wrote — Camera lifting framing out of Action, Performance lifting gaze out of it.
/// <para>The rule they share: a directive is removed only when it stands as its own clause. A
/// phrase welded into a sentence is load-bearing grammar, and cutting it leaves wreckage —
/// "steps into a of the letter", "Nick and lifts the lantern". Reading a directive twice costs
/// less than a beat the model cannot read at all.</para>
/// </summary>
public static class ProseClauses
{
    /// <summary>A clause and the punctuation that ended it, so kept clauses rejoin as written.</summary>
    public readonly record struct Clause(string Text, string Separator);

    private static readonly char[] Enders = ['.', ',', ';', ':', '!', '?'];
    private static readonly char[] WordSeparators = [' ', '\t'];
    private static readonly char[] WordTrim = ['.', ',', ';', ':', '-', '(', ')'];

    /// <summary>Words that carry nothing on their own, so a clause of only these says nothing.</summary>
    public static readonly string[] JoiningWords =
        ["a", "an", "the", "and", "or", "with", "in", "on", "at", "of", "to", "into", "from",
         "is", "are", "was", "were", "then", "there", "here"];

    /// <summary>Split on clause and sentence ends, keeping each separator with its clause.</summary>
    public static List<Clause> Split(string text)
    {
        var clauses = new List<Clause>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (Array.IndexOf(Enders, text[i]) < 0)
            {
                i++;
                continue;
            }

            var stop = i;
            while (stop + 1 < text.Length && Array.IndexOf(Enders, text[stop + 1]) >= 0)
                stop++;
            clauses.Add(new Clause(text[start..i], text[i..(stop + 1)] + " "));
            start = stop + 1;
            i = start;
        }

        if (start < text.Length)
            clauses.Add(new Clause(text[start..], ""));
        return clauses;
    }

    /// <summary>
    /// Drop the clauses that are nothing but <paramref name="directive"/> matches and the words
    /// joining them; leave every other clause exactly as written.
    /// </summary>
    public static string DropClausesOnlyMatching(
        string? text,
        Regex directive,
        IEnumerable<string>? extraJoiningWords = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var joining = extraJoiningWords is null
            ? JoiningWords
            : JoiningWords.Concat(extraJoiningWords).ToArray();
        var kept = Split(text)
            .Where(clause => !IsOnly(clause.Text, directive, joining))
            .Select(clause => clause.Text.Trim() + clause.Separator);
        return Tidy(string.Concat(kept));
    }

    /// <summary>True when removing every <paramref name="directive"/> match leaves only joining words.</summary>
    public static bool IsOnly(string clause, Regex directive, IReadOnlyCollection<string> joining)
    {
        if (!directive.IsMatch(clause))
            return false;
        return directive.Replace(clause, " ")
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .All(word => joining.Contains(word.Trim(WordTrim), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Close the gaps a dropped clause leaves: doubled punctuation and loose spacing.</summary>
    public static string Tidy(string text)
    {
        var t = CommonRegex.WhitespaceCollapse.Replace(text, " ");
        t = CommonRegex.Replace(t, @"\s*([,;])(?:\s*[,;])+", "$1");
        t = CommonRegex.Replace(t, @"\s+([,;.])", "$1");
        t = CommonRegex.DotCollapse.Replace(t, ".");
        return t.Trim(' ', ',', ';', '.', '-', ':');
    }
}
