using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

#region Enums

/// <summary>
/// Identification keys for external AI service providers.
/// Note: SupportedModelCatalog is the single source of truth for runtime providers;
/// this enum is used for strongly-typed metadata schemas.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiProviderId
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
    Custom
}

/// <summary>
/// Functional capability categories for AI models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelCapabilityCategory
{
    TextGeneration,
    ImageGeneration,
    VideoGeneration,
    AudioGeneration,
    VoiceCloning,
    SpeechToText,
    Embeddings,
    VisionAnalysis
}

/// <summary>
/// Units of measurement used for model API pricing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelPricingUnit
{
    Per1kTokens,
    Per1mTokens,
    PerImage,
    PerSecondVideo,
    PerMinuteAudio,
    PerRequest,
    Free
}

/// <summary>
/// Handling behavior when hitting provider concurrency limits.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelConcurrencyLimitBehavior
{
    Queue,
    Reject,
    Throttle,
    Fallback,
    RetryWithBackoff
}

/// <summary>
/// Protocol endpoint types supported by AI provider services.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelEndpointType
{
    HttpJson,
    HttpStreaming,
    WebSockets,
    Grpc,
    LocalProcess,
    SdkNative
}

/// <summary>
/// Execution response statuses returned from AI provider invocations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelResponseStatus
{
    Success,
    Pending,
    Processing,
    RateLimited,
    QuotaExceeded,
    ServerError,
    InvalidRequest,
    ContentFiltered
}

/// <summary>
/// Content safety ratings returned by moderation filters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiSafetyRating
{
    Safe,
    Warning,
    Flagged,
    Blocked,
    Unknown
}

/// <summary>
/// Rationale for manual or automated AI model parameter call overrides.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiCallOverrideReason
{
    OperatorChoice,
    QualityDegradation,
    CostOptimization,
    RateLimitFallback,
    Failover,
    Testing
}

/// <summary>
/// Data types for AI model input parameters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelParameterType
{
    Float,
    Integer,
    String,
    Boolean,
    EnumValue,
    JsonArray,
    JsonObject
}

/// <summary>
/// Chunk payload kinds for streaming response endpoints.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreamingChunkType
{
    TextDelta,
    AudioBytes,
    ProgressStatus,
    Metadata,
    ErrorDelta,
    FinalSummary
}

/// <summary>
/// Reason kinds for AI model completion termination.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinishReasonKind
{
    Stop,
    Length,
    ContentFilter,
    ToolCalls,
    Error,
    Cancelled
}

/// <summary>
/// Token consumption metric classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TokenCountKind
{
    InputPrompt,
    OutputCompletion,
    CacheRead,
    CacheWrite,
    Total
}

/// <summary>
/// Model architectural family classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelFamilyKind
{
    Grok,
    Claude,
    Gpt,
    Gemini,
    Llama,
    Flux,
    Veo,
    Suno,
    Midjourney
}

/// <summary>
/// Performance and capability tier levels for model selection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelTierLevel
{
    Free,
    Standard,
    Pro,
    Enterprise,
    Ultra,
    Custom
}

/// <summary>
/// Billing structure modes for AI provider API usage.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelBillingMode
{
    PayAsYouGo,
    Subscription,
    Credits,
    FreeTier,
    FlatRate
}

/// <summary>
/// Configuration and validation status of provider API keys.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderKeyStatus
{
    Unconfigured,
    Valid,
    Invalid,
    Expired,
    RateLimited,
    QuotaExceeded
}

/// <summary>
/// Capability feature flags exposed by AI models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelFeatureFlag
{
    SupportsStreaming,
    SupportsVision,
    SupportsFunctionCalling,
    SupportsSystemPrompt,
    SupportsReferenceImage,
    SupportsNegativePrompt,
    SupportsSeed
}

/// <summary>
/// Sorting criteria for model catalog displays.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelCatalogSortBy
{
    DisplayName,
    Provider,
    Capability,
    Cost,
    Latency,
    ReleaseDate,
    Popularity
}

/// <summary>
/// Filter options for model enablement states in catalog views.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelFilterByState
{
    All,
    EnabledOnly,
    DisabledOnly,
    ExperimentalOnly,
    DeprecatedOnly
}

