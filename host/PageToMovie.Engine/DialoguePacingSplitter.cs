using System.Text;
using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

public sealed record DialogueSpeechBeat(
    string DialogueText,
    int WordCount,
    double EstimatedDurationSeconds
);

public static class DialoguePacingSplitter
{
    private static readonly Regex TerminalPunctuationRegex = new(@"[.!?](?=\s|$)", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ProsodicPunctuationRegex = new(@"[—;:](?=\s|$)|—", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ConjunctionCommaRegex = new(@",\s+(?:and|but|or|so|yet|because|when|as|while|since)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, CommonRegex.Timeout);
    private static readonly Regex AnyCommaRegex = new(@",(?=\s)", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// Splits a dialogue turn into speech beats sized to fit the target video model's real clip-
    /// duration bounds, based on natural prosodic punctuation pauses.
    /// </summary>
    /// <param name="targetMaxSeconds">Soft cap — the splitter prefers to cut here. Defaults to
    /// <see cref="ClipDurationEstimator.MaxSeconds"/>; pass the resolved value from
    /// <see cref="ClipDurationEstimator.ResolveBoundsForModel"/> for the actual active video model
    /// instead of relying on the generic default, since providers' real limits vary (e.g. Grok's
    /// catalog entry doesn't override these, so it uses the generic bounds; another provider might).</param>
    /// <param name="hardMaxSeconds">Absolute ceiling — never exceeded, even by a single delimiter-free
    /// run-on clause. Defaults to <see cref="ClipDurationEstimator.AbsMaxSeconds"/>, same caveat as
    /// <paramref name="targetMaxSeconds"/>.</param>
    /// <param name="wordsPerSecond">Defaults to <see cref="ClipDurationEstimator.DialogueWordsPerSecond"/> —
    /// the same speech-rate constant the rest of the pipeline's duration estimates are built on, so
    /// this splitter's numbers are directly comparable to <see cref="ClipDurationEstimator"/>'s.</param>
    public static List<DialogueSpeechBeat> SplitDialogue(
        string dialogueText,
        int targetMaxSeconds = ClipDurationEstimator.MaxSeconds,
        int hardMaxSeconds = ClipDurationEstimator.AbsMaxSeconds,
        double wordsPerSecond = ClipDurationEstimator.DialogueWordsPerSecond)
    {
        if (string.IsNullOrWhiteSpace(dialogueText))
            return new List<DialogueSpeechBeat>();

        var targetMaxWords = WordsForSeconds(targetMaxSeconds, wordsPerSecond);
        var hardMaxWords = Math.Max(targetMaxWords, WordsForSeconds(hardMaxSeconds, wordsPerSecond));

        var cleaned = dialogueText.Trim();
        var words = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= targetMaxWords)
            return new List<DialogueSpeechBeat> { new DialogueSpeechBeat(cleaned, words.Length, EstimateSeconds(words.Length, wordsPerSecond)) };

        // Split into candidate clause chunks using natural punctuation hierarchy
        var rawChunks = SplitIntoClauseChunks(cleaned, hardMaxWords);

        // Merge tiny orphan chunks (< 4 words) with adjacent beats
        var mergedBeats = MergeOrphanChunks(rawChunks, targetMaxWords);

        // Convert to DialogueSpeechBeat DTOs
        var results = new List<DialogueSpeechBeat>();
        foreach (var chunk in mergedBeats)
        {
            var chunkWords = chunk.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (chunkWords > 0)
                results.Add(new DialogueSpeechBeat(chunk, chunkWords, EstimateSeconds(chunkWords, wordsPerSecond)));
        }

        return results;
    }

    /// <summary>Same head/tail-padded speech duration model as <see cref="ClipDurationEstimator"/>'s
    /// word-count formula, so a beat's estimate here means the same thing it would mean there.</summary>
    private static double EstimateSeconds(int wordCount, double wordsPerSecond) =>
        Math.Round(ClipDurationEstimator.SpeechHeadSeconds + wordCount / wordsPerSecond + ClipDurationEstimator.SpeechTailSeconds, 1);

    /// <summary>Inverse of <see cref="EstimateSeconds"/> — the most words that fit in
    /// <paramref name="seconds"/> once head/tail padding is subtracted. Always at least 1, so a
    /// caller passing an unreasonably small seconds bound still gets a usable (if aggressive)
    /// split instead of a division producing zero/negative words.</summary>
    private static int WordsForSeconds(int seconds, double wordsPerSecond) =>
        Math.Max(1, (int)Math.Floor((seconds - ClipDurationEstimator.SpeechHeadSeconds - ClipDurationEstimator.SpeechTailSeconds) * wordsPerSecond));

    private static List<string> SplitIntoClauseChunks(string text, int maxWords)
    {
        var currentWords = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (currentWords.Length <= maxWords)
            return new List<string> { text };

        // Try each punctuation tier in turn; a tier that doesn't actually split the text
        // (Count <= 1, i.e. no matches, or one trailing match with nothing after it) falls
        // through to the next. PackPieces recursively re-splits any individual piece that's
        // STILL over maxWords on its own — e.g. one very long clause between two semicolons —
        // instead of letting it through unbounded (which the single-shot version of each tier
        // used to do).
        foreach (var regex in PunctuationTiers)
        {
            var pieces = SplitByRegexMatches(text, regex);
            if (pieces.Count > 1)
                return PackPieces(pieces, maxWords);
        }

        // No punctuation delimiter of any kind was found — the previous "hard fallback" here
        // just returned the whole text as one unsplit chunk despite being named a "word window
        // split"; this now actually is one, so maxWords is honored even for a long run-on
        // clause with no internal punctuation at all.
        return PackByWordWindow(text, maxWords);
    }

    private static readonly Regex[] PunctuationTiers =
    {
        TerminalPunctuationRegex, ProsodicPunctuationRegex, ConjunctionCommaRegex, AnyCommaRegex,
    };

    /// <summary>Greedily combines consecutive delimited pieces up to maxWords per chunk. A single
    /// piece that's already over maxWords on its own gets recursively re-split (falls through to
    /// the next punctuation tier, or ultimately <see cref="PackByWordWindow"/>) rather than being
    /// emitted whole.</summary>
    private static List<string> PackPieces(List<string> pieces, int maxWords)
    {
        var chunks = new List<string>();
        var accum = "";
        foreach (var piece in pieces)
        {
            var pieceWords = piece.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (pieceWords > maxWords)
            {
                if (!string.IsNullOrEmpty(accum)) { chunks.Add(accum); accum = ""; }
                chunks.AddRange(SplitIntoClauseChunks(piece, maxWords));
                continue;
            }

            var combined = string.IsNullOrEmpty(accum) ? piece : accum + " " + piece;
            var combinedWords = combined.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (combinedWords <= maxWords)
            {
                accum = combined;
            }
            else
            {
                chunks.Add(accum);
                accum = piece;
            }
        }
        if (!string.IsNullOrEmpty(accum))
            chunks.Add(accum);

        return chunks;
    }

    /// <summary>Last-resort split for text with no usable punctuation anywhere — fixed-size word
    /// windows so maxWords is still a real ceiling, not just an aspiration.</summary>
    private static List<string> PackByWordWindow(string text, int maxWords)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        for (var i = 0; i < words.Length; i += maxWords)
            chunks.Add(string.Join(' ', words.Skip(i).Take(maxWords)));
        return chunks;
    }

    private static List<string> SplitByRegexMatches(string text, Regex regex)
    {
        var matches = regex.Matches(text);
        if (matches.Count == 0)
            return new List<string> { text };

        var parts = new List<string>();
        int lastIndex = 0;
        foreach (Match m in matches)
        {
            int splitEnd = m.Index + m.Length;
            string part = text.Substring(lastIndex, splitEnd - lastIndex).Trim();
            if (!string.IsNullOrWhiteSpace(part))
                parts.Add(part);
            lastIndex = splitEnd;
        }

        if (lastIndex < text.Length)
        {
            string remainder = text.Substring(lastIndex).Trim();
            if (!string.IsNullOrWhiteSpace(remainder))
                parts.Add(remainder);
        }

        return parts;
    }

    private static List<string> MergeOrphanChunks(List<string> rawChunks, int maxWords)
    {
        if (rawChunks.Count <= 1)
            return rawChunks;

        var merged = new List<string>();
        var current = new StringBuilder();

        foreach (var chunk in rawChunks)
        {
            if (current.Length == 0)
            {
                current.Append(chunk);
                continue;
            }

            var chunkWords = chunk.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var currentWords = current.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            // If chunk is an orphan (< 4 words) or combining them stays under maxWords
            if (chunkWords < 4 || (currentWords + chunkWords) <= maxWords)
            {
                current.Append(' ').Append(chunk);
            }
            else
            {
                merged.Add(current.ToString());
                current.Clear();
                current.Append(chunk);
            }
        }

        if (current.Length > 0)
        {
            var currentText = current.ToString();
            if (merged.Count > 0)
            {
                var currentWords = currentText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                if (currentWords < 4)
                {
                    merged[merged.Count - 1] = merged[merged.Count - 1] + " " + currentText;
                }
                else
                {
                    merged.Add(currentText);
                }
            }
            else
            {
                merged.Add(currentText);
            }
        }

        return merged;
    }
}
