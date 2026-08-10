namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Values substituted into book_to_fountain before the model sees the prompt.
/// No unresolved <c>{{TOKEN}}</c> may remain after <see cref="AdaptationPromptPack.ApplyPromptTokens"/>.
/// </summary>
public sealed class AdaptationPromptTokens
{
    /// <summary>Null / ≤0 → unlimited natural-length directive.</summary>
    public int? TotalRuntimeMinutes { get; init; }

    /// <summary>
    /// User/project preference for render medium, or <c>auto</c> to let the model infer.
    /// Typical values: auto, photoreal, illustrated_picture_book, live_action, stylized_2d.
    /// </summary>
    public string VisualMedium { get; init; } = "auto";

    public string DraftDate { get; init; } = DateTime.UtcNow.ToString("M/d/yyyy");

    public int MaxDialogueWords { get; init; } = 35;
    /// <summary>
    /// Default speaking-cast ceiling. 16 leaves room for epic/novel ensembles without
    /// forcing every supporting role into Action/NARRATOR (old default of 8 collapsed Odyssey-scale casts).
    /// </summary>
    public int MaxSpeakingCast { get; init; } = 16;
    public int BodyWordsPerMinute { get; init; } = 155;
    public int MinAudioCuesPerScene { get; init; } = 1;
    public int MinAudioCuesAtPeak { get; init; } = 2;
    public int VoMaxSentences { get; init; } = 3;

    /// <summary>
    /// Optional scene-count band. Null = do not constrain count (prefer runtime guidance only).
    /// When null, SCENE_COUNT_MIN/MAX tokens are replaced with open guidance text.
    /// </summary>
    public int? SceneCountMin { get; init; }
    public int? SceneCountMax { get; init; }

    public static AdaptationPromptTokens Default(int? totalRuntimeMinutes = null, string? visualMedium = null) =>
        new()
        {
            TotalRuntimeMinutes = totalRuntimeMinutes,
            VisualMedium = string.IsNullOrWhiteSpace(visualMedium) ? "auto" : visualMedium.Trim(),
        };
}
