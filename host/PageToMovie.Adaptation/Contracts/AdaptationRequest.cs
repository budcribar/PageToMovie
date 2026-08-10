namespace PageToMovie.Adaptation.Contracts;

/// <summary>
/// Pure Stage‑1 convert inputs. No projectId, paths, or userId — Engine supplies book text
/// and injects <see cref="PageToMovie.Core.Abstractions.IChatClient"/>.
/// </summary>
public sealed class AdaptationRequest
{
    public required string BookText { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }

    /// <summary>Null → use natural runtime from density.</summary>
    public int? TargetRuntimeMinutes { get; init; }

    /// <summary>Planning model id from catalog (caller resolved).</summary>
    public string ModelId { get; init; } = "";

    public double Temperature { get; init; } = 0.2;

    /// <summary>Provider-neutral reasoning hint (low/medium/high/max); null = provider default.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Preferred visual medium for VISION_META / MEDIUM DIRECTIVE, or null/<c>auto</c> to infer.
    /// Engine/UI may set photoreal, illustrated_picture_book, etc.
    /// </summary>
    public string? VisualMedium { get; init; }

    /// <summary>
    /// Already-resolved overrides for <see cref="PageToMovie.Adaptation.Conversion.AdaptationPromptTokens"/>
    /// (per-project value, else
    /// admin-global default, else the hardcoded default) — Engine resolves the tiers before
    /// constructing this request; null here just means "use the hardcoded default" for that field.
    /// </summary>
    public int? MaxSpeakingCast { get; init; }
    public int? MaxDialogueWords { get; init; }
    public int? VoMaxSentences { get; init; }
    public int? SceneCountMin { get; init; }
    public int? SceneCountMax { get; init; }
    public int? MinAudioCuesPerScene { get; init; }
    public int? MinAudioCuesAtPeak { get; init; }
    public int? BodyWordsPerMinute { get; init; }
}
