namespace PageToMovie.Adaptation.Contracts;

/// <summary>
/// Natural vs resolved target film length (pure math — no ProjectStore).
/// </summary>
public sealed class NaturalRuntimeEstimate
{
    public int NaturalMinutes { get; init; }

    /// <summary>Natural if not overridden.</summary>
    public int TargetMinutes { get; init; }

    /// <summary>natural | reduced | custom</summary>
    public string Mode { get; init; } = "natural";

    public string Method { get; init; } = "";
    public int SourceWords { get; init; }
    public int SourceSyllables { get; init; }
    public BookKind BookKind { get; init; } = BookKind.Short;

    /// <summary>δ — finished film minutes per 1,000 source words.</summary>
    public double MinutesPerThousandWords { get; init; }

    /// <summary>τ — natural film / audiobook compression ratio.</summary>
    public double TemporalCompressionRatio { get; init; }

    public double QuotedDialogueFraction { get; init; }
    public string Notes { get; init; } = "";
}
