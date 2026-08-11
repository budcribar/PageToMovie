namespace PageToMovie.Core.Models;

/// <summary>H4–H8 — aggregated takes-per-clip telemetry for cost learning and admin.</summary>
public sealed class TakesTelemetryStats
{
    /// <summary>Clips that have at least one billed take (unique project+scene+clip).</summary>
    public int ClipSampleCount { get; set; }
    /// <summary>Total billed video take events.</summary>
    public int EventCount { get; set; }
    public double MeanTakesPerClip { get; set; }
    public double P25TakesPerClip { get; set; }
    public double P50TakesPerClip { get; set; }
    public double P75TakesPerClip { get; set; }
    /// <summary>Share of clips with take_index ≥ 2 (0–1).</summary>
    public double RegenRate { get; set; }
    /// <summary>Share of take events that are user_regen (0–1).</summary>
    public double UserRegenShare { get; set; }
    /// <summary>Share of take events that are qa_auto (0–1).</summary>
    public double QaAutoShare { get; set; }
    public double FillHolesShare { get; set; }
    public double StaleRegenShare { get; set; }
    public double InitialShare { get; set; }
    /// <summary>Reason code → count (H3 optional reasons only).</summary>
    public Dictionary<string, int> Reasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Weekly-ish buckets: ISO week start date → mean takes (H7).</summary>
    public List<TakesTelemetryWeekBucket> Weekly { get; set; } = new();
    /// <summary>True when ClipSampleCount meets the min sample size for blending into estimates.</summary>
    public bool SufficientForBlend { get; set; }
    public string Scope { get; set; } = "global"; // global | project
    public string Notes { get; set; } = "";
}

public sealed class TakesTelemetryWeekBucket
{
    public string WeekStart { get; set; } = ""; // yyyy-MM-dd
    public int ClipSampleCount { get; set; }
    public double MeanTakesPerClip { get; set; }
    public double RegenRate { get; set; }
}

/// <summary>H1 row dual-written for studio-wide aggregates (privacy: no content fields).</summary>
public sealed class VideoTakeEventRecord
{
    public string ProjectId { get; set; } = "";
    public string? UserId { get; set; }
    public int Scene { get; set; }
    public int Clip { get; set; }
    public int TakeIndex { get; set; } = 1;
    public string TakeKind { get; set; } = VideoTakeKinds.Initial;
    public string? Reason { get; set; }
    public string? Model { get; set; }
    public string? Resolution { get; set; }
    public double? ListUsd { get; set; }
    public double? DurationSec { get; set; }
    public string? KeyMode { get; set; }
    public string? StableBeatId { get; set; }
    public bool HadCharRefs { get; set; }
    public bool HadLocRef { get; set; }
    public double? MinutesSincePrevTake { get; set; }
    /// <summary>H9 — when false, excluded from global studio averages.</summary>
    public bool ContributeToStudioAverages { get; set; } = true;
    public string? Ts { get; set; }
}

/// <summary>Embedded on CostReport for DecisionCard + calibration (H5–H8).</summary>
public sealed class CostTakesLearning
{
    public int GlobalClipSamples { get; set; }
    public int ProjectClipSamples { get; set; }
    public double? GlobalP25 { get; set; }
    public double? GlobalP50 { get; set; }
    public double? GlobalP75 { get; set; }
    public double? ProjectMeanTakes { get; set; }
    public double? ProjectRegenRate { get; set; }
    /// <summary>Blended expected takes used in this report's $ point.</summary>
    public double ExpectedTakes { get; set; } = 1.0;
    public double PriorTakes { get; set; } = 1.0;
    public double BlendWeight { get; set; }
    public bool UsedLearnedTakes { get; set; }
    /// <summary>Human line for DecisionCard, e.g. "typical ~1.5 takes/clip from studio history".</summary>
    public string HistoryLabel { get; set; } = "";
    /// <summary>H8 calibration: project mean vs estimate expected.</summary>
    public string CalibrationLabel { get; set; } = "";
    public bool SufficientForRange { get; set; }
}
