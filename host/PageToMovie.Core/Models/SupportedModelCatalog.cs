namespace PageToMovie.Core.Models;

/// <summary>
/// What the model is used for in Film Studio (drives Configuration dropdowns).
/// </summary>
public enum ModelCapability
{
    Video,
    Image,
    Chat,
    Vision,
    Audio,
    /// <summary>Voice clone / TTS (e.g. ElevenLabs, Fal.ai MiniMax) — dialogue personalization, not BGM. Covers both the clone step (see <see cref="SupportedModelEntry.IsVoiceCloneStep"/>) and the text-to-speech step.</summary>
    Voice,
    /// <summary>Video lip-sync: resync a finished clip's mouth movement to a separate audio track (Fal.ai / Sync Labs). Input is video+audio, not a text prompt — kept distinct from <see cref="Video"/>.</summary>
    LipSync,
    /// <summary>Prompt-based edit of an already-generated clip (xAI's /v1/videos/edits). Input is video+text-prompt, not text-prompt(+refs) like <see cref="Video"/> generation, and not video+audio like <see cref="LipSync"/> — kept distinct.</summary>
    VideoEdit,
}

/// <summary>
/// Backend family — maps to API base URL + required env keys.
/// User never picks this; it is derived from the model id via the catalog.
/// Clients are selected by <see cref="SupportedModelEntry.Id"/> through multi-provider
/// facades (chat / image / video / vision) in PageToMovie.Engine.
/// </summary>
public enum ModelProviderFamily
{
    /// <summary>xAI (api.x.ai) — <c>XAI_API_KEY</c>. Full product path (chat, image, video, vision/OCR).</summary>
    Xai = 0,
    /// <summary>
    /// Google Gemini / Veo (<c>GEMINI_API_KEY</c>) — wired via GeminiChatClient, GeminiImageClient,
    /// GeminiVideoClient (text/image-to-video only), MultiProviderVisionClient for frame review.
    /// Book-page OCR and cast-on-image classify stay Grok-only; Veo has no clip-extend / multi-ref plates.
    /// </summary>
    Google = 1,
    /// <summary>
    /// Anthropic Claude (<c>ANTHROPIC_API_KEY</c>) — wired via AnthropicChatClient and
    /// MultiProviderVisionClient for frame review. No image generation API; OCR/cast classify stay Grok-only.
    /// </summary>
    Anthropic = 2,
    /// <summary>
    /// Fal.ai (<c>FAL_KEY</c>) — serverless open-source video/image models (HunyuanVideo).
    /// </summary>
    Fal = 3,
    /// <summary>Suno via sunoapi.org (<c>SUNO_API_KEY</c>) — unofficial third-party Suno reseller.</summary>
    Suno = 4,
    /// <summary>Suno via aimusicapi.ai (<c>AIMUSICAPI_API_KEY</c>) — a different unofficial Suno reseller.</summary>
    AiMusicApi = 5,
    /// <summary>ElevenLabs (<c>ElevenLabs_API_KEY</c>) — voice clone + TTS for personal dialogue.</summary>
    ElevenLabs = 6,
    /// <summary>OpenAI (<c>OPENAI_API_KEY</c>) — chat / planning models.</summary>
    OpenAI = 7,
    /// <summary>
    /// Fake test vendor — only present when <c>PageToMovie:UseFakes</c> is on (its models are
    /// merged in from <c>models_catalog.fake.json</c>). Backs every capability with a key-free
    /// fake client so the whole pipeline is drivable offline with full control over which
    /// capabilities are "configured". Never appears in real mode.
    /// </summary>
    Fake = 8,
}

