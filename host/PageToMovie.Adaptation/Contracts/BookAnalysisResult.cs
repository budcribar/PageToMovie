namespace PageToMovie.Adaptation.Contracts;

/// <summary>
/// Book text quality + Stage‑1 defaults (maps from Engine <c>BookTextAnalysis</c> after Phase 1 move).
/// </summary>
public sealed class BookAnalysisResult
{
    public int Pages { get; init; }
    public int TextChars { get; init; }
    public int TextWords { get; init; }
    public double LetterRatio { get; init; }
    public double EmptyPageRatio { get; init; }
    public double SparsePageRatio { get; init; }
    public double AvgCharsPerPage { get; init; }
    public double GarbageScore { get; init; }
    public TextQuality TextQuality { get; init; } = TextQuality.Empty;
    public TextDensity TextDensity { get; init; } = TextDensity.Normal;
    public BookKind BookKind { get; init; } = BookKind.Short;
    public bool ReadyForStage1 { get; init; }
    public int SuggestedTotalMinutes { get; init; }
    public int SuggestedChunkPages { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public string? TextEngine { get; init; }
}
