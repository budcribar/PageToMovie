using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

#region Enums

/// <summary>
/// Identification keys for extended external AI service providers.
/// Note: SupportedModelCatalog is the single source of truth for runtime providers;
/// this enum is used for strongly-typed metadata schemas.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiProviderIdKind
{
    Grok,
    Suno,
    AiMusicApi,
    OpenAi,
    Anthropic,
    Fal,
    Google,
    ElevenLabs,
    Runway,
    Luma,
    StabilityAi,
    Midjourney,
    Replicate,
    Cohere,
    DeepSeek,
    Mistral,
    LocalOllama,
    Custom
}

/// <summary>
/// Extended functional capability categories for AI models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelCapabilityCategoryKind
{
    TextGeneration,
    ImageGeneration,
    VideoGeneration,
    AudioGeneration,
    VoiceCloning,
    SpeechToText,
    Embeddings,
    VisionAnalysis,
    MusicGeneration,
    LipSync,
    DepthEstimation,
    Inpainting
}

/// <summary>
/// Extended units of measurement used for model API pricing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelPricingUnitKind
{
    Per1kTokens,
    Per1mTokens,
    PerImage,
    PerSecondVideo,
    PerMinuteAudio,
    PerRequest,
    PerGigabyte,
    Free
}

/// <summary>
/// Extended concurrency behaviors when encountering provider rate limits.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelConcurrencyBehavior
{
    Queue,
    Reject,
    Throttle,
    Fallback,
    RetryWithBackoff,
    CircuitBreak
}

/// <summary>
/// Extended protocol endpoint types for model service integration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelEndpointTypeKind
{
    HttpJson,
    HttpStreaming,
    WebSockets,
    Grpc,
    LocalProcess,
    SdkNative,
    WebhookAsync
}

/// <summary>
/// Extended status code responses returned by provider execution jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelResponseStatusCode
{
    Success,
    Pending,
    Processing,
    RateLimited,
    QuotaExceeded,
    ServerError,
    InvalidRequest,
    ContentFiltered,
    Timeout,
    Unauthorized,
    ServiceUnavailable
}

/// <summary>
/// Extended AI content safety categories for safety filters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiSafetyRatingCategory
{
    Safe,
    Warning,
    Flagged,
    Blocked,
    Harassment,
    HateSpeech,
    SexualContent,
    DangerousContent,
    CivicIntegrity,
    Unknown
}

/// <summary>
/// Extended rationale for model parameter or provider overrides.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiCallOverrideReasonKind
{
    OperatorChoice,
    QualityDegradation,
    CostOptimization,
    RateLimitFallback,
    Failover,
    Testing,
    PolicyEnforcement,
    PerformanceBenchmark
}

/// <summary>
/// Common standardized parameter names passed to AI model endpoints.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelParameterName
{
    Temperature,
    TopP,
    TopK,
    MaxTokens,
    Seed,
    NegativePrompt,
    AspectRatio,
    Resolution,
    FrameRate,
    GuidanceScale,
    FrequencyPenalty,
    PresencePenalty,
    SystemPrompt,
    ReferenceImageWeight,
    DurationSeconds,
    CustomParam
}

/// <summary>
/// Extended streaming payload chunk types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreamingChunkTypeKind
{
    TextDelta,
    AudioBytes,
    VideoFrames,
    ProgressStatus,
    Metadata,
    ErrorDelta,
    Heartbeat,
    FinalSummary
}

/// <summary>
/// Extended termination reason kinds for AI model outputs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinishReasonKindType
{
    Stop,
    Length,
    ContentFilter,
    ToolCalls,
    Error,
    Cancelled,
    Timeout,
    ContextOverflow
}

/// <summary>
/// Extended token count category classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TokenCountCategory
{
    InputPrompt,
    OutputCompletion,
    CacheRead,
    CacheWrite,
    ReasoningTokens,
    Total
}

/// <summary>
/// Extended architectural model family classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelFamilyType
{
    Grok,
    Claude,
    Gpt,
    Gemini,
    Llama,
    Flux,
    Veo,
    Suno,
    Midjourney,
    StableDiffusion,
    Sora,
    RunwayGen,
    DeepSeek,
    Custom
}

/// <summary>
/// Extended performance tier levels for AI models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelTierLevelKind
{
    Free,
    Standard,
    Pro,
    Enterprise,
    Ultra,
    Experimental,
    Legacy,
    Custom
}

/// <summary>
/// Extended billing mode classifications for provider services.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelBillingModeKind
{
    PayAsYouGo,
    Subscription,
    Credits,
    FreeTier,
    FlatRate,
    ReservedCapacity,
    UsageTier
}