/// <summary>
/// One supported model. Only entries with <see cref="Enabled"/> true appear in user pickers.
/// Wishlist / not-yet-wired models stay off the list and can be tracked as GitHub feature requests.
/// </summary>
public sealed class SupportedModelEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ModelCapability Capability { get; init; }
    public required ModelProviderFamily Provider { get; init; }

    /// <summary>API origin, e.g. <c>https://api.x.ai/v1</c>.</summary>
    public required string ApiBase { get; init; }

    /// <summary>
    /// Primary relative path under <see cref="ApiBase"/> (e.g. <c>videos/generations</c>).
    /// Extensions / alternate routes stay in the client; this is the capability home.
    /// </summary>
    public required string EndpointPath { get; init; }

    /// <summary>Env var names that must be set (e.g. <c>XAI_API_KEY</c>).</summary>
    public required IReadOnlyList<string> RequiredEnvKeys { get; init; }

    /// <summary>Where API auth credentials are placed (Bearer, Header, Query).</summary>
    public ApiAuthLocation AuthLocation { get; init; } = ApiAuthLocation.Bearer;

    /// <summary>Retry backoff strategy (Linear, Exponential, Quadratic).</summary>
    public RetryBackoffKind BackoffKind { get; init; } = RetryBackoffKind.Quadratic;

    /// <summary>When false, hidden from Configuration pickers.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>When true, this model is deprecated: hidden from standard catalog pickers and ignored by automated update scans.</summary>
    public bool Deprecated { get; init; }

    /// <summary>
    /// Context window (max input tokens), for callers that need to budget large prompts against
    /// the actual model — e.g. book-to-screenplay chunking. Null for models where this isn't a
    /// meaningful concept (video/image) or isn't verified yet. Sourced from provider docs as of
    /// 2026-07; providers do increase these over time, so re-check before trusting an old number
    /// for a cost/quality-sensitive decision.
    /// </summary>
    public int? MaxInputTokens { get; init; }

    /// <summary>
    /// Maximum output tokens the model will generate in a single synchronous Messages API call
    /// (Chat / Vision only), i.e. the real per-model ceiling for the <c>max_tokens</c> request
    /// field. Null when not applicable or not yet confirmed against provider docs — callers must
    /// fall back to their own hardcoded default rather than guess. Sourced from provider docs;
    /// providers do raise these over time, so re-check before trusting an old number for a
    /// cost/quality-sensitive decision.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>USD per 1,000,000 input tokens (Chat / Vision only). Null when not applicable.</summary>
    public double? InputCostPerMillionTokens { get; init; }

    /// <summary>USD per 1,000,000 output tokens (Chat / Vision only). Null when not applicable.</summary>
    public double? OutputCostPerMillionTokens { get; init; }

    /// <summary>
    /// USD per second of generated output, by resolution (Video only) — same key convention
    /// ("480p" / "720p" / "1080p") as the project-level <c>cost_estimates.video_output_per_sec</c>
    /// table in Configuration. That table is an operator-editable planning estimate for whichever
    /// video model is active; this is the catalog's own reference price per model, and a given
    /// model may not price every resolution (only confirmed keys are present). Null when no
    /// per-resolution pricing applies (non-video capabilities).
    /// </summary>
    public IReadOnlyDictionary<string, double>? VideoCostPerSecondByResolution { get; init; }

    /// <summary>
    /// USD charged once per video regardless of length, by resolution (Video only) — same key
    /// convention as <see cref="VideoCostPerSecondByResolution"/>. Total cost is
    /// <c>base + perSecond * durationSeconds</c>, so a provider that bills a flat fee per clip
    /// (e.g. Fal's Hunyuan/Wan, which price per generation, not per second, since they're
    /// frame-count-based) sets this and leaves the per-second rate at 0 — a genuinely
    /// duration-priced provider (Grok, Veo) leaves this null/0 and only sets the per-second rate,
    /// so existing behavior is unchanged for them. Modeling flat fees as a fake per-second rate
    /// (dividing by an assumed "typical" duration) silently produces the wrong number the moment a
    /// clip's actual duration differs from that assumption — this field avoids the whole problem
    /// by representing the real billing shape directly.
    /// </summary>
    public IReadOnlyDictionary<string, double>? VideoBaseCostByResolution { get; init; }

    /// <summary>USD per generated image (Image only). Null when not applicable.</summary>
    public double? ImageCostPerImage { get; init; }

    /// <summary>
    /// USD per reference/character image attached to a video generation call (Video only), when
    /// the vendor publishes this as a distinct line item separate from the flat per-second output
    /// rate. Null when not published/verified — <c>PageToMovie.Engine.CostReportService</c> then
    /// applies a small estimated fallback constant instead. As of 2026-08 no enabled video provider
    /// (including xAI's grok-imagine-video, checked against docs.x.ai/developers/pricing) itemizes
    /// reference images separately, so this is null for every catalog entry today.
    /// </summary>
    public double? VideoReferenceImageCost { get; init; }

    /// <summary>
    /// USD per second billed for a video-extend/continuation call (Video only; only meaningful when
    /// <see cref="SupportsVideoContinue"/> is true), when the vendor publishes an extend rate
    /// distinct from its base per-second generation rate. Null when not published/verified —
    /// <c>PageToMovie.Engine.CostReportService</c> then applies a small estimated fallback constant
    /// instead. As of 2026-08 xAI (the only enabled provider with <see cref="SupportsVideoContinue"/>
    /// true) has no published extend-specific rate on docs.x.ai/developers/pricing — grok-imagine-video
    /// is a flat $0.050/sec with no separate extend line item — so this is null for every catalog
    /// entry today.
    /// </summary>
    public double? VideoExtendCostPerSecond { get; init; }

    /// <summary>
    /// Free-text citation for cost fields (URL + date + what was published). Not used in math —
    /// keeps vendor price sources out of Engine constants so updates are catalog-only.
    /// </summary>
    public string? PricingNotes { get; init; }

    /// <summary>
    /// ISO date (yyyy-MM-dd) when cost fields on this row were <b>last reviewed</b> against
    /// the vendor price list (not merely last edited in git). Use this to audit how often
    /// cost data is re-checked. Not used in math.
    /// </summary>
    public string? PricingLastReviewedAt { get; init; }

    /// <summary>
    /// ISO date (yyyy-MM-dd) when this model row was last <b>reviewed</b> for complete required
    /// capability fields (self-test). Distinct from <see cref="PricingLastReviewedAt"/> (cost review).
    /// </summary>
    public string? LastVerifiedAt { get; init; }

    /// <summary>
    /// When true, this row is experimental: self-test only requires id/capability/provider and
    /// non-empty <see cref="LabNotes"/>. Missing limits/costs do not fail catalog load; runtime
    /// still fails on the specific field needed for a call. Cost UI must not invent USD.
    /// </summary>
    public bool LabMode { get; init; }

    /// <summary>Required when <see cref="LabMode"/> is true — why this model is incomplete.</summary>
    public string? LabNotes { get; init; }

    public string? Notes { get; init; }

    /// <summary>
    /// Optional link to a GitHub issue / feature request for models we plan to support.
    /// Prefer leaving unsupported models out of the enabled list and tracking them on GitHub.
    /// </summary>
    public string? FeatureRequestUrl { get; init; }

    /// <summary>
    /// When true (default for Grok Imagine Video), clip 2+ can continue via video-extend.
    /// False for providers that only support text/image-to-video (e.g. Veo today).
    /// </summary>
    public bool SupportsVideoContinue { get; init; } = true;

    /// <summary>
    /// When true, locked character reference plates can be attached on fresh gen.
    /// False for backends that reject multi-image / reference conditioning.
    /// </summary>
    public bool SupportsReferenceImages { get; init; } = true;

    /// <summary>
    /// Real max reference images this model's API accepts (Video only) — a plain
    /// <see cref="SupportsReferenceImages"/> boolean isn't precise enough: Fal's Wan/HunyuanVideo
    /// accept exactly one init/reference image (true single-image i2v, not Grok-style multi-plate
    /// identity conditioning), so treating "supports" as "accepts up to N" would silently attach
    /// more images than the model can actually use. Null falls back to whatever the caller's own
    /// historical default was (today 7 for Grok in <c>GrokVideoClient</c>, 5 at the
    /// <c>FilmJobService</c> call site) until every model has a confirmed real number here.
    /// </summary>
    public int? MaxReferenceImages { get; init; }

    /// <summary>
    /// When true, accepts native MP4 video & audio files directly for clip/dialogue review (Google Gemini).
    /// </summary>
    public bool SupportsVideoReview { get; init; } = false;

    /// <summary>
    /// Shortest clip this model should be asked to generate (Video only). Null falls back to
    /// <see cref="PageToMovie.Engine.ClipDurationEstimator.MinSeconds"/> in <c>ClipDurationEstimator</c>.
    /// </summary>
    public int? MinClipDurationSeconds { get; init; }

    /// <summary>
    /// Soft cap for a single clip (Video only) — the duration/dialogue budget planner should split
    /// rather than exceed this. Null falls back to <c>ClipDurationEstimator.MaxSeconds</c>.
    /// </summary>
    public int? MaxClipDurationSeconds { get; init; }

    /// <summary>
    /// Absolute ceiling for a single clip even for big-action beats (Video only). Null falls back to
    /// <c>ClipDurationEstimator.AbsMaxSeconds</c>. Values below are today's known-safe defaults, not
    /// necessarily each provider's real published limit — confirm against provider docs before relying
    /// on a per-model number for a cost/quality-sensitive decision.
    /// </summary>
    public int? AbsMaxClipDurationSeconds { get; init; }

    /// <summary>
    /// Video only: how many characters may SPEAK in a single generated clip. Current models render a
    /// two-person exchange at best (often best at one speaker per clip — see Grok), and reliably
    /// lip-syncing three distinct speakers in one shot is not yet feasible. The shot planner uses this
    /// to decide whether to coalesce adjacent different-speaker beats into a two-hander (>=2) or keep
    /// one speaker per clip / shot-reverse-shot (1). Absent → <see cref="MaxSpeakersPerClipOrDefault"/>
    /// falls back to 1 (the safe, always-renderable choice). Raise it per model as video models improve.
    /// </summary>
    public int? MaxSpeakersPerClip { get; init; }

    /// <summary>Effective speakers-per-clip cap: the catalog value when a positive one is set, else 1.</summary>
    public int MaxSpeakersPerClipOrDefault => MaxSpeakersPerClip is > 0 ? MaxSpeakersPerClip.Value : 1;

    /// <summary>
    /// Discrete set of durations this model accepts (Video only) — e.g. Veo 3.1 documents exactly
    /// 4, 6, or 8 seconds, not an arbitrary continuous range. When set, generation-time duration
    /// resolution must snap to the nearest value here rather than a plain min/max clamp (a clamped
    /// "7" is still not an accepted Veo duration). Null means the model accepts any value in
    /// [MinClipDurationSeconds, MaxClipDurationSeconds] (or the global defaults).
    /// </summary>
    public IReadOnlyList<int>? AllowedDurationsSeconds { get; init; }

    /// <summary>
    /// Tighter max duration specifically for image-to-video / reference-conditioned / video-extend
    /// generation (Video only) — some providers (Grok) allow a longer fresh text-to-video clip than
    /// they do for the "new portion" of a reference/continue call. Null means image/ref/continue
    /// modes use the same <see cref="MaxClipDurationSeconds"/> as fresh generation.
    /// </summary>
    public int? MaxExtensionSeconds { get; init; }

    /// <summary>
    /// Longest input clip this model's video-edit endpoint accepts, in seconds (VideoEdit only) —
    /// e.g. xAI's <c>/v1/videos/edits</c> caps input at 8.7s and always returns output at the same
    /// duration/resolution as the input (not independently configurable). Drives the Scenes page's
    /// per-clip "AI Edit" button eligibility check — never hardcode the limit in code.
    /// </summary>
    public double? MaxEditInputDurationSeconds { get; init; }

    /// <summary>
    /// Longest single-call duration this audio model will accept, in seconds (Audio only) — the
    /// generation-side counterpart to <see cref="MaxClipDurationSeconds"/> for video. Callers
    /// (FilmJobService's music job) generate this many seconds per segment and concatenate
    /// client-side for anything longer. Null when the provider doesn't document/enforce a duration
    /// control at all (the caller then treats one call as "whatever length comes back").
    /// </summary>
    public int? MaxAudioDurationSeconds { get; init; }

    /// <summary>
    /// When true, this Audio model can generate sung vocals (not instrumental-only).
    /// Catalog-driven — do not infer from provider id (suno/fal/etc.).
    /// </summary>
    public bool SupportsVocals { get; init; }

    /// <summary>
    /// Maximum character length for visual prompts passed to the API (Video/Image models).
    /// Null defaults to 4096 (Grok's budget). Models with tighter limits (e.g. Fal.ai / HunyuanVideo max 1000)
    /// specify their limit here so prompt builders automatically trim to fit without API 400 errors.
    /// </summary>
    public int? MaxPromptLength { get; init; }

    /// <summary>
    /// Maximum bounding dimension (in pixels) for reference images sent to the API.
    /// Null defaults to 1280px (optimal for HunyuanVideo / Veo 720p latent dimensions).
    /// </summary>
    public int? MaxReferenceImageDimension { get; init; }

    /// <summary>
    /// Diffusion sampling steps (Video/Image only) — the quality/speed knob Fal-hosted diffusion
    /// models expose as <c>num_inference_steps</c>. Null falls back to the calling client's own
    /// hardcoded default (30 in <c>FalVideoClient</c>).
    /// </summary>
    public int? NumInferenceSteps { get; init; }

    /// <summary>
    /// For Fal video models whose real generation API takes a discrete <c>num_frames</c> value
    /// rather than a continuous seconds-based duration (see the frame-count-native note on the
    /// <c>hunyuan-video</c> catalog entry) — the frame count <c>FalVideoClient</c> requests for
    /// clips at or under its short/long duration split (4s). Null falls back to the client's
    /// hardcoded default (85). Duration-native Fal models (e.g. Wan-2.1, which instead populate
    /// <see cref="MinClipDurationSeconds"/>/<see cref="MaxClipDurationSeconds"/>) leave this null.
    /// </summary>
    public int? ShortClipFrameCount { get; init; }

    /// <summary>
    /// Frame count <c>FalVideoClient</c> requests for clips over its short/long duration split
    /// (4s) — the counterpart to <see cref="ShortClipFrameCount"/>. Null falls back to the
    /// client's hardcoded default (129).
    /// </summary>
    public int? LongClipFrameCount { get; init; }

    /// <summary>
    /// Discrete set of aspect-ratio strings (e.g. <c>"16:9"</c>, <c>"9:16"</c>) this model's API
    /// actually accepts for generation (Video only), sourced from provider docs — mirrors
    /// <see cref="MaxReferenceImageDimension"/>-style per-model capability data rather than a
    /// continuous range, since providers document aspect ratio as a fixed enum. Null when not yet
    /// confirmed for this model; callers should keep sending their historical fixed value in that
    /// case rather than guessing.
    /// </summary>
    public IReadOnlyList<string>? SupportedAspectRatios { get; init; }

    /// <summary>
    /// Aspect ratio to request when the caller doesn't have a more specific one in mind (Video
    /// only). Should be a member of <see cref="SupportedAspectRatios"/> when both are set. Null
    /// falls back to the client's historical hardcoded default (<c>"16:9"</c>).
    /// </summary>
    public string? DefaultAspectRatio { get; init; }

    /// <summary>
    /// True for a clone-shaped Voice model (takes a reference audio sample, returns a provider voice
    /// id — e.g. Fal.ai <c>fal-ai/minimax/voice-clone</c>, ElevenLabs <c>eleven_voice_clone</c>).
    /// False (default) means a speak-shaped Voice model (takes text + a voice id/name, returns
    /// synthesized speech audio — e.g. <c>fal-ai/minimax/speech-02-hd</c>, <c>eleven_multilingual_v2</c>).
    /// Both shapes share <see cref="ModelCapability.Voice"/> since a caller resolving "the voice
    /// model" for a narration flow needs one clone-shaped and one speak-shaped entry, not two
    /// separate capability buckets — this flag disambiguates which is which. Only meaningful when
    /// <see cref="Capability"/> is <see cref="ModelCapability.Voice"/>.
    /// </summary>
    public bool IsVoiceCloneStep { get; init; }

    /// <summary>USD per voice-clone call (Voice, clone-shaped models only — see <see cref="IsVoiceCloneStep"/>). Null when not applicable/unconfirmed.</summary>
    public double? CostPerCloneUsd { get; init; }

    /// <summary>USD per 1,000 characters of synthesized speech (Voice, speak-shaped models only). Null when not applicable/unconfirmed.</summary>
    public double? CostPerThousandCharsUsd { get; init; }

    /// <summary>USD per minute of output video (LipSync only, flat rate — Sync Labs-style lip-sync models aren't priced per resolution like <see cref="VideoCostPerSecondByResolution"/>). Null when not applicable/unconfirmed.</summary>
    public double? CostPerMinuteUsd { get; init; }

    /// <summary>Raw provider string from models_catalog.json (e.g. OpenAI, Xai, Google).</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>
    /// Stable key-slot id from catalog <c>providers[].id</c> / model <c>providerId</c>
    /// (e.g. <c>grok</c>, <c>gemini</c>, <c>suno</c>). Not invented at runtime.
    /// </summary>
    public string ProviderId { get; init; } = "";

    /// <summary>UI label from catalog <c>providers[].label</c> / model <c>providerLabel</c> (e.g. xAI, Suno API).</summary>
    public string ProviderLabel { get; init; } = "";
}

