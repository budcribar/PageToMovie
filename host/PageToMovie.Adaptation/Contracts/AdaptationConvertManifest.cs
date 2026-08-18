namespace PageToMovie.Adaptation.Contracts;

/// <summary>
/// Attribution pins for one Stage‑1 convert. Pure DTO — Engine persists under the project.
/// </summary>
public sealed class AdaptationConvertManifest
{
    public const string SchemaVersion = "stage1_convert_manifest.v1";

    public string Schema { get; init; } = SchemaVersion;

    /// <summary>UTC when convert completed.</summary>
    public string CompletedUtc { get; init; } = "";

    public string ModelId { get; init; } = "";
    public double Temperature { get; init; }
    public string? ReasoningEffort { get; init; }

    /// <summary>SHA-256 hex of the system prompt actually sent (after runtime token fill).</summary>
    public string PromptContentSha256 { get; init; } = "";

    /// <summary>12-char Adaptation surface id (<see cref="AdaptationVersion.Current"/>).</summary>
    public string AdaptationVersion { get; init; } = "";

    /// <summary>unlimited | natural | reduced | custom | none</summary>
    public string RuntimeMode { get; init; } = "";

    /// <summary>Density natural minutes (informational).</summary>
    public int NaturalRuntimeMinutes { get; init; }

    /// <summary>Artificial target when set; null when unlimited.</summary>
    public int? TargetRuntimeMinutes { get; init; }

    public bool UsedHeuristicFallback { get; init; }
    public string? HeuristicFallbackReason { get; init; }

    public string VisionMetaStatus { get; init; } = "";
    public string AdaptationReportStatus { get; init; } = "";

    /// <summary>Provider book file session id when multi-turn/file_id was used.</summary>
    public string? BookFileSessionId { get; init; }

    public string? BookId { get; init; }

    public string Title { get; init; } = "";
    public string? Author { get; init; }

    public int FountainChars { get; init; }
    public int SceneCountApprox { get; init; }
}
