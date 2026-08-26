namespace PageToMovie.Core.Abstractions;

/// <summary>
/// Chat completions. Grok, Anthropic, Gemini, or fake — see Engine MultiProviderChatClient.
/// Lives in Core so Stage‑1 Adaptation (and Engine) share one interface without Adaptation
/// referencing Engine.
/// </summary>
public interface IChatClient
{
    bool IsConfigured { get; }

    /// <param name="mode">
    /// Telemetry tag for <c>api_calls.jsonl</c> (<c>ApiCallTelemetry.Mode</c>), e.g.
    /// <c>book_to_fountain</c>, <c>cast_from_screenplay</c>, <c>cast_visual_literalize</c>.
    /// </param>
    /// <param name="reasoningEffort">
    /// Provider-neutral reasoning/thinking intensity hint: <c>"low"</c>, <c>"medium"</c>,
    /// <c>"high"</c>, or <c>"max"</c>. Null (default) leaves each provider's own default
    /// behavior untouched. Each concrete client translates this to its own API shape
    /// (OpenAI/xAI <c>reasoning_effort</c>, Anthropic <c>thinking</c>+<c>output_config.effort</c>,
    /// Gemini <c>thinkingConfig.thinkingLevel</c>) and self-heals by retrying without it if the
    /// requested model doesn't support the parameter at all, rather than requiring callers to
    /// know which models are reasoning-capable.
    /// </param>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null);
}

/// <summary>Canonical <see cref="IChatClient.CompleteAsync"/> mode tags for telemetry.</summary>
public static class ChatCallModes
{
    public const string PromptImprovementReview = "prompt_improvement_review";
    public const string SidecarPlanning = "sidecar_planning";
    public const string BookToFountain = "book_to_fountain";
    public const string BookToFountainRetry = "book_to_fountain_retry";
    public const string BookToFountainCoverage = "book_to_fountain_coverage";
    public const string BookToFountainChunk = "book_to_fountain_chunk";
    public const string BookToFountainChunkRetry = "book_to_fountain_chunk_retry";
    public const string BookToFountainMerge = "book_to_fountain_merge";
    public const string BookToFountainLocationsRetry = "book_to_fountain_locations_retry";
    public const string BookToFountainSpeakersRetry = "book_to_fountain_speakers_retry";
    public const string BookToFountainNarrationRetry = "book_to_fountain_narration_retry";
    public const string BookToFountainLocationNormalizeRetry = "book_to_fountain_location_normalize_retry";
    public const string BookToFountainNameNormalizeRetry = "book_to_fountain_name_normalize_retry";
    public const string BookToIndex = "book_to_index";
    public const string BookToFountainIndex = "book_to_fountain_index";
    public const string CastFromScreenplay = "cast_from_screenplay";
    public const string VisionMetaAdaptation = "vision_meta_adaptation";
    public const string CastVisualLiteralize = "cast_visual_literalize";
    public const string LearningPropose = "learning_propose";
    public const string SilentBeatClassify = "silent_beat_classify";
    public const string AmbientSfxClassify = "ambient_sfx_classify";
    public const string OnScreenCastClassify = "onscreen_cast_classify";
    public const string ExtendCutClassify = "extend_cut_classify";
    public const string ContinuationActionClassify = "continuation_action_classify";
    public const string SpeciesKindClassify = "species_kind_classify";
    public const string PlateRankClassify = "plate_rank_classify";
    public const string ShotPlanRefineClassify = "shot_plan_refine_classify";
    public const string BeatPacingClassify = "beat_pacing_classify";
    public const string CinematicLightingClassify = "cinematic_lighting_classify";
    public const string CameraDirectorClassify = "camera_director_classify";
    public const string NegativePromptClassify = "negative_prompt_classify";
    public const string WardrobeContinuityClassify = "wardrobe_continuity_classify";
    public const string CharacterEmotionArcClassify = "character_emotion_arc_classify";
    public const string SoundDesignComposerClassify = "sound_design_composer_classify";
    public const string DepthOfFieldClassify = "depth_of_field_classify";
    public const string ColorPaletteGradingClassify = "color_palette_grading_classify";
}
