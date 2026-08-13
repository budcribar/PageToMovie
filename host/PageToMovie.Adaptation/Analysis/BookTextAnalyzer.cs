using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Adaptation;

/// <summary>Port of extract_book_source.analyze_book_text — quality + Stage 1 defaults.</summary>
public static class BookTextAnalyzer
{
    private static readonly Regex PageMarker = new(@"---\s*PAGE\s+(\d+)\s*---", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>Line-anchored page markers (matches Engine BookContextService.ParseBookPages).</summary>
    public static readonly Regex PageMarkerLine = new(@"^---\s*PAGE\s+(\d+)\s*---\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline, CommonRegex.Timeout);

    private static readonly Regex WeirdChars = new(@"[^\w\s'.,!?;:\-""()…°]", RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex BadTokens = new(@"\b\w*[0-9]\w*\b", RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex GarbleHits = new(@"\b(?:[A-Za-z]*[0-9][A-Za-z0-9]*|[A-Za-z]{1,2}[;:][A-Za-z]{2,})\b", RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex IllustrationParenRegex = new(@"\(\s*illustration only\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex WhitespaceSplitRegex = new(@"\s+", RegexOptions.Compiled, CommonRegex.Timeout);
    // IgnoreCase to match IllustrationParenRegex above — OCR/Gutenberg captions like
    // "(Illustration)" or "(ILLUSTRATION ONLY)" should be caught the same way "(illustration only)" is.
    private static readonly Regex IllustrationExactMatchRegex = new(@"^\(.*illustration.*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    public static BookTextAnalysis Analyze(string text, int? pagesHint = null)
    {
        text = GutenbergCleaner.StripHeaderAndFooter(text ?? "");
        var bodies = PageBodies(text);
        var pages = pagesHint is > 0 ? pagesHint.Value : (bodies.Count > 0 ? bodies.Count : 1);
        if (bodies.Count == 0 && !string.IsNullOrWhiteSpace(text))
            bodies = new List<string> { text.Trim() };

        var contentBodies = bodies.Where(b => !IsIllustrationOnly(b)).ToList();
        var plain = PageMarker.Replace(text ?? "", " ");
        plain = IllustrationParenRegex.Replace(plain, " ");
        plain = WhitespaceSplitRegex.Replace(plain, " ").Trim();
        var chars = plain.Length;
        var words = string.IsNullOrEmpty(plain) ? 0 : plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var letters = plain.Count(char.IsLetter);
        var letterRatio = chars > 0 ? letters / (double)chars : 0.0;

        var emptyPages = bodies.Count(b => IsIllustrationOnly(b) || b.Length < 20);
        var sparsePages = bodies.Count(b => b.Length < 120);
        var emptyRatio = emptyPages / (double)Math.Max(bodies.Count, 1);
        var sparseRatio = sparsePages / (double)Math.Max(bodies.Count, 1);
        var avgChars = chars / (double)Math.Max(pages, 1);

        var garbage = 0.0;
        var wordList = string.IsNullOrEmpty(plain)
            ? Array.Empty<string>()
            : plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (chars > 40)
        {
            // CA1875: Regex.Count avoids allocating MatchCollection
            var weird = WeirdChars.Count(plain);
            garbage += Math.Min(1.0, weird / (double)Math.Max(chars, 1) * 10);
            if (letterRatio < 0.55) garbage += 0.35;
            if (letterRatio < 0.4) garbage += 0.35;
            var badTokens = BadTokens.Count(plain);
            garbage += Math.Min(0.35, badTokens / (double)Math.Max(words, 1));
            var garbleHits = GarbleHits.Count(plain);
            garbage += Math.Min(0.3, garbleHits / (double)Math.Max(words, 1) * 2);
            // OCR soup: low vowels in longer tokens
            if (wordList.Length > 8)
            {
                // CA1827: Any() would early-out for existence checks; here we need a count ratio
                var shortJunk = 0;
                foreach (var w in wordList)
                {
                    if (w.Length is < 4 or > 12) continue;
                    if (!w.Any(c => "aeiouAEIOU".Contains(c))) shortJunk++;
                }
                garbage += Math.Min(0.35, shortJunk / (double)wordList.Length);
            }
        }

        garbage = Math.Clamp(garbage, 0, 1.5);

        TextQuality quality;
        if (words < 8 && contentBodies.Count == 0)
            quality = TextQuality.Empty;
        else if (garbage >= 0.45 || letterRatio < 0.4)
            quality = TextQuality.Poor;
        else if (words < 40 && sparseRatio > 0.6)
            quality = TextQuality.Good; // picture book clean short text
        else if (letterRatio >= 0.55 && garbage < 0.35)
            quality = TextQuality.Good;
        else
            quality = TextQuality.Poor;

        var textDensity = sparseRatio > 0.45 || avgChars < 200 ? TextDensity.Sparse : TextDensity.Normal;
        var bookKind = pages <= 40 && (textDensity == TextDensity.Sparse || words < 800)
            ? BookKind.PictureBook
            : words < 15000 ? BookKind.Short : BookKind.Novel;

        // Natural film length from adaptation density (speech times staging for short literary
        // work; market delta for novels). Calibrated on a published short-story film of about 17 minutes.
        var syllables = TextMetrics.CountSyllables(plain);
        var quoteFrac = AdaptationDensity.EstimateQuotedDialogueFraction(plain);
        var runtimeEstimate = AdaptationDensity.EstimateFromStats(bookKind, words, syllables, quoteFrac);
        var suggestedMinutes = runtimeEstimate.NaturalFilmMinutes;
        var suggestedChunks = bookKind == BookKind.PictureBook
            ? Math.Clamp(pages, 5, 20)
            : 10;

        var notes = new List<string>();
        if (textDensity == TextDensity.Sparse)
            notes.Add("Layout is illustration-heavy (normal for picture books) but wording may still be usable.");
        if (bookKind == BookKind.PictureBook)
            notes.Add($"Treated as picture book (~{pages} pages). Suggested Stage 1 runtime {suggestedMinutes} min.");
        if (quality == TextQuality.Poor)
            notes.Add("Text looks garbled (OCR noise). Prefer Grok vision on page images.");
        if (quality == TextQuality.Empty)
            notes.Add("Almost no readable text. Use Grok vision or paste a transcript.");

        return new BookTextAnalysis
        {
            Pages = pages,
            TextChars = chars,
            TextWords = words,
            LetterRatio = Math.Round(letterRatio, 3),
            EmptyPageRatio = Math.Round(emptyRatio, 3),
            SparsePageRatio = Math.Round(sparseRatio, 3),
            AvgCharsPerPage = Math.Round(avgChars, 1),
            GarbageScore = Math.Round(garbage, 3),
            TextQuality = quality,
            TextDensity = textDensity,
            BookKind = bookKind,
            ReadyForStage1 = quality == TextQuality.Good && garbage < 0.45,
            SuggestedTotalMinutes = suggestedMinutes,
            SuggestedChunkPages = suggestedChunks,
            Notes = notes,
        };
    }

    /// <summary>
    /// Stage 1 target runtime used by production Stage 1 / screenplay services and the
    /// screenplay benchmark. Optional override is clamped to 2–180; otherwise uses
    /// <see cref="AdaptationDensity"/> natural film minutes via <see cref="NaturalRuntime"/>.
    /// </summary>
    public static int ResolveStage1RuntimeMinutes(string bookText, int? overrideMinutes = null)
    {
        if (overrideMinutes is > 0)
            return NaturalRuntime.ClampMinutes(overrideMinutes.Value);
        return NaturalRuntime.ClampMinutes(
            AdaptationDensity.EstimateNatural(bookText).NaturalFilmMinutes);
    }

    /// <summary>
    /// Stats-only helper for tests; prefer <see cref="ResolveStage1RuntimeMinutes"/> /
    /// <see cref="AdaptationDensity.EstimateFromStats"/>.
    /// </summary>
    public static int SuggestStage1RuntimeMinutes(BookKind bookKind, int words, int pages)
    {
        words = Math.Max(0, words);
        // Without real text, approximate syllables ≈ words (tests only).
        return AdaptationDensity.EstimateFromStats(bookKind, words, syllables: words, quotedDialogueFraction: 0.2)
            .NaturalFilmMinutes;
    }

    public static int SuggestStage1RuntimeMinutes(string bookKind, int words, int pages) =>
        SuggestStage1RuntimeMinutes(AdaptationEnumExtensions.ParseBookKind(bookKind), words, pages);

    /// <summary>
    /// Page bodies for density/quality heuristics. Same split rules as Engine
    /// <c>BookContextService.ParseBookPages</c>: <c>--- PAGE N ---</c> markers
    /// when present, otherwise paragraph-based synthetic pages (plain .txt).
    /// Implemented here so Adaptation does not reference Engine.
    /// </summary>
    public static List<string> PageBodies(string text)
    {
        text ??= "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var bodies = new List<string>();

        var matches = PageMarkerLine.Matches(text);
        if (matches.Count > 0)
        {
            for (var i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var start = m.Index + m.Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                bodies.Add(text[start..end].Trim());
            }
            return bodies;
        }

        var paras = CommonRegex.Split(text.Trim(), @"\n\s*\n+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paras.Count == 0 && text.Trim().Length > 0)
            paras.Add(text.Trim());
        return paras;
    }

    public static bool IsIllustrationOnly(string body)
    {
        var b = (body ?? "").Trim().ToLowerInvariant();
        if (b.Length == 0) return true;
        if (b is "(illustration only)" or "illustration only" or "[illustration only]")
            return true;
        return IllustrationExactMatchRegex.IsMatch(b);
    }
}

public sealed class BookTextAnalysis
{
    public int Pages { get; set; }
    public int TextChars { get; set; }
    public int TextWords { get; set; }
    public double LetterRatio { get; set; }
    public double EmptyPageRatio { get; set; }
    public double SparsePageRatio { get; set; }
    public double AvgCharsPerPage { get; set; }
    public double GarbageScore { get; set; }
    public TextQuality TextQuality { get; set; } = TextQuality.Empty;
    public TextDensity TextDensity { get; set; } = TextDensity.Normal;
    public BookKind BookKind { get; set; } = BookKind.Short;
    public bool ReadyForStage1 { get; set; }
    public int SuggestedTotalMinutes { get; set; }
    public int SuggestedChunkPages { get; set; }
    public List<string> Notes { get; set; } = new();
    public string? TextEngine { get; set; }
}