/// <summary>
/// Extended key status types for API authentication keys.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderKeyStatusType
{
    Unconfigured,
    Valid,
    Invalid,
    Expired,
    RateLimited,
    QuotaExceeded,
    Revoked,
    PendingVerification
}

/// <summary>
/// Extended capability feature flag types for models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelFeatureFlagType
{
    SupportsStreaming,
    SupportsVision,
    SupportsFunctionCalling,
    SupportsSystemPrompt,
    SupportsReferenceImage,
    SupportsNegativePrompt,
    SupportsSeed,
    SupportsAudioOutput,
    SupportsVideoOutput,
    SupportsLoRa
}

/// <summary>
/// Extended sort options for model catalog displays.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelCatalogSortOption
{
    DisplayName,
    Provider,
    Capability,
    Cost,
    Latency,
    ReleaseDate,
    Popularity,
    QualityScore,
    ContextWindowSize
}

/// <summary>
/// Extended filter state options for model selection views.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelFilterStateOption
{
    All,
    EnabledOnly,
    DisabledOnly,
    ExperimentalOnly,
    DeprecatedOnly,
    RecommendedOnly,
    ActiveOnly
}

/// <summary>
/// Extended retry strategies for model API request failures.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelClientRetryStrategy
{
    None,
    Linear,
    Exponential,
    Immediate,
    Jittered,
    AdaptiveBackoff
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods and string parsers for AI model provider extended enums.
/// </summary>
public static class AiModelProviderExtendedEnumExtensions
{
    public static string ToApiString(this AiProviderIdKind val) => val switch
    {
        AiProviderIdKind.AiMusicApi => "aimusicapi",
        AiProviderIdKind.OpenAi => "openai",
        AiProviderIdKind.ElevenLabs => "elevenlabs",
        AiProviderIdKind.StabilityAi => "stability_ai",
        AiProviderIdKind.LocalOllama => "local_ollama",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelCapabilityCategoryKind val) => val switch
    {
        ModelCapabilityCategoryKind.TextGeneration => "text_generation",
        ModelCapabilityCategoryKind.ImageGeneration => "image_generation",
        ModelCapabilityCategoryKind.VideoGeneration => "video_generation",
        ModelCapabilityCategoryKind.AudioGeneration => "audio_generation",
        ModelCapabilityCategoryKind.VoiceCloning => "voice_cloning",
        ModelCapabilityCategoryKind.SpeechToText => "speech_to_text",
        ModelCapabilityCategoryKind.VisionAnalysis => "vision_analysis",
        ModelCapabilityCategoryKind.MusicGeneration => "music_generation",
        ModelCapabilityCategoryKind.LipSync => "lip_sync",
        ModelCapabilityCategoryKind.DepthEstimation => "depth_estimation",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelPricingUnitKind val) => val switch
    {
        ModelPricingUnitKind.Per1kTokens => "per_1k_tokens",
        ModelPricingUnitKind.Per1mTokens => "per_1m_tokens",
        ModelPricingUnitKind.PerImage => "per_image",
        ModelPricingUnitKind.PerSecondVideo => "per_second_video",
        ModelPricingUnitKind.PerMinuteAudio => "per_minute_audio",
        ModelPricingUnitKind.PerRequest => "per_request",
        ModelPricingUnitKind.PerGigabyte => "per_gigabyte",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelConcurrencyBehavior val) => val switch
    {
        ModelConcurrencyBehavior.RetryWithBackoff => "retry_with_backoff",
        ModelConcurrencyBehavior.CircuitBreak => "circuit_break",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelEndpointTypeKind val) => val switch
    {
        ModelEndpointTypeKind.HttpJson => "http_json",
        ModelEndpointTypeKind.HttpStreaming => "http_streaming",
        ModelEndpointTypeKind.LocalProcess => "local_process",
        ModelEndpointTypeKind.SdkNative => "sdk_native",
        ModelEndpointTypeKind.WebhookAsync => "webhook_async",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelResponseStatusCode val) => val switch
    {
        ModelResponseStatusCode.RateLimited => "rate_limited",
        ModelResponseStatusCode.QuotaExceeded => "quota_exceeded",
        ModelResponseStatusCode.ServerError => "server_error",
        ModelResponseStatusCode.InvalidRequest => "invalid_request",
        ModelResponseStatusCode.ContentFiltered => "content_filtered",
        ModelResponseStatusCode.ServiceUnavailable => "service_unavailable",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this AiSafetyRatingCategory val) => val switch
    {
        AiSafetyRatingCategory.HateSpeech => "hate_speech",
        AiSafetyRatingCategory.SexualContent => "sexual_content",
        AiSafetyRatingCategory.DangerousContent => "dangerous_content",
        AiSafetyRatingCategory.CivicIntegrity => "civic_integrity",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this AiCallOverrideReasonKind val) => val switch
    {
        AiCallOverrideReasonKind.OperatorChoice => "operator_choice",
        AiCallOverrideReasonKind.QualityDegradation => "quality_degradation",
        AiCallOverrideReasonKind.CostOptimization => "cost_optimization",
        AiCallOverrideReasonKind.RateLimitFallback => "rate_limit_fallback",
        AiCallOverrideReasonKind.PolicyEnforcement => "policy_enforcement",
        AiCallOverrideReasonKind.PerformanceBenchmark => "performance_benchmark",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelParameterName val) => val switch
    {
        ModelParameterName.TopP => "top_p",
        ModelParameterName.TopK => "top_k",
        ModelParameterName.MaxTokens => "max_tokens",
        ModelParameterName.NegativePrompt => "negative_prompt",
        ModelParameterName.AspectRatio => "aspect_ratio",
        ModelParameterName.FrameRate => "frame_rate",
        ModelParameterName.GuidanceScale => "guidance_scale",
        ModelParameterName.FrequencyPenalty => "frequency_penalty",
        ModelParameterName.PresencePenalty => "presence_penalty",
        ModelParameterName.SystemPrompt => "system_prompt",
        ModelParameterName.ReferenceImageWeight => "reference_image_weight",
        ModelParameterName.DurationSeconds => "duration_seconds",
        ModelParameterName.CustomParam => "custom_param",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this StreamingChunkTypeKind val) => val switch
    {
        StreamingChunkTypeKind.TextDelta => "text_delta",
        StreamingChunkTypeKind.AudioBytes => "audio_bytes",
        StreamingChunkTypeKind.VideoFrames => "video_frames",
        StreamingChunkTypeKind.ProgressStatus => "progress_status",
        StreamingChunkTypeKind.ErrorDelta => "error_delta",
        StreamingChunkTypeKind.FinalSummary => "final_summary",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this FinishReasonKindType val) => val switch
    {
        FinishReasonKindType.ContentFilter => "content_filter",
        FinishReasonKindType.ToolCalls => "tool_calls",
        FinishReasonKindType.ContextOverflow => "context_overflow",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this TokenCountCategory val) => val switch
    {
        TokenCountCategory.InputPrompt => "input_prompt",
        TokenCountCategory.OutputCompletion => "output_completion",
        TokenCountCategory.CacheRead => "cache_read",
        TokenCountCategory.CacheWrite => "cache_write",
        TokenCountCategory.ReasoningTokens => "reasoning_tokens",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelFamilyType val) => val switch
    {
        ModelFamilyType.StableDiffusion => "stable_diffusion",
        ModelFamilyType.RunwayGen => "runway_gen",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelTierLevelKind val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ModelBillingModeKind val) => val switch
    {
        ModelBillingModeKind.PayAsYouGo => "pay_as_you_go",
        ModelBillingModeKind.FreeTier => "free_tier",
        ModelBillingModeKind.FlatRate => "flat_rate",
        ModelBillingModeKind.ReservedCapacity => "reserved_capacity",
        ModelBillingModeKind.UsageTier => "usage_tier",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ProviderKeyStatusType val) => val switch
    {
        ProviderKeyStatusType.RateLimited => "rate_limited",
        ProviderKeyStatusType.QuotaExceeded => "quota_exceeded",
        ProviderKeyStatusType.PendingVerification => "pending_verification",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelFeatureFlagType val) => val switch
    {
        ModelFeatureFlagType.SupportsStreaming => "supports_streaming",
        ModelFeatureFlagType.SupportsVision => "supports_vision",
        ModelFeatureFlagType.SupportsFunctionCalling => "supports_function_calling",
        ModelFeatureFlagType.SupportsSystemPrompt => "supports_system_prompt",
        ModelFeatureFlagType.SupportsReferenceImage => "supports_reference_image",
        ModelFeatureFlagType.SupportsNegativePrompt => "supports_negative_prompt",
        ModelFeatureFlagType.SupportsSeed => "supports_seed",
        ModelFeatureFlagType.SupportsAudioOutput => "supports_audio_output",
        ModelFeatureFlagType.SupportsVideoOutput => "supports_video_output",
        ModelFeatureFlagType.SupportsLoRa => "supports_lora",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelCatalogSortOption val) => val switch
    {
        ModelCatalogSortOption.DisplayName => "display_name",
        ModelCatalogSortOption.ReleaseDate => "release_date",
        ModelCatalogSortOption.QualityScore => "quality_score",
        ModelCatalogSortOption.ContextWindowSize => "context_window_size",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelFilterStateOption val) => val switch
    {
        ModelFilterStateOption.EnabledOnly => "enabled_only",
        ModelFilterStateOption.DisabledOnly => "disabled_only",
        ModelFilterStateOption.ExperimentalOnly => "experimental_only",
        ModelFilterStateOption.DeprecatedOnly => "deprecated_only",
        ModelFilterStateOption.RecommendedOnly => "recommended_only",
        ModelFilterStateOption.ActiveOnly => "active_only",
        _ => val.ToString().ToLowerInvariant()
    };

    public static string ToApiString(this ModelClientRetryStrategy val) => val switch
    {
        ModelClientRetryStrategy.AdaptiveBackoff => "adaptive_backoff",
        _ => val.ToString().ToLowerInvariant()
    };

    public static AiProviderIdKind ParseAiProviderIdKind(string? s, AiProviderIdKind defaultValue = AiProviderIdKind.Grok)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AiProviderIdKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static AiProviderIdKind ToAiProviderIdKind(this string? s, AiProviderIdKind defaultValue = AiProviderIdKind.Grok) => ParseAiProviderIdKind(s, defaultValue);

    public static ModelCapabilityCategoryKind ParseModelCapabilityCategoryKind(string? s, ModelCapabilityCategoryKind defaultValue = ModelCapabilityCategoryKind.TextGeneration)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelCapabilityCategoryKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelCapabilityCategoryKind ToModelCapabilityCategoryKind(this string? s, ModelCapabilityCategoryKind defaultValue = ModelCapabilityCategoryKind.TextGeneration) => ParseModelCapabilityCategoryKind(s, defaultValue);

    public static ModelPricingUnitKind ParseModelPricingUnitKind(string? s, ModelPricingUnitKind defaultValue = ModelPricingUnitKind.PerRequest)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelPricingUnitKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelPricingUnitKind ToModelPricingUnitKind(this string? s, ModelPricingUnitKind defaultValue = ModelPricingUnitKind.PerRequest) => ParseModelPricingUnitKind(s, defaultValue);

    public static ModelConcurrencyBehavior ParseModelConcurrencyBehavior(string? s, ModelConcurrencyBehavior defaultValue = ModelConcurrencyBehavior.Queue)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelConcurrencyBehavior>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelConcurrencyBehavior ToModelConcurrencyBehavior(this string? s, ModelConcurrencyBehavior defaultValue = ModelConcurrencyBehavior.Queue) => ParseModelConcurrencyBehavior(s, defaultValue);

    public static ModelEndpointTypeKind ParseModelEndpointTypeKind(string? s, ModelEndpointTypeKind defaultValue = ModelEndpointTypeKind.HttpJson)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelEndpointTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelEndpointTypeKind ToModelEndpointTypeKind(this string? s, ModelEndpointTypeKind defaultValue = ModelEndpointTypeKind.HttpJson) => ParseModelEndpointTypeKind(s, defaultValue);

    public static ModelResponseStatusCode ParseModelResponseStatusCode(string? s, ModelResponseStatusCode defaultValue = ModelResponseStatusCode.Success)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelResponseStatusCode>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelResponseStatusCode ToModelResponseStatusCode(this string? s, ModelResponseStatusCode defaultValue = ModelResponseStatusCode.Success) => ParseModelResponseStatusCode(s, defaultValue);

    public static AiSafetyRatingCategory ParseAiSafetyRatingCategory(string? s, AiSafetyRatingCategory defaultValue = AiSafetyRatingCategory.Safe)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AiSafetyRatingCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static AiSafetyRatingCategory ToAiSafetyRatingCategory(this string? s, AiSafetyRatingCategory defaultValue = AiSafetyRatingCategory.Safe) => ParseAiSafetyRatingCategory(s, defaultValue);

    public static AiCallOverrideReasonKind ParseAiCallOverrideReasonKind(string? s, AiCallOverrideReasonKind defaultValue = AiCallOverrideReasonKind.OperatorChoice)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AiCallOverrideReasonKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static AiCallOverrideReasonKind ToAiCallOverrideReasonKind(this string? s, AiCallOverrideReasonKind defaultValue = AiCallOverrideReasonKind.OperatorChoice) => ParseAiCallOverrideReasonKind(s, defaultValue);

    public static ModelParameterName ParseModelParameterName(string? s, ModelParameterName defaultValue = ModelParameterName.Temperature)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelParameterName>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelParameterName ToModelParameterName(this string? s, ModelParameterName defaultValue = ModelParameterName.Temperature) => ParseModelParameterName(s, defaultValue);

    public static StreamingChunkTypeKind ParseStreamingChunkTypeKind(string? s, StreamingChunkTypeKind defaultValue = StreamingChunkTypeKind.TextDelta)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<StreamingChunkTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static StreamingChunkTypeKind ToStreamingChunkTypeKind(this string? s, StreamingChunkTypeKind defaultValue = StreamingChunkTypeKind.TextDelta) => ParseStreamingChunkTypeKind(s, defaultValue);

    public static FinishReasonKindType ParseFinishReasonKindType(string? s, FinishReasonKindType defaultValue = FinishReasonKindType.Stop)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<FinishReasonKindType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static FinishReasonKindType ToFinishReasonKindType(this string? s, FinishReasonKindType defaultValue = FinishReasonKindType.Stop) => ParseFinishReasonKindType(s, defaultValue);

    public static TokenCountCategory ParseTokenCountCategory(string? s, TokenCountCategory defaultValue = TokenCountCategory.Total)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<TokenCountCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static TokenCountCategory ToTokenCountCategory(this string? s, TokenCountCategory defaultValue = TokenCountCategory.Total) => ParseTokenCountCategory(s, defaultValue);

    public static ModelFamilyType ParseModelFamilyType(string? s, ModelFamilyType defaultValue = ModelFamilyType.Grok)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelFamilyType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelFamilyType ToModelFamilyType(this string? s, ModelFamilyType defaultValue = ModelFamilyType.Grok) => ParseModelFamilyType(s, defaultValue);

    public static ModelTierLevelKind ParseModelTierLevelKind(string? s, ModelTierLevelKind defaultValue = ModelTierLevelKind.Standard)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelTierLevelKind>(s, true, out var r) ? r : defaultValue;
    }

    public static ModelTierLevelKind ToModelTierLevelKind(this string? s, ModelTierLevelKind defaultValue = ModelTierLevelKind.Standard) => ParseModelTierLevelKind(s, defaultValue);

    public static ModelBillingModeKind ParseModelBillingModeKind(string? s, ModelBillingModeKind defaultValue = ModelBillingModeKind.PayAsYouGo)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelBillingModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelBillingModeKind ToModelBillingModeKind(this string? s, ModelBillingModeKind defaultValue = ModelBillingModeKind.PayAsYouGo) => ParseModelBillingModeKind(s, defaultValue);

    public static ProviderKeyStatusType ParseProviderKeyStatusType(string? s, ProviderKeyStatusType defaultValue = ProviderKeyStatusType.Unconfigured)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ProviderKeyStatusType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ProviderKeyStatusType ToProviderKeyStatusType(this string? s, ProviderKeyStatusType defaultValue = ProviderKeyStatusType.Unconfigured) => ParseProviderKeyStatusType(s, defaultValue);

    public static ModelFeatureFlagType ParseModelFeatureFlagType(string? s, ModelFeatureFlagType defaultValue = ModelFeatureFlagType.SupportsStreaming)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelFeatureFlagType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelFeatureFlagType ToModelFeatureFlagType(this string? s, ModelFeatureFlagType defaultValue = ModelFeatureFlagType.SupportsStreaming) => ParseModelFeatureFlagType(s, defaultValue);

    public static ModelCatalogSortOption ParseModelCatalogSortOption(string? s, ModelCatalogSortOption defaultValue = ModelCatalogSortOption.DisplayName)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelCatalogSortOption>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelCatalogSortOption ToModelCatalogSortOption(this string? s, ModelCatalogSortOption defaultValue = ModelCatalogSortOption.DisplayName) => ParseModelCatalogSortOption(s, defaultValue);

    public static ModelFilterStateOption ParseModelFilterStateOption(string? s, ModelFilterStateOption defaultValue = ModelFilterStateOption.All)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelFilterStateOption>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelFilterStateOption ToModelFilterStateOption(this string? s, ModelFilterStateOption defaultValue = ModelFilterStateOption.All) => ParseModelFilterStateOption(s, defaultValue);

    public static ModelClientRetryStrategy ParseModelClientRetryStrategy(string? s, ModelClientRetryStrategy defaultValue = ModelClientRetryStrategy.Exponential)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ModelClientRetryStrategy>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }

    public static ModelClientRetryStrategy ToModelClientRetryStrategy(this string? s, ModelClientRetryStrategy defaultValue = ModelClientRetryStrategy.Exponential) => ParseModelClientRetryStrategy(s, defaultValue);

}

#endregion