/// <summary>
/// Master list of models Film Studio knows how to call.
/// User picks <see cref="SupportedModelEntry.Id"/> only; app resolves endpoint + keys.
/// </summary>
public static class SupportedModelCatalog
{
    public const string XaiApiBase = "https://api.x.ai/v1";
    public const string XaiApiKeyEnv = "XAI_API_KEY";
    public const string GoogleApiBase = "https://generativelanguage.googleapis.com/v1beta";
    public const string GoogleApiKeyEnv = "GEMINI_API_KEY";
    public const string AnthropicApiBase = "https://api.anthropic.com/v1";
    public const string AnthropicApiKeyEnv = "ANTHROPIC_API_KEY";
    public const string FalApiBase = "https://queue.fal.run";
    public const string FalApiKeyEnv = "FAL_API_KEY";
    public const string FalApiKeyFallbackEnv = "FAL_KEY";
    /// <summary>Unofficial Suno reseller — no official public Suno API exists as of 2026-07.</summary>
    public const string SunoApiBase = "https://api.sunoapi.org/api/v1";
    public const string SunoApiKeyEnv = "SUNO_API_KEY";
    /// <summary>A different unofficial Suno reseller (formerly reached via the sunoapi.com redirect).</summary>
    public const string AiMusicApiBase = "https://api.aimusicapi.ai/api/v1";
    public const string AiMusicApiKeyEnv = "AIMUSICAPI_API_KEY";
    public const string ElevenLabsApiKeyEnv = "ElevenLabs_API_KEY";
    public const string VideoReviewCapabilityId = "video-review";
    private const string MaxPromptLengthField = "maxPromptLength";
    public const string ElevenLabsApiBase = "https://api.elevenlabs.io/v1";

    private static List<SupportedModelEntry>? _loadedEntries;
    private static readonly object CatalogSync = new();
    private static List<CatalogProviderDefinition>? _loadedProviders;

    private static IReadOnlyList<ModelCapabilityDefinition>? _loadedCapabilities;

    /// <summary>Dynamic list of capabilities registered in models_catalog.json (or defaults).</summary>
    public static IReadOnlyList<ModelCapabilityDefinition> RegisteredCapabilities
    {
        get
        {
            if (_loadedCapabilities is null)
            {
                EnsureLoaded();
            }
            // Catalog JSON is SSoT — no hard-coded capability list when file had none.
            return _loadedCapabilities ?? Array.Empty<ModelCapabilityDefinition>();
        }
    }

    public static readonly IReadOnlyList<ModelCapabilityDefinition> DefaultCapabilityDefinitions =
    [
        new() { Id = "video", DisplayName = "Video Generation", Description = "Generates MP4 video clips from prompts and character reference plates.", Order = 1 },
        new() { Id = "image", DisplayName = "Portrait / Image Generation", Description = "Creates character reference portraits and book plate graphics.", Order = 2 },
        new() { Id = "chat", DisplayName = "Script & Planning", Description = "Screenplay reasoning, shot planning, and cast analysis.", Order = 3 },
        new() { Id = "vision", DisplayName = "Image Vision & OCR", Description = "Book page OCR, cast-on-image classification, and frame inspection.", Order = 4 },
        new() { Id = VideoReviewCapabilityId, DisplayName = "Video & Clip Review (Multimodal)", Description = "Evaluates dialogue, lip sync, and scene rhythm (Google Gemini natively analyzes MP4 video files).", Order = 5 },
        new() { Id = "audio", DisplayName = "Audio & Music Generation", Description = "Generates beat-aligned background music scores and sound effects.", Order = 6 },
        new() { Id = "voice", DisplayName = "Voice Clone / TTS", Description = "Personal voice clone from a short sample, then spoken dialogue or narration for the film.", Order = 70 },
        new() { Id = "lipsync", DisplayName = "Video Lip-Sync", Description = "Resyncs a generated clip's mouth movement to a separate dialogue or narration audio track.", Order = 80 },
        new() { Id = "video-edit", DisplayName = "Video Clip Edit", Description = "Prompt-based edit of an already-generated clip (re-render an action, color, or detail from a text instruction).", Order = 85 },
    ];

    private static Dictionary<string, List<string>>? _loadedTaskRankings;

