namespace PageToMovie.Adaptation.Contracts;

/// <summary>
/// Pure Stage‑1 convert outputs. Fountain + optional vision meta + runtime/analysis reports.
/// No project paths — Engine persists to disk.
/// </summary>
public sealed class AdaptationResult
{
    public required string Fountain { get; init; }

    /// <summary>Adaptation-owned DTO; Engine maps to project vision_meta / extract_meta.</summary>
    public AdaptationVisionMeta? VisionMeta { get; init; }

    public AdaptationVisionMetaStatus VisionMetaStatus { get; init; }
    public string? VisionMetaError { get; init; }

    public AdaptationReport? AdaptationReport { get; init; }
    public AdaptationReportStatus AdaptationReportStatus { get; init; }
    public string? AdaptationReportError { get; init; }

    public required NaturalRuntimeEstimate Runtime { get; init; }
    public required BookAnalysisResult Analysis { get; init; }

    public bool UsedHeuristicFallback { get; init; }
    public string PromptContentSha256 { get; init; } = "";
    public AdaptationConvertManifest? ConvertManifest { get; init; }
    public string? Notes { get; init; }
}
