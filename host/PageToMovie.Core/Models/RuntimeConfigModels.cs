namespace PageToMovie.Core.Models;

/// <summary>Hot-editable server settings (Phase D admin config).</summary>
public sealed class RuntimeConfigDto
{
    public CapacityRuntimeDto Capacity { get; set; } = new();
    public FakesRuntimeDto Fakes { get; set; } = new();
    public AdaptationRuntimeDto Adaptation { get; set; } = new();
    public TimeoutsRuntimeDto Timeouts { get; set; } = new();
    public bool UseFakes { get; set; }
    /// <summary>
    /// Customer charge multiplier on vendor list rates (estimates + actual charges).
    /// 1.0 = pass-through. Hot-applied without restart.
    /// </summary>
    public double ChargeMultiplier { get; set; } = 1.0;
    /// <summary>Settings that need process restart to fully apply.</summary>
    public List<string> RestartRequired { get; set; } = new();
    public string? ConfigPath { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class TimeoutsRuntimeDto
{
    public int ImageTimeoutSeconds { get; set; } = 300;
    public int VideoTimeoutSeconds { get; set; } = 900;
    public int ChatTimeoutSeconds { get; set; } = 1200;
    public int AudioTimeoutSeconds { get; set; } = 300;
}

public sealed class CapacityRuntimeDto
{
    public int MaxVideoInFlight { get; set; } = 4;
    public int MaxVideoInFlightPerUser { get; set; } = 2;
    public int MaxQueuePerUser { get; set; } = 5;
}

public sealed class FakesRuntimeDto
{
    public string VideoMode { get; set; } = "MergeRealistic";
    public int VideoDelayMs { get; set; } = 200;
    public double FailRate { get; set; }
    public int RateLimitEveryN { get; set; }
}

/// <summary>
/// Admin-global defaults for Stage 1 adaptation tunables that otherwise default to a hardcoded
/// value on <c>AdaptationPromptTokens</c> (book_to_fountain.txt). Null = use the hardcoded
/// default. The five "shared" fields can be overridden further per-project on Configuration's
/// "Advanced Adaptation Settings" panel (per-project wins over this admin default, which wins
/// over the hardcoded default). MinAudioCuesPerScene/MinAudioCuesAtPeak/BodyWordsPerMinute are
/// admin-only — quality floors/calibration constants, not a per-book creative choice.
/// </summary>
public sealed class AdaptationRuntimeDto
{
    public int? MaxSpeakingCast { get; set; }
    public int? MaxDialogueWords { get; set; }
    public int? VoMaxSentences { get; set; }
    public int? SceneCountMin { get; set; }
    public int? SceneCountMax { get; set; }
    public int? MinAudioCuesPerScene { get; set; }
    public int? MinAudioCuesAtPeak { get; set; }
    public int? BodyWordsPerMinute { get; set; }
}

public sealed class RuntimeConfigUpdateRequest
{
    public CapacityRuntimeDto? Capacity { get; set; }
    public FakesRuntimeDto? Fakes { get; set; }
    public AdaptationRuntimeDto? Adaptation { get; set; }
    public TimeoutsRuntimeDto? Timeouts { get; set; }
    public bool? UseFakes { get; set; }
    /// <summary>Customer charge multiplier (list rate × this = charge). Null = leave unchanged.</summary>
    public double? ChargeMultiplier { get; set; }
}

public sealed class AdminCancelJobRequest
{
    public string? JobId { get; set; }
}

public sealed class AdminReleaseLockRequest
{
    public string Resource { get; set; } = "";
    public bool Force { get; set; } = true;
}