/// <summary>
/// Retry algorithms employed by AI model clients on network error.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelClientRetryMode
{
    None,
    Linear,
    Exponential,
    Immediate,
    Jittered
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods and string parsers for AI model provider enums.
/// </summary>
public static class AiModelProviderEnumExtensions
{
    public static string ToApiString(this AiProviderId val) => val switch
    {
        AiProviderId.AiMusicApi => "aimusicapi",
        AiProviderId.OpenAi => "openai",
        AiProviderId.ElevenLabs => "elevenlabs",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelCapabilityCategory val) => val switch
    {
        ModelCapabilityCategory.TextGeneration => "text_generation",
        ModelCapabilityCategory.ImageGeneration => "image_generation",
        ModelCapabilityCategory.VideoGeneration => "video_generation",
        ModelCapabilityCategory.AudioGeneration => "audio_generation",
        ModelCapabilityCategory.VoiceCloning => "voice_cloning",
        ModelCapabilityCategory.SpeechToText => "speech_to_text",
        ModelCapabilityCategory.VisionAnalysis => "vision_analysis",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelPricingUnit val) => val switch
    {
        ModelPricingUnit.Per1kTokens => "per_1k_tokens",
        ModelPricingUnit.Per1mTokens => "per_1m_tokens",
        ModelPricingUnit.PerImage => "per_image",
        ModelPricingUnit.PerSecondVideo => "per_second_video",
        ModelPricingUnit.PerMinuteAudio => "per_minute_audio",
        ModelPricingUnit.PerRequest => "per_request",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelConcurrencyLimitBehavior val) => val switch
    {
        ModelConcurrencyLimitBehavior.RetryWithBackoff => "retry_with_backoff",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelEndpointType val) => val switch
    {
        ModelEndpointType.HttpJson => "http_json",
        ModelEndpointType.HttpStreaming => "http_streaming",
        ModelEndpointType.LocalProcess => "local_process",
        ModelEndpointType.SdkNative => "sdk_native",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelResponseStatus val) => val switch
    {
        ModelResponseStatus.RateLimited => "rate_limited",
        ModelResponseStatus.QuotaExceeded => "quota_exceeded",
        ModelResponseStatus.ServerError => "server_error",
        ModelResponseStatus.InvalidRequest => "invalid_request",
        ModelResponseStatus.ContentFiltered => "content_filtered",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this AiSafetyRating val) => val.ToString().ToLowerInvariant();
    public static string ToApiString(this AiCallOverrideReason val) => val switch
    {
        AiCallOverrideReason.OperatorChoice => "operator_choice",
        AiCallOverrideReason.QualityDegradation => "quality_degradation",
        AiCallOverrideReason.CostOptimization => "cost_optimization",
        AiCallOverrideReason.RateLimitFallback => "rate_limit_fallback",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelParameterType val) => val switch
    {
        ModelParameterType.EnumValue => "enum_value",
        ModelParameterType.JsonArray => "json_array",
        ModelParameterType.JsonObject => "json_object",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this StreamingChunkType val) => val switch
    {
        StreamingChunkType.TextDelta => "text_delta",
        StreamingChunkType.AudioBytes => "audio_bytes",
        StreamingChunkType.ProgressStatus => "progress_status",
        StreamingChunkType.ErrorDelta => "error_delta",
        StreamingChunkType.FinalSummary => "final_summary",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this FinishReasonKind val) => val switch
    {
        FinishReasonKind.ContentFilter => "content_filter",
        FinishReasonKind.ToolCalls => "tool_calls",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this TokenCountKind val) => val switch
    {
        TokenCountKind.InputPrompt => "input_prompt",
        TokenCountKind.OutputCompletion => "output_completion",
        TokenCountKind.CacheRead => "cache_read",
        TokenCountKind.CacheWrite => "cache_write",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelFamilyKind val) => val.ToString().ToLowerInvariant();
    public static string ToApiString(this ModelTierLevel val) => val.ToString().ToLowerInvariant();
    public static string ToApiString(this ModelBillingMode val) => val switch
    {
        ModelBillingMode.PayAsYouGo => "pay_as_you_go",
        ModelBillingMode.FreeTier => "free_tier",
        ModelBillingMode.FlatRate => "flat_rate",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ProviderKeyStatus val) => val switch
    {
        ProviderKeyStatus.RateLimited => "rate_limited",
        ProviderKeyStatus.QuotaExceeded => "quota_exceeded",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelFeatureFlag val) => val switch
    {
        ModelFeatureFlag.SupportsStreaming => "supports_streaming",
        ModelFeatureFlag.SupportsVision => "supports_vision",
        ModelFeatureFlag.SupportsFunctionCalling => "supports_function_calling",
        ModelFeatureFlag.SupportsSystemPrompt => "supports_system_prompt",
        ModelFeatureFlag.SupportsReferenceImage => "supports_reference_image",
        ModelFeatureFlag.SupportsNegativePrompt => "supports_negative_prompt",
        ModelFeatureFlag.SupportsSeed => "supports_seed",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelCatalogSortBy val) => val switch
    {
        ModelCatalogSortBy.DisplayName => "display_name",
        ModelCatalogSortBy.ReleaseDate => "release_date",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelFilterByState val) => val switch
    {
        ModelFilterByState.EnabledOnly => "enabled_only",
        ModelFilterByState.DisabledOnly => "disabled_only",
        ModelFilterByState.ExperimentalOnly => "experimental_only",
        ModelFilterByState.DeprecatedOnly => "deprecated_only",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this ModelClientRetryMode val) => val.ToString().ToLowerInvariant();

    public static AiProviderId ParseAiProviderId(string? s, AiProviderId defaultValue = AiProviderId.Grok) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AiProviderId>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AiProviderId ToAiProviderId(this string? s, AiProviderId defaultValue = AiProviderId.Grok) => ParseAiProviderId(s, defaultValue);

    public static ModelCapabilityCategory ParseModelCapabilityCategory(string? s, ModelCapabilityCategory defaultValue = ModelCapabilityCategory.TextGeneration) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelCapabilityCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelCapabilityCategory ToModelCapabilityCategory(this string? s, ModelCapabilityCategory defaultValue = ModelCapabilityCategory.TextGeneration) => ParseModelCapabilityCategory(s, defaultValue);

    public static ModelPricingUnit ParseModelPricingUnit(string? s, ModelPricingUnit defaultValue = ModelPricingUnit.PerRequest) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelPricingUnit>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelPricingUnit ToModelPricingUnit(this string? s, ModelPricingUnit defaultValue = ModelPricingUnit.PerRequest) => ParseModelPricingUnit(s, defaultValue);

    public static ModelConcurrencyLimitBehavior ParseModelConcurrencyLimitBehavior(string? s, ModelConcurrencyLimitBehavior defaultValue = ModelConcurrencyLimitBehavior.Queue) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelConcurrencyLimitBehavior>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelConcurrencyLimitBehavior ToModelConcurrencyLimitBehavior(this string? s, ModelConcurrencyLimitBehavior defaultValue = ModelConcurrencyLimitBehavior.Queue) => ParseModelConcurrencyLimitBehavior(s, defaultValue);

    public static ModelEndpointType ParseModelEndpointType(string? s, ModelEndpointType defaultValue = ModelEndpointType.HttpJson) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelEndpointType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelEndpointType ToModelEndpointType(this string? s, ModelEndpointType defaultValue = ModelEndpointType.HttpJson) => ParseModelEndpointType(s, defaultValue);

    public static ModelResponseStatus ParseModelResponseStatus(string? s, ModelResponseStatus defaultValue = ModelResponseStatus.Success) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelResponseStatus>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelResponseStatus ToModelResponseStatus(this string? s, ModelResponseStatus defaultValue = ModelResponseStatus.Success) => ParseModelResponseStatus(s, defaultValue);

    public static AiSafetyRating ParseAiSafetyRating(string? s, AiSafetyRating defaultValue = AiSafetyRating.Safe) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AiSafetyRating>(s, true, out var r) ? r : defaultValue;
    public static AiSafetyRating ToAiSafetyRating(this string? s, AiSafetyRating defaultValue = AiSafetyRating.Safe) => ParseAiSafetyRating(s, defaultValue);

    public static AiCallOverrideReason ParseAiCallOverrideReason(string? s, AiCallOverrideReason defaultValue = AiCallOverrideReason.OperatorChoice) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AiCallOverrideReason>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AiCallOverrideReason ToAiCallOverrideReason(this string? s, AiCallOverrideReason defaultValue = AiCallOverrideReason.OperatorChoice) => ParseAiCallOverrideReason(s, defaultValue);

    public static ModelParameterType ParseModelParameterType(string? s, ModelParameterType defaultValue = ModelParameterType.String) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelParameterType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelParameterType ToModelParameterType(this string? s, ModelParameterType defaultValue = ModelParameterType.String) => ParseModelParameterType(s, defaultValue);

    public static StreamingChunkType ParseStreamingChunkType(string? s, StreamingChunkType defaultValue = StreamingChunkType.TextDelta) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<StreamingChunkType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static StreamingChunkType ToStreamingChunkType(this string? s, StreamingChunkType defaultValue = StreamingChunkType.TextDelta) => ParseStreamingChunkType(s, defaultValue);

    public static FinishReasonKind ParseFinishReasonKind(string? s, FinishReasonKind defaultValue = FinishReasonKind.Stop) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FinishReasonKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FinishReasonKind ToFinishReasonKind(this string? s, FinishReasonKind defaultValue = FinishReasonKind.Stop) => ParseFinishReasonKind(s, defaultValue);

    public static TokenCountKind ParseTokenCountKind(string? s, TokenCountKind defaultValue = TokenCountKind.Total) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<TokenCountKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static TokenCountKind ToTokenCountKind(this string? s, TokenCountKind defaultValue = TokenCountKind.Total) => ParseTokenCountKind(s, defaultValue);

    public static ModelFamilyKind ParseModelFamilyKind(string? s, ModelFamilyKind defaultValue = ModelFamilyKind.Grok) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelFamilyKind>(s, true, out var r) ? r : defaultValue;
    public static ModelFamilyKind ToModelFamilyKind(this string? s, ModelFamilyKind defaultValue = ModelFamilyKind.Grok) => ParseModelFamilyKind(s, defaultValue);

    public static ModelTierLevel ParseModelTierLevel(string? s, ModelTierLevel defaultValue = ModelTierLevel.Standard) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelTierLevel>(s, true, out var r) ? r : defaultValue;
    public static ModelTierLevel ToModelTierLevel(this string? s, ModelTierLevel defaultValue = ModelTierLevel.Standard) => ParseModelTierLevel(s, defaultValue);

    public static ModelBillingMode ParseModelBillingMode(string? s, ModelBillingMode defaultValue = ModelBillingMode.PayAsYouGo) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelBillingMode>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelBillingMode ToModelBillingMode(this string? s, ModelBillingMode defaultValue = ModelBillingMode.PayAsYouGo) => ParseModelBillingMode(s, defaultValue);

    public static ProviderKeyStatus ParseProviderKeyStatus(string? s, ProviderKeyStatus defaultValue = ProviderKeyStatus.Unconfigured) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ProviderKeyStatus>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ProviderKeyStatus ToProviderKeyStatus(this string? s, ProviderKeyStatus defaultValue = ProviderKeyStatus.Unconfigured) => ParseProviderKeyStatus(s, defaultValue);

    public static ModelFeatureFlag ParseModelFeatureFlag(string? s, ModelFeatureFlag defaultValue = ModelFeatureFlag.SupportsStreaming) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelFeatureFlag>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelFeatureFlag ToModelFeatureFlag(this string? s, ModelFeatureFlag defaultValue = ModelFeatureFlag.SupportsStreaming) => ParseModelFeatureFlag(s, defaultValue);

    public static ModelCatalogSortBy ParseModelCatalogSortBy(string? s, ModelCatalogSortBy defaultValue = ModelCatalogSortBy.DisplayName) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelCatalogSortBy>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelCatalogSortBy ToModelCatalogSortBy(this string? s, ModelCatalogSortBy defaultValue = ModelCatalogSortBy.DisplayName) => ParseModelCatalogSortBy(s, defaultValue);

    public static ModelFilterByState ParseModelFilterByState(string? s, ModelFilterByState defaultValue = ModelFilterByState.All) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelFilterByState>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ModelFilterByState ToModelFilterByState(this string? s, ModelFilterByState defaultValue = ModelFilterByState.All) => ParseModelFilterByState(s, defaultValue);

    public static ModelClientRetryMode ParseModelClientRetryMode(string? s, ModelClientRetryMode defaultValue = ModelClientRetryMode.Exponential) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ModelClientRetryMode>(s, true, out var r) ? r : defaultValue;
    public static ModelClientRetryMode ToModelClientRetryMode(this string? s, ModelClientRetryMode defaultValue = ModelClientRetryMode.Exponential) => ParseModelClientRetryMode(s, defaultValue);

}

#endregion