    public static IReadOnlyDictionary<string, List<string>> TaskRankings
    {
        get
        {
            EnsureLoaded();
            return _loadedTaskRankings ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>All catalog rows, from the embedded catalog (real, or fake in fakes mode — see
    /// <see cref="EmbeddedCatalogResourceName"/>). Throws via <see cref="EnsureLoaded"/> if the
    /// embedded catalog is missing or fails its self-test.</summary>
    public static IReadOnlyList<SupportedModelEntry> Entries
    {
        get
        {
            lock (CatalogSync)
            {
                EnsureLoaded();
                return _loadedEntries
                    ?? throw new InvalidOperationException("The models catalog did not produce a model list.");
            }
        }
    }

    // ── Single source of truth ────────────────────────────────────────────────────────────────
    // The models catalog is EMBEDDED in PageToMovie.Core (see the csproj EmbeddedResource items):
    //   • real mode  → config/models_catalog.json
    //   • fakes mode → config/models_catalog.fake.json (a standalone one-time copy + the fake vendor)
    // Those are the ONLY two ways to load a catalog. There is no /data override, no on-disk file, and
    // no fallback chain — a real server can never reach the fake catalog. The catalog is code: edit it
    // in git and rebuild. This removes the old multi-location resolution whose silent degradation made
    // "which catalog is actually loaded?" ambiguous.
    private const string RealCatalogResource = "PageToMovie.Core.config.models_catalog.json";
    private const string FakeCatalogResource = "PageToMovie.Core.config.models_fake_catalog.json";

    /// <summary>Logical name of the embedded catalog this process must use (fake only in fakes mode).</summary>
    private static string EmbeddedCatalogResourceName =>
        FakeCatalogEnabled() ? FakeCatalogResource : RealCatalogResource;

    /// <summary>Human-readable source label for admin/diagnostics — the catalog is embedded, not a file.</summary>
    public static string GetCatalogSourceLabel() => "embedded:" + EmbeddedCatalogResourceName;

    /// <summary>Raw JSON of the embedded catalog this process uses (real, or fake in fakes mode) —
    /// for admin display and the WASM catalog-json hydration endpoint. Never a filesystem read.</summary>
    public static string GetEmbeddedCatalogJson()
    {
        var resource = EmbeddedCatalogResourceName;
        var asm = typeof(SupportedModelCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded models catalog resource '{resource}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// True when <paramref name="rawJson"/> is structurally usable by <see cref="EnsureLoaded"/> —
    /// either an object with a non-empty "models" array, or a bare non-empty array of model entries.
    /// Pulled out of <see cref="SaveCatalogJson"/> so it's testable without touching the filesystem.
    /// </summary>
    public static bool IsUsableCatalogJson(string rawJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Object => root.TryGetProperty("models", out var modelsEl) &&
                    modelsEl.ValueKind == System.Text.Json.JsonValueKind.Array && modelsEl.GetArrayLength() > 0,
                System.Text.Json.JsonValueKind.Array => root.GetArrayLength() > 0,
                _ => false,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runtime catalog edits are no longer supported: the catalog is embedded in PageToMovie.Core
    /// (config/models_catalog.json) and is the single source of truth. Change it in git and rebuild.
    /// Kept so callers fail loudly instead of silently writing a file the server would never load.
    /// </summary>
    public static void SaveCatalogJson(string rawJson) =>
        throw new NotSupportedException(
            "The models catalog is embedded at build time (PageToMovie.Core/config/models_catalog.json) " +
            "and cannot be edited at runtime. Edit the JSON in git and redeploy.");

    /// <summary>Reset and reload the embedded catalog (e.g. after fakes mode is toggled in a test).</summary>
    public static void ReloadCatalog()
    {
        lock (CatalogSync)
        {
            _loadedEntries = null;
            _loadedProviders = null;
            _loadedCapabilities = null;
            _loadedTaskRankings = null;
            EnsureLoaded();
        }
    }

    /// <summary>Parse catalog JSON into static fields. Returns true on success.</summary>
    public static bool TryLoadFromJson(string json)
    {
        lock (CatalogSync)
            return TryLoadFromJsonCore(json);
    }

    private static bool TryLoadFromJsonCore(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var container = System.Text.Json.JsonSerializer.Deserialize<ModelCatalogContainerDto>(json, opts);
                if (container?.Models is { Count: > 0 })
                {
                    _loadedProviders = (container.Providers is { Count: > 0 }
                        ? container.Providers
                        : InferProvidersFromModels(container.Models))
                        .Select(NormalizeProviderDef)
                        .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                        .ToList();
                    _loadedEntries = container.Models.Select(FromDto).ToList();
                    if (container.Capabilities is { Count: > 0 })
                    {
                        _loadedCapabilities = container.Capabilities.Select(c => new ModelCapabilityDefinition
                        {
                            Id = c.Id,
                            DisplayName = c.DisplayName,
                            Description = c.Description,
                            Order = c.Order,
                            DefaultModelId = c.DefaultModelId,
                        }).ToList();
                    }
                    else
                    {
                        _loadedCapabilities = new List<ModelCapabilityDefinition>();
                    }

                    _loadedTaskRankings = LoadTaskRankings(container, doc.RootElement);
                    return true;
                }
            }
            else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var dtos = System.Text.Json.JsonSerializer.Deserialize<List<SupportedModelDto>>(json, opts);
                if (dtos is { Count: > 0 })
                {
                    _loadedProviders = InferProvidersFromModels(dtos)
                        .Select(NormalizeProviderDef)
                        .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                        .ToList();
                    _loadedEntries = dtos.Select(FromDto).ToList();
                    _loadedCapabilities = new List<ModelCapabilityDefinition>();
                    _loadedTaskRankings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    return true;
                }
            }
        }
        catch
        {
            // leave unloaded
        }
        return false;
    }

    /// <summary>
    /// True when the standalone fake test-vendor catalog should be loaded instead of the real one —
    /// i.e. the host is running with <c>PageToMovie:UseFakes</c> on. Read from environment (Core has
    /// no config access); the Api sets <c>PageToMovie__UseFakes</c>/<c>PAGETOMOVIE_USE_FAKES</c> when
    /// fakes are on. Always false in the WASM browser (no env) — the browser loads the real embedded
    /// catalog, then hydrates from the Api's /api/models/catalog-json, which serves the fake catalog
    /// file on a fakes host.
    /// </summary>
    public static bool FakeCatalogEnabled()
    {
        if (OperatingSystem.IsBrowser()) return false;
        foreach (var name in new[] { "PageToMovie__UseFakes", "PageToMovie_USE_FAKES", "PAGETOMOVIE_USE_FAKES" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v) &&
                (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Prefer DTO <c>taskRankings</c>; also accept snake_case <c>task_rankings</c> from models_catalog.json.
    /// Never falls back to C# hard-coded rankings.
    /// </summary>
    private static Dictionary<string, List<string>> LoadTaskRankings(
        ModelCatalogContainerDto container,
        System.Text.Json.JsonElement root)
    {
        if (container.TaskRankings is { Count: > 0 })
            return new Dictionary<string, List<string>>(container.TaskRankings, StringComparer.OrdinalIgnoreCase);

        foreach (var name in new[] { "task_rankings", "taskRankings", "TaskRankings" })
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                    el.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed is { Count: > 0 })
                    return new Dictionary<string, List<string>>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // try next key
            }
        }

        if (_loadedTaskRankings is { Count: > 0 })
            return new Dictionary<string, List<string>>(_loadedTaskRankings, StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the browser (or any host) has successfully loaded a catalog into memory.</summary>
    public static bool IsLoaded => _loadedEntries is { Count: > 0 };

    private static void EnsureLoaded()
    {
        if (_loadedEntries is not null && _loadedCapabilities is not null) return;
        lock (CatalogSync)
        {
            if (_loadedEntries is not null && _loadedCapabilities is not null) return;

            // Load the ONE embedded catalog for this process (real, or fake in fakes mode). No files, no
        // /data, no fallback chain. Fail fast if it is missing or invalid rather than silently
        // degrading to a different catalog — the previous multi-candidate loader hid exactly that.
        var resource = EmbeddedCatalogResourceName;
        try
        {
            var asm = typeof(SupportedModelCatalog).Assembly;
            using var stream = asm.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"Embedded models catalog resource '{resource}' not found in {asm.GetName().Name}. " +
                    "It is embedded at build time from PageToMovie.Core/config — check the csproj EmbeddedResource item.");
            using var reader = new StreamReader(stream);
            if (!TryLoadFromJson(reader.ReadToEnd()))
                throw new InvalidOperationException(
                    $"Embedded models catalog '{resource}' is not usable (expected a non-empty \"models\" array).");

            // Self-test required fields before any pipeline use. Server hosts fail fast; the browser
            // skips this (it re-hydrates the full catalog from the API's /api/models/catalog-json).
            if (!OperatingSystem.IsBrowser())
                EnsureEnabledModelsComplete();
        }
        catch when (OperatingSystem.IsBrowser())
        {
            // Browser only: never brick the WASM UI — soft-load an empty shell; LoadCatalogAsync
            // hydrates the real catalog from the API right after boot.
            _loadedEntries = new List<SupportedModelEntry>();
            _loadedProviders = new List<CatalogProviderDefinition>();
            _loadedCapabilities = new List<ModelCapabilityDefinition>();
            _loadedTaskRankings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

    public static IReadOnlyList<SupportedModelEntry> ForCapability(
        ModelCapability capability,
        bool enabledOnly = true,
        bool includeLabModels = false,
        bool includeDeprecated = false) =>
        Entries.Where(e =>
            e.Capability == capability
            && (!enabledOnly || e.Enabled)
            && (includeLabModels || !e.LabMode)
            && (includeDeprecated || !e.Deprecated)).ToList();

    public static SupportedModelEntry? Find(string? modelId, ModelCapability? capability = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var id = modelId.Trim();
        var exact = Entries.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) return null;

        if (capability is not { } cap)
            return exact[0];

        var match = exact.FirstOrDefault(e => e.Capability == cap);
        if (match is not null) return match;

        if (cap is ModelCapability.Chat or ModelCapability.Vision)
        {
            return exact.FirstOrDefault(e =>
                e.Capability is ModelCapability.Chat or ModelCapability.Vision);
        }

        return null;
    }

    /// <summary>
    /// Map an API-call kind (telemetry <c>kind</c>) to a catalog capability for lookup.
    /// Null when the kind is unknown / not model-backed.
    /// </summary>
    public static ModelCapability? CapabilityFromApiKind(string? kind) =>
        (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "image" or "image_edit" => ModelCapability.Image,
            "video" or "video_extend" or "video_poll" => ModelCapability.Video,
            "vision" => ModelCapability.Vision,
            "audio" or "music" => ModelCapability.Audio,
            "voice" or "tts" or "voice_clone" => ModelCapability.Voice,
            "lip_sync" or "lipsync" => ModelCapability.LipSync,
            "video_edit" or "videoedit" => ModelCapability.VideoEdit,
            "chat" or "planning" or "video_review" or VideoReviewCapabilityId => ModelCapability.Chat,
            _ => null,
        };

    /// <summary>
    /// Catalog-only resolution for logging / cost lines. Never invents models or providers.
    /// Prefer capability from <paramref name="kind"/> when present; otherwise any capability match.
    /// </summary>
    public static SupportedModelEntry? ResolveForLogging(string? modelId, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var cap = CapabilityFromApiKind(kind);
        if (cap is { } c)
        {
            var hit = Find(modelId, c);
            if (hit is not null) return hit;
        }
        return Find(modelId);
    }

    /// <summary>
    /// Catalog <c>providerId</c> for a model id, or null if the model is not in the catalog.
    /// </summary>
    public static string? CatalogProviderId(string? modelId, string? kind = null) =>
        ResolveForLogging(modelId, kind)?.ProviderId;

    /// <summary>
    /// Canonical catalog model id (casing/id as stored), or null if unknown.
    /// </summary>
    public static string? CanonicalModelId(string? modelId, string? kind = null) =>
        ResolveForLogging(modelId, kind)?.Id;

    public static SupportedModelEntry ResolveOrDefault(
        string? modelId,
        ModelCapability capability,
        string? fallbackId = null)
    {
        var hit = Find(modelId, capability);
        if (hit is not null) return hit;

        // Same id under a compatible capability (chat/vision share many models).
        if (TryCompatibleCapabilityHit(modelId, capability) is { } compatible)
            return compatible;

        if (TryFallbackHit(fallbackId, capability) is { } fallback)
            return fallback;

        // Catalog is SSoT — never invent synthetic models or pick an arbitrary "first" model.
        var label = string.IsNullOrWhiteSpace(modelId) ? "(none)" : modelId.Trim();
        throw new InvalidOperationException(
            $"Model '{label}' is not in models_catalog.json for {capability}. " +
            "Open Settings → Studio coverage and choose a catalog model for this job. " +
            "Do not rely on code defaults.");
    }

    private static SupportedModelEntry? TryCompatibleCapabilityHit(string? modelId, ModelCapability capability)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var hit = Find(modelId);
        if (hit is null) return null;
        if (hit.Capability == capability) return hit;
        if (capability is ModelCapability.Chat or ModelCapability.Vision
            && hit.Capability is ModelCapability.Chat or ModelCapability.Vision)
            return hit;
        return null;
    }

    private static SupportedModelEntry? TryFallbackHit(string? fallbackId, ModelCapability capability)
    {
        if (string.IsNullOrWhiteSpace(fallbackId)) return null;
        return Find(fallbackId, capability) ?? Find(fallbackId);
    }

    /// <summary>
    /// Build Configuration "API keys" rows from the catalog (enabled models only).
    /// Personal/server key flags are left false — server fills those from SQLite/env.
    /// </summary>
    public static List<ProviderKeyStatusDto> BuildProviderKeyRows()
    {
        var groups = Entries
            .Where(IsEnabledKeyProvider)
            .GroupBy(e => NormalizeProviderId(e.ProviderId), StringComparer.OrdinalIgnoreCase);

        var rows = new List<ProviderKeyStatusDto>();
        foreach (var group in groups.OrderBy(g => DisplayOrder(g.Key)))
        {
            var row = TryBuildProviderKeyRow(group.Key, group);
            if (row is not null)
                rows.Add(row);
        }
        return rows;
    }

    private static bool IsEnabledKeyProvider(SupportedModelEntry e) =>
        e.Enabled && (e.RequiredEnvKeys is { Count: > 0 }
                      || string.Equals(NormalizeProviderId(e.ProviderId), "fake", StringComparison.OrdinalIgnoreCase));

    private static ProviderKeyStatusDto? TryBuildProviderKeyRow(
        string pId, IEnumerable<SupportedModelEntry> group)
    {
        if (string.IsNullOrWhiteSpace(pId) || pId is "none") return null;
        var sample = group.First();
        var required = group.SelectMany(m => m.RequiredEnvKeys)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The fake test vendor is key-free by design (requiredEnvKeys: [] on every fake-* model) —
        // it must still get a row so GetUserSettingsDtoAsync's "always configured" special-case for
        // it has a row to apply to. Real providers with zero required keys are still dropped.
        if (required.Count == 0 && !string.Equals(pId, "fake", StringComparison.OrdinalIgnoreCase))
            return null;

        var supportsVideoGen = group.Any(m => m.Capability == ModelCapability.Video);
        var supportsVideoReview = group.Any(m => m.SupportsVideoReview);
        var supportsImageGen = group.Any(m => m.Capability == ModelCapability.Image);
        var supportsScriptPlanning = group.Any(m => m.Capability == ModelCapability.Chat);
        var supportsImageVision = group.Any(m => m.Capability == ModelCapability.Vision);
        var supportsAudio = group.Any(m => m.Capability == ModelCapability.Audio);
        var supportsVoice = group.Any(m => m.Capability == ModelCapability.Voice);
        var supportsLipSync = group.Any(m => m.Capability == ModelCapability.LipSync);
        var caps = ProviderCapabilityLabels(
            supportsVideoGen, supportsVideoReview, supportsImageGen, supportsScriptPlanning,
            supportsImageVision, supportsAudio, supportsVoice, supportsLipSync);

        return new ProviderKeyStatusDto
        {
            ProviderId = pId,
            DisplayName = DisplayNameForProvider(pId, sample),
            Family = string.IsNullOrWhiteSpace(sample.ProviderName) ? sample.Provider.ToString() : sample.ProviderName,
            ActiveSource = "none",
            CapabilitiesSummary = caps.Count > 0 ? string.Join(", ", caps) : "—",
            SupportsVideo = supportsVideoGen || supportsVideoReview,
            SupportsImage = supportsImageGen,
            SupportsChat = supportsScriptPlanning,
            SupportsVision = supportsImageVision,
            SupportsVideoGen = supportsVideoGen,
            SupportsVideoReview = supportsVideoReview,
            SupportsImageGen = supportsImageGen,
            SupportsScriptPlanning = supportsScriptPlanning,
            SupportsImageVision = supportsImageVision,
            RequiredEnvKeys = required,
            // Provider cards are for API keys — never dump per-model engineering notes here.
            Notes = ShortProviderBlurb(),
        };
    }

    private static List<string> ProviderCapabilityLabels(
        bool videoGen, bool videoReview, bool imageGen, bool scriptPlanning,
        bool imageVision, bool audio, bool voice, bool lipSync)
    {
        var caps = new List<string>();
        if (videoGen) caps.Add("Video Gen");
        if (videoReview) caps.Add("Video Review");
        if (imageGen) caps.Add("Image Gen");
        if (scriptPlanning) caps.Add("Script & Planning");
        if (imageVision) caps.Add("Image Vision / OCR");
        if (audio) caps.Add("Audio / Music");
        if (voice) caps.Add("Voice clone / TTS");
        if (lipSync) caps.Add("Lip-sync");
        return caps;
    }

    public static string NormalizeProviderId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return "";
        var raw = providerId.Trim();
        // Prefer in-memory providers[] — safe during TryLoadFromJson (before Entries is set).
        if (_loadedProviders is { Count: > 0 })
        {
            foreach (var prov in _loadedProviders)
            {
                if (string.Equals(prov.Id, raw, StringComparison.OrdinalIgnoreCase))
                    return prov.Id;
                if (prov.Aliases.Any(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase)))
                    return prov.Id;
            }
        }
        // Unknown: keep as lowercase token — do not invent a different provider.
        return raw.ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="providerId"/> matches a catalog <c>providers[]</c> id or alias,
    /// or appears as <c>providerId</c> on any model row. False for free-text / invented names.
    /// </summary>
    public static bool IsKnownProviderId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        try { EnsureLoaded(); } catch { /* catalog may be mid-load */ }
        var raw = providerId.Trim();
        if (_loadedProviders is { Count: > 0 })
        {
            foreach (var prov in _loadedProviders)
            {
                if (string.Equals(prov.Id, raw, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (prov.Aliases.Any(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        if (_loadedEntries is { Count: > 0 })
        {
            return _loadedEntries.Any(e =>
                string.Equals(e.ProviderId, raw, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private static string DisplayNameForProvider(string pId, SupportedModelEntry sample)
    {
        if (!string.IsNullOrWhiteSpace(sample.ProviderLabel))
            return sample.ProviderLabel;
        return ProviderLabelFor(pId);
    }

    /// <summary>UI label for a provider id — catalog <c>providers[]</c> only (no hardcoded map).</summary>
    public static string ProviderLabelFor(string? providerId)
    {
        var id = NormalizeProviderId(providerId);
        if (string.IsNullOrWhiteSpace(id)) return "";
        var hit = _loadedProviders?.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (hit is not null && !string.IsNullOrWhiteSpace(hit.Label))
            return hit.Label;
        if (_loadedEntries is { Count: > 0 })
        {
            var fromModel = _loadedEntries.FirstOrDefault(e =>
                string.Equals(e.ProviderId, id, StringComparison.OrdinalIgnoreCase));
            if (fromModel is not null && !string.IsNullOrWhiteSpace(fromModel.ProviderLabel))
                return fromModel.ProviderLabel;
        }
        return id;
    }

    private static string? ShortProviderBlurb()
    {
        // Optional notes live on models; provider rows keep a short capability summary only.
        return null;
    }

    private static int DisplayOrder(string pId)
    {
        var hit = _loadedProviders?.FirstOrDefault(p =>
            string.Equals(p.Id, pId, StringComparison.OrdinalIgnoreCase));
        return hit?.Order ?? 50;
    }

    public static string ProviderIdFor(string? modelId, ModelCapability capability) =>
        Find(modelId, capability)?.ProviderId
        ?? Find(modelId)?.ProviderId
        ?? "";

    public static string? DefaultModelIdForCapability(ModelCapability capability) =>
        DefaultModelIdForCapability(capability.ToString());

    /// <summary>
    /// Default model id for a capability from catalog <c>capabilities[].defaultModelId</c>,
    /// else first enabled model with that capability. Null if catalog has none.
    /// </summary>
    public static string? DefaultModelIdForCapability(string capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityId)) return null;
        try { EnsureLoaded(); } catch { return null; }

        var capDef = (_loadedCapabilities ?? Enumerable.Empty<ModelCapabilityDefinition>())
            .FirstOrDefault(c => string.Equals(c.Id, capabilityId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(capDef?.DefaultModelId))
        {
            var hit = Find(capDef.DefaultModelId);
            if (hit is { Enabled: true, Deprecated: false })
                return hit.Id;
        }

        var cap = ParseCapabilityId(capabilityId);
        if (TryVideoReviewDefault(capabilityId) is { } reviewId)
            return reviewId;

        return ForCapability(cap).FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Catalog default for a capability, or throw. Tools and tests must not invent a model id.
    /// </summary>
    public static string RequireDefaultModelIdForCapability(ModelCapability capability) =>
        RequireDefaultModelIdForCapability(capability.ToString());

    /// <summary>
    /// Catalog default for a capability, or throw. Tools and tests must not invent a model id.
    /// </summary>
    public static string RequireDefaultModelIdForCapability(string capabilityId)
    {
        var id = DefaultModelIdForCapability(capabilityId);
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                $"No default model is configured in the product catalog for capability '{capabilityId}'. " +
                "Set capabilities[].defaultModelId in models_catalog.json.");
        return id;
    }

    public static bool TryParseCapabilityId(string? capabilityId, out ModelCapability cap)
    {
        cap = ModelCapability.Chat;
        if (string.IsNullOrWhiteSpace(capabilityId)) return false;
        var s = capabilityId.Trim();
        if (Enum.TryParse<ModelCapability>(s.Replace("-", "").Replace("_", ""), true, out cap))
            return true;

        switch (s.ToLowerInvariant())
        {
            case "video": cap = ModelCapability.Video; return true;
            case "image": cap = ModelCapability.Image; return true;
            case "chat": case "planning": cap = ModelCapability.Chat; return true;
            case "vision": cap = ModelCapability.Vision; return true;
            case "audio": case "music": cap = ModelCapability.Audio; return true;
            case "voice": cap = ModelCapability.Voice; return true;
            case "lipsync": case "lip-sync": cap = ModelCapability.LipSync; return true;
            case "videoedit": case "video-edit": cap = ModelCapability.VideoEdit; return true;
            case VideoReviewCapabilityId: case "videoreview": cap = ModelCapability.Chat; return true;
            default: return false;
        }
    }

    private static ModelCapability ParseCapabilityId(string capabilityId)
    {
        if (TryParseCapabilityId(capabilityId, out var cap))
            return cap;
        return ModelCapability.Chat;
    }

    private static string? TryVideoReviewDefault(string capabilityId)
    {
        if (!string.Equals(capabilityId, VideoReviewCapabilityId, StringComparison.OrdinalIgnoreCase))
            return null;
        var review = Entries.FirstOrDefault(e => e.Enabled && !e.Deprecated && e.SupportsVideoReview);
        return review?.Id;
    }

    /// <summary>First enabled catalog voice model marked <see cref="SupportedModelEntry.IsVoiceCloneStep"/>.</summary>
    public static string? FirstEnabledVoiceCloneModelId()
    {
        try { EnsureLoaded(); } catch { return null; }
        return Entries.FirstOrDefault(e => e.Enabled && !e.Deprecated && e.Capability == ModelCapability.Voice && e.IsVoiceCloneStep)?.Id
               ?? ForCapability(ModelCapability.Voice).FirstOrDefault()?.Id;
    }

    /// <summary>
    /// First enabled Voice catalog row that is speak/TTS (not a clone step).
    /// Optional <paramref name="providerId"/> restricts to that catalog provider (aliases accepted).
    /// </summary>
    public static SupportedModelEntry? FirstEnabledSpeakModel(string? providerId = null)
    {
        try { EnsureLoaded(); } catch { return null; }
        var want = string.IsNullOrWhiteSpace(providerId) ? null : NormalizeProviderId(providerId);
        return ForCapability(ModelCapability.Voice)
            .FirstOrDefault(m => !m.IsVoiceCloneStep
                && (want is null
                    || string.Equals(NormalizeProviderId(m.ProviderId), want, StringComparison.OrdinalIgnoreCase)));
    }

    public static string? FirstEnabledSpeakModelId(string? providerId = null) =>
        FirstEnabledSpeakModel(providerId)?.Id;

    /// <summary>Speak/TTS catalog id, or throw. Product code must not invent a speak-model id.</summary>
    public static string RequireFirstEnabledSpeakModelId(string? providerId = null)
    {
        var id = FirstEnabledSpeakModelId(providerId);
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(providerId)
                    ? "No enabled speak/TTS model is configured in the product catalog for capability Voice."
                    : $"No enabled speak/TTS model is configured in the product catalog for provider '{providerId}'.");
        return id;
    }

    /// <summary>
    /// Speak/TTS model id: selected catalog row, else first enabled speak model for the provider,
    /// else an explicit caller id, else throw. Never invents a model id.
    /// </summary>
    public static string ResolveSpeakModelId(string? selectedId, string? providerId, string? explicitModel = null)
    {
        if (!string.IsNullOrWhiteSpace(selectedId)) return selectedId.Trim();
        var fromCatalog = FirstEnabledSpeakModelId(providerId);
        if (!string.IsNullOrWhiteSpace(fromCatalog)) return fromCatalog;
        if (!string.IsNullOrWhiteSpace(explicitModel)) return explicitModel.Trim();
        return RequireFirstEnabledSpeakModelId(providerId);
    }

    public static IReadOnlyList<string> MissingEnvKeys(SupportedModelEntry model)
    {
        return model.RequiredEnvKeys
            .Where(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            .ToList();
    }


    /// <summary>
    /// Self-test: every enabled model must have the required capability + cost fields for its
    /// capability. Returns human-readable errors (empty = OK). Call after catalog load / in tests
    /// so deploy fails before movie generation, not mid-pipeline.
    /// </summary>
    public static IReadOnlyList<string> ValidateEnabledModels()
    {
        EnsureLoaded();
        var errors = new List<string>();
        foreach (var e in Entries.Where(x => x.Enabled))
            ValidateOne(e, errors);
        return errors;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> if any enabled model is incomplete.</summary>
    public static void EnsureEnabledModelsComplete()
    {
        var errors = ValidateEnabledModels();
        if (errors.Count == 0) return;
        throw new InvalidOperationException(
            "models_catalog.json failed self-test (" + errors.Count + " issue(s)):\n- " +
            string.Join("\n- ", errors));
    }

    private static void ValidateOne(SupportedModelEntry e, List<string> errors)
    {
        void Need(bool ok, string field)
        {
            if (!ok)
                errors.Add($"{e.Id} ({e.Capability}): missing or invalid {field}");
        }

        Need(!string.IsNullOrWhiteSpace(e.Id), "id");
        Need(!string.IsNullOrWhiteSpace(e.DisplayName), "displayName");

        // Lab models: structural only — incomplete limits/costs are intentional.
        if (e.LabMode)
        {
            Need(!string.IsNullOrWhiteSpace(e.LabNotes), "labNotes (required when labMode=true)");
            return;
        }

        Need(!string.IsNullOrWhiteSpace(e.LastVerifiedAt), "lastVerifiedAt");

        switch (e.Capability)
        {
            case ModelCapability.Chat:
            case ModelCapability.Vision:
                Need(e.MaxInputTokens is > 0, "maxInputTokens");
                Need(e.MaxOutputTokens is > 0, "maxOutputTokens");
                Need(e.InputCostPerMillionTokens is not null, "inputCostPerMillionTokens");
                Need(e.OutputCostPerMillionTokens is not null, "outputCostPerMillionTokens");
                Need(!string.IsNullOrWhiteSpace(e.PricingNotes), "pricingNotes");
                Need(!string.IsNullOrWhiteSpace(e.PricingLastReviewedAt), "pricingLastReviewedAt");
                break;

            case ModelCapability.Video:
                Need(e.MinClipDurationSeconds is not null, "minClipDurationSeconds");
                Need(e.MaxClipDurationSeconds is not null, "maxClipDurationSeconds");
                Need(e.AbsMaxClipDurationSeconds is not null, "absMaxClipDurationSeconds");
                Need(e.MaxReferenceImages is not null, "maxReferenceImages");
                Need(e.MaxPromptLength is > 0, MaxPromptLengthField);
                Need(
                    e.VideoCostPerSecondByResolution is { Count: > 0 } ||
                    e.VideoBaseCostByResolution is { Count: > 0 },
                    "videoCostPerSecondByResolution or videoBaseCostByResolution");
                Need(e.VideoReferenceImageCost is not null, "videoReferenceImageCost");
                if (e.SupportsVideoContinue)
                    Need(e.VideoExtendCostPerSecond is not null, "videoExtendCostPerSecond");
                Need(!string.IsNullOrWhiteSpace(e.PricingNotes), "pricingNotes");
                Need(!string.IsNullOrWhiteSpace(e.PricingLastReviewedAt), "pricingLastReviewedAt");
                break;

            case ModelCapability.Image:
                Need(e.MaxReferenceImages is not null, "maxReferenceImages");
                Need(e.MaxPromptLength is > 0, MaxPromptLengthField);
                Need(e.ImageCostPerImage is not null, "imageCostPerImage");
                Need(!string.IsNullOrWhiteSpace(e.PricingNotes), "pricingNotes");
                Need(!string.IsNullOrWhiteSpace(e.PricingLastReviewedAt), "pricingLastReviewedAt");
                break;

            case ModelCapability.Audio:
                Need(e.MaxAudioDurationSeconds is > 0, "maxAudioDurationSeconds");
                Need(e.MaxPromptLength is > 0, MaxPromptLengthField);
                // supportsVocals is bool — always present
                break;

            case ModelCapability.Voice:
                Need(e.MaxPromptLength is > 0, MaxPromptLengthField);
                break;

            case ModelCapability.LipSync:
                // costPerMinuteUsd optional until catalog filled for every lip model
                break;

            case ModelCapability.VideoEdit:
                Need(e.MaxEditInputDurationSeconds is > 0, "maxEditInputDurationSeconds");
                Need(e.MaxPromptLength is > 0, MaxPromptLengthField);
                // Cost fields optional — xAI hasn't published edit-specific pricing; entries may
                // proxy generation's videoCostPerSecondByResolution as a labeled estimate instead.
                break;
        }
    }


    /// <param name="includeLabModels">
    /// When false (default), lab-mode rows are omitted — regular users never see incomplete models.
    /// Admins pass true for catalog management / experimental picks.
    /// </param>
    public static IReadOnlyList<SupportedModelDto> ToDtoList(bool enabledOnly = true, bool includeLabModels = false) =>
        Entries.Where(e => (!enabledOnly || e.Enabled) && (includeLabModels || !e.LabMode))
            .Select(ToDto)
            .ToList();

    /// <summary>True when <paramref name="modelId"/> is an enabled lab-mode catalog row.</summary>
    public static bool IsLabModel(string? modelId, ModelCapability? capability = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        try { EnsureLoaded(); } catch { return false; }
        var e = Find(modelId, capability);
        return e is { LabMode: true, Enabled: true };
    }

    public static SupportedModelDto ToDto(SupportedModelEntry e) => new()
    {
        Id = e.Id,
        DisplayName = e.DisplayName,
        Capability = e.Capability.ToString().ToLowerInvariant(),
        Provider = e.Provider.ToString().ToLowerInvariant(),
        ApiBase = e.ApiBase,
        EndpointPath = e.EndpointPath,
        RequiredEnvKeys = e.RequiredEnvKeys.ToList(),
        Enabled = e.Enabled,
        Deprecated = e.Deprecated,
        MaxInputTokens = e.MaxInputTokens,
        MaxOutputTokens = e.MaxOutputTokens,
        InputCostPerMillionTokens = e.InputCostPerMillionTokens,
        OutputCostPerMillionTokens = e.OutputCostPerMillionTokens,
        VideoCostPerSecondByResolution = e.VideoCostPerSecondByResolution is { } v
            ? new Dictionary<string, double>(v)
            : null,
        VideoBaseCostByResolution = e.VideoBaseCostByResolution is { } vb
            ? new Dictionary<string, double>(vb)
            : null,
        ImageCostPerImage = e.ImageCostPerImage,
        VideoReferenceImageCost = e.VideoReferenceImageCost,
        VideoExtendCostPerSecond = e.VideoExtendCostPerSecond,
        PricingNotes = e.PricingNotes,
        PricingLastReviewedAt = e.PricingLastReviewedAt,
        LastVerifiedAt = e.LastVerifiedAt,
        LabMode = e.LabMode,
        LabNotes = e.LabNotes,
        Notes = e.Notes,
        FeatureRequestUrl = e.FeatureRequestUrl,
        ProviderId = e.ProviderId,
        ProviderLabel = e.ProviderLabel,
        SupportsVideoContinue = e.SupportsVideoContinue,
        SupportsReferenceImages = e.SupportsReferenceImages,
        MaxReferenceImages = e.MaxReferenceImages,
        SupportsVideoReview = e.SupportsVideoReview,
        MinClipDurationSeconds = e.MinClipDurationSeconds,
        MaxClipDurationSeconds = e.MaxClipDurationSeconds,
        AbsMaxClipDurationSeconds = e.AbsMaxClipDurationSeconds,
        MaxSpeakersPerClip = e.MaxSpeakersPerClip,
        AllowedDurationsSeconds = e.AllowedDurationsSeconds is { } ad ? new List<int>(ad) : null,
        MaxExtensionSeconds = e.MaxExtensionSeconds,
        MaxEditInputDurationSeconds = e.MaxEditInputDurationSeconds,
        MaxAudioDurationSeconds = e.MaxAudioDurationSeconds,
        SupportsVocals = e.SupportsVocals,
        NumInferenceSteps = e.NumInferenceSteps,
        ShortClipFrameCount = e.ShortClipFrameCount,
        LongClipFrameCount = e.LongClipFrameCount,
        SupportedAspectRatios = e.SupportedAspectRatios is { } sar ? sar.ToList() : null,
        DefaultAspectRatio = e.DefaultAspectRatio,
        MaxPromptLength = e.MaxPromptLength,
        IsVoiceCloneStep = e.IsVoiceCloneStep,
        CostPerCloneUsd = e.CostPerCloneUsd,
        CostPerThousandCharsUsd = e.CostPerThousandCharsUsd,
        CostPerMinuteUsd = e.CostPerMinuteUsd,
    };

    public static SupportedModelEntry FromDto(SupportedModelDto d)
    {
        var (providerId, providerLabel) = ResolveProviderFromDto(d);
        var providerFamily = Enum.TryParse<ModelProviderFamily>(d.Provider, true, out var prov)
            ? prov
            : ProviderFamilyFromId(providerId);
        return new SupportedModelEntry
    {
        Id = d.Id,
        DisplayName = d.DisplayName,
        Capability = Enum.TryParse<ModelCapability>(d.Capability, true, out var cap) ? cap : ModelCapability.Chat,
        ProviderName = d.Provider ?? "",
        Provider = providerFamily,
        ProviderId = providerId,
        ProviderLabel = providerLabel,
        ApiBase = d.ApiBase ?? "",
        EndpointPath = d.EndpointPath ?? "",
        RequiredEnvKeys = d.RequiredEnvKeys ?? new List<string>(),
        Enabled = d.Enabled,
        Deprecated = d.Deprecated,
        MaxInputTokens = d.MaxInputTokens,
        MaxOutputTokens = d.MaxOutputTokens,
        InputCostPerMillionTokens = d.InputCostPerMillionTokens,
        OutputCostPerMillionTokens = d.OutputCostPerMillionTokens,
        VideoCostPerSecondByResolution = d.VideoCostPerSecondByResolution,
        VideoBaseCostByResolution = d.VideoBaseCostByResolution,
        ImageCostPerImage = d.ImageCostPerImage,
        VideoReferenceImageCost = d.VideoReferenceImageCost,
        VideoExtendCostPerSecond = d.VideoExtendCostPerSecond,
        PricingNotes = d.PricingNotes,
        PricingLastReviewedAt = d.PricingLastReviewedAt,
        LastVerifiedAt = d.LastVerifiedAt,
        LabMode = d.LabMode,
        LabNotes = d.LabNotes,
        Notes = d.Notes,
        FeatureRequestUrl = d.FeatureRequestUrl,
        SupportsVideoContinue = d.SupportsVideoContinue,
        SupportsReferenceImages = d.SupportsReferenceImages,
        MaxReferenceImages = d.MaxReferenceImages,
        SupportsVideoReview = d.SupportsVideoReview,
        MinClipDurationSeconds = d.MinClipDurationSeconds,
        MaxClipDurationSeconds = d.MaxClipDurationSeconds,
        AbsMaxClipDurationSeconds = d.AbsMaxClipDurationSeconds,
        MaxSpeakersPerClip = d.MaxSpeakersPerClip,
        AllowedDurationsSeconds = d.AllowedDurationsSeconds,
        MaxExtensionSeconds = d.MaxExtensionSeconds,
        MaxEditInputDurationSeconds = d.MaxEditInputDurationSeconds,
        MaxAudioDurationSeconds = d.MaxAudioDurationSeconds,
        SupportsVocals = d.SupportsVocals,
        NumInferenceSteps = d.NumInferenceSteps,
        ShortClipFrameCount = d.ShortClipFrameCount,
        LongClipFrameCount = d.LongClipFrameCount,
        SupportedAspectRatios = d.SupportedAspectRatios,
        DefaultAspectRatio = d.DefaultAspectRatio,
        MaxPromptLength = d.MaxPromptLength,
        IsVoiceCloneStep = d.IsVoiceCloneStep,
        CostPerCloneUsd = d.CostPerCloneUsd,
        CostPerThousandCharsUsd = d.CostPerThousandCharsUsd,
        CostPerMinuteUsd = d.CostPerMinuteUsd,
    };
    }

    /// <summary>Resolve provider id + label from model DTO using catalog providers[] only.</summary>
    private static (string Id, string Label) ResolveProviderFromDto(SupportedModelDto d)
    {
        // Explicit providerId on the model wins.
        if (!string.IsNullOrWhiteSpace(d.ProviderId))
        {
            var id = NormalizeProviderId(d.ProviderId);
            var label = !string.IsNullOrWhiteSpace(d.ProviderLabel)
                ? d.ProviderLabel.Trim()
                : ProviderLabelFor(id);
            return (id, label);
        }
        // provider field (enum-style name) resolved via providers[].aliases
        if (!string.IsNullOrWhiteSpace(d.Provider))
        {
            var id = NormalizeProviderId(d.Provider);
            var label = !string.IsNullOrWhiteSpace(d.ProviderLabel)
                ? d.ProviderLabel.Trim()
                : ProviderLabelFor(id);
            return (id, label);
        }
        return ("", "");
    }

    private static ModelProviderFamily ProviderFamilyFromId(string providerId) =>
        NormalizeProviderId(providerId) switch
        {
            "grok" => ModelProviderFamily.Xai,
            "gemini" => ModelProviderFamily.Google,
            "anthropic" => ModelProviderFamily.Anthropic,
            "fal" => ModelProviderFamily.Fal,
            "suno" => ModelProviderFamily.Suno,
            "aimusicapi" => ModelProviderFamily.AiMusicApi,
            "elevenlabs" => ModelProviderFamily.ElevenLabs,
            "openai" => ModelProviderFamily.OpenAI,
            "fake" => ModelProviderFamily.Fake,
            _ => ModelProviderFamily.Xai,
        };

    private static CatalogProviderDefinition NormalizeProviderDef(CatalogProviderDto d) => new()
    {
        Id = (d.Id ?? "").Trim().ToLowerInvariant(),
        Label = (d.Label ?? d.Id ?? "").Trim(),
        Aliases = (d.Aliases ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        Order = d.Order,
    };

    /// <summary>
    /// When catalog JSON has models but no providers[] array, derive provider rows from model fields only
    /// (still data-driven — no invented providers).
    /// </summary>
    private static List<CatalogProviderDto> InferProvidersFromModels(IEnumerable<SupportedModelDto> models)
    {
        var list = new List<CatalogProviderDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var m in models)
        {
            var id = !string.IsNullOrWhiteSpace(m.ProviderId)
                ? m.ProviderId.Trim().ToLowerInvariant()
                : (m.Provider ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            list.Add(new CatalogProviderDto
            {
                Id = id,
                Label = !string.IsNullOrWhiteSpace(m.ProviderLabel) ? m.ProviderLabel.Trim() : (m.Provider ?? id),
                Aliases = string.IsNullOrWhiteSpace(m.Provider) ? new List<string>() : new List<string> { m.Provider },
                Order = order++,
            });
        }
        return list;
    }
}

public sealed class SupportedModelDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Capability { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ApiBase { get; set; } = "";
    public string EndpointPath { get; set; } = "";
    public List<string> RequiredEnvKeys { get; set; } = new();
    public ApiAuthLocation AuthLocation { get; set; } = ApiAuthLocation.Bearer;
    public RetryBackoffKind BackoffKind { get; set; } = RetryBackoffKind.Quadratic;
    public bool Enabled { get; set; } = true;
    public bool Deprecated { get; set; }
    public int? MaxInputTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public double? InputCostPerMillionTokens { get; set; }
    public double? OutputCostPerMillionTokens { get; set; }
    public Dictionary<string, double>? VideoCostPerSecondByResolution { get; set; }
    public Dictionary<string, double>? VideoBaseCostByResolution { get; set; }
    public double? ImageCostPerImage { get; set; }
    public double? VideoReferenceImageCost { get; set; }
    public double? VideoExtendCostPerSecond { get; set; }
        public string? PricingNotes { get; set; }
        public string? PricingLastReviewedAt { get; set; }
        public string? LastVerifiedAt { get; set; }
        public bool LabMode { get; set; }
        public string? LabNotes { get; set; }
public string? Notes { get; set; }
    public string? FeatureRequestUrl { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderLabel { get; set; }
    public bool SupportsVideoContinue { get; set; } = true;
    public bool SupportsReferenceImages { get; set; } = true;
    public int? MaxReferenceImages { get; set; }
    public bool SupportsVideoReview { get; set; }
    public int? MinClipDurationSeconds { get; set; }
    public int? MaxClipDurationSeconds { get; set; }
    public int? AbsMaxClipDurationSeconds { get; set; }
    public int? MaxSpeakersPerClip { get; set; }
    public List<int>? AllowedDurationsSeconds { get; set; }
    public int? MaxExtensionSeconds { get; set; }
    public double? MaxEditInputDurationSeconds { get; set; }
    public int? MaxAudioDurationSeconds { get; set; }
    public bool SupportsVocals { get; set; }
    public int? NumInferenceSteps { get; set; }
    public int? ShortClipFrameCount { get; set; }
    public int? LongClipFrameCount { get; set; }
    public List<string>? SupportedAspectRatios { get; set; }
    public string? DefaultAspectRatio { get; set; }
    public int? MaxPromptLength { get; set; }
    public bool IsVoiceCloneStep { get; set; }
    public double? CostPerCloneUsd { get; set; }
    public double? CostPerThousandCharsUsd { get; set; }
    public double? CostPerMinuteUsd { get; set; }
}

public sealed class ModelCapabilityDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public int Order { get; init; }
    public string? DefaultModelId { get; init; }
}

public sealed class ModelCapabilityDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public int Order { get; set; }
    public string? DefaultModelId { get; set; }
}

public sealed class ModelCatalogContainerDto
{
    public List<CatalogProviderDto>? Providers { get; set; }
    public List<ModelCapabilityDto>? Capabilities { get; set; }
    public Dictionary<string, List<string>>? TaskRankings { get; set; }
    public List<SupportedModelDto>? Models { get; set; }
}

/// <summary>One provider row in models_catalog.json <c>providers[]</c>.</summary>
public sealed class CatalogProviderDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string>? Aliases { get; set; }
    public ApiAuthLocation AuthLocation { get; set; } = ApiAuthLocation.Bearer;
    public int Order { get; set; }
}

public sealed class CatalogProviderDefinition
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public List<string> Aliases { get; init; } = new();
    public ApiAuthLocation AuthLocation { get; init; } = ApiAuthLocation.Bearer;
    public int Order { get; init; }
}
