using System;

namespace PageToMovie.Engine;

#region 1. Jobs / Workers Enums

/// <summary>
/// Execution priority levels for queued film processing jobs.
/// </summary>
public enum JobPriorityLevel
{
    Low,
    Normal,
    High,
    Critical,
    Emergency
}

/// <summary>
/// Identification names for specialized worker pools handling API and rendering concurrency.
/// </summary>
public enum WorkerPoolName
{
    ApiWorkerPool,
    LocalWorkerPool,
    ImageWorkerPool,
    VideoWorkerPool,
    AudioWorkerPool,
    GeneralWorkerPool
}

/// <summary>
/// Scheduling and queuing strategies used by worker pools.
/// </summary>
public enum WorkerPoolQueueStrategy
{
    Fifo,
    Lifo,
    Priority,
    RoundRobin,
    FairSharing
}

/// <summary>
/// Severity level categorization for engine job errors and system alerts.
/// </summary>
public enum ErrorSeverityLevel
{
    Info,
    Warning,
    Error,
    Critical,
    Fatal
}

/// <summary>
/// Caching strategies for scene assets, AI completions, and pre-rendered clips.
/// </summary>
public enum CacheStrategyType
{
    None,
    Memory,
    Disk,
    Hybrid,
    Distributed,
    ReadThrough,
    WriteThrough
}

/// <summary>
/// Events triggering cache invalidation across the engine pipeline.
/// </summary>
public enum CacheInvalidationTrigger
{
    Manual,
    TimeToLive,
    UserEdit,
    JobCompleted,
    SystemRestart,
    ProjectDeleted
}

/// <summary>
/// Strategies governing job retry behaviors on failure.
/// </summary>
public enum RetryPolicyStrategy
{
    None,
    FixedInterval,
    ExponentialBackoff,
    LinearBackoff,
    ImmediateRetry
}

#endregion

#region 2. Camera / Planning Enums

/// <summary>
/// Camera vertical angle perspectives for shot planning.
/// </summary>
public enum ShotAngleType
{
    EyeLevel,
    LowAngle,
    HighAngle,
    BirdsEye,
    WormsEye,
    DutchAngle,
    Overhead
}

/// <summary>
/// Lens focal length specifications for shot generation prompts.
/// </summary>
public enum CameraLensSpec
{
    Lens14mm,
    Lens24mm,
    Lens35mm,
    Lens50mm,
    Lens85mm,
    Lens135mm,
    Lens200mm,
    Anamorphic
}

/// <summary>
/// Camera movement patterns for video generation prompts.
/// </summary>
public enum CameraMovementPattern
{
    Static,
    PushIn,
    PullOut,
    PanLeft,
    PanRight,
    TiltUp,
    TiltDown,
    Tracking,
    Crane,
    Handheld
}

/// <summary>
/// Category of beat action within screenplay narrative flow.
/// </summary>
public enum BeatActionCategory
{
    Dialogue,
    Action,
    Establishing,
    Reaction,
    Transition,
    Emotional,
    Atmosphere,
    Exposition
}

/// <summary>
/// Cinematic lighting setup styles.
/// </summary>
public enum LightingStyleType
{
    Daylight,
    SoftLight,
    DramaticLowKey,
    GoldenHour,
    NeonCyberpunk,
    HighKey,
    NaturalIndoor,
    Volumetric
}

/// <summary>
/// Color grading preset palettes for shot visual consistency.
/// </summary>
public enum ColorPalettePreset
{
    Natural,
    WarmCinematic,
    CoolNoir,
    VibrantPop,
    Desaturated,
    Pastel,
    Monochrome,
    Vintage
}

/// <summary>
/// Narrative pacing mood bands.
/// </summary>
public enum PacingMoodBand
{
    SlowBurn,
    Moderate,
    Energetic,
    Frenetic,
    Suspenseful,
    Melancholic
}

/// <summary>
/// Modes for clip duration allocation and budget estimation.
/// </summary>
public enum ClipDurationBudgetMode
{
    Fixed,
    Dynamic,
    NaturalRuntime,
    ExactMatch,
    FitToAudio
}

/// <summary>
/// Retry strategies specific to AI video clip generation.
/// </summary>
public enum ClipGenRetryStrategy
{
    FailFast,
    RetrySamePrompt,
    VarySeed,
    FallbackModel,
    RelaxConstraints
}

#endregion

#region 3. Voice / Audio Enums

/// <summary>
/// Supported voice generation service providers.
/// </summary>
public enum VoiceProviderKind
{
    ElevenLabs,
    AzureSpeech,
    Suno,
    FalVoice,
    SystemSynthetic,
    CustomClone
}

/// <summary>
/// Gender filter options for character voice matching.
/// </summary>
public enum VoiceGenderFilter
{
    Unspecified,
    Male,
    Female,
    NonBinary,
    Neutral
}

/// <summary>
/// Target age group filter for voice selection.
/// </summary>
public enum VoiceAgeGroupFilter
{
    Child,
    Teen,
    YoungAdult,
    Adult,
    Senior
}

/// <summary>
/// Audio encoding codec types.
/// </summary>
public enum AudioCodecType
{
    Mp3,
    Aac,
    Wav,
    Pcm,
    Opus,
    Flac
}

/// <summary>
/// Audio container format types.
/// </summary>
public enum AudioContainerType
{
    Mp3,
    Wav,
    M4a,
    Ogg,
    Flac,
    Webm
}

/// <summary>
/// Scope of text substitution in speech synthesis.
/// </summary>
public enum SpeechSubstitutionScope
{
    None,
    Word,
    Sentence,
    Paragraph,
    CharacterName,
    FullScript
}

/// <summary>
/// Sensitivity presets for silence detection algorithms.
/// </summary>
public enum SilenceDetectorPreset
{
    Conservative,
    Balanced,
    Aggressive,
    Strict,
    Custom
}

/// <summary>
/// Audio mix balance profiles between dialogue, music, and SFX.
/// </summary>
public enum AudioMixBalanceMode
{
    DialogueDominant,
    MusicDominant,
    SFXDominant,
    EqualBalanced,
    AutomatedDucking
}

/// <summary>
/// Musical genre styles for background score selection.
/// </summary>
public enum MusicGenrePreset
{
    Orchestral,
    Ambient,
    Electronic,
    Cinematic,
    Acoustic,
    Jazz,
    Dramatic,
    Rock
}

/// <summary>
/// Music tempo classification categories.
/// </summary>
public enum MusicTempoCategory
{
    VerySlow,
    Slow,
    Medium,
    Fast,
    VeryFast
}

#endregion

#region 4. Media / Video Enums

/// <summary>
/// Classification of media asset files stored in project workspace.
/// </summary>
public enum MediaAssetType
{
    Image,
    Video,
    Audio,
    Voiceover,
    BackgroundMusic,
    Subtitle,
    FountainScript,
    VideoFrame
}

/// <summary>
/// Storage tier classification for project media persistence.
/// </summary>
public enum StorageTierKind
{
    Hot,
    Warm,
    Cold,
    Archive,
    Temporary
}

/// <summary>
/// Video encoding codec specifications.
/// </summary>
public enum VideoCodecType
{
    H264,
    Hevc,
    Vp9,
    Av1,
    ProRes
}

/// <summary>
/// Video file container formats.
/// </summary>
public enum VideoContainerKind
{
    Mp4,
    Mkv,
    Mov,
    Webm,
    Avi
}

/// <summary>
/// Standard video resolution presets.
/// </summary>
public enum VideoResolutionPreset
{
    Res720p,
    Res1080p,
    Res1440p,
    Res4k,
    Res8k
}

/// <summary>
/// Target framerate presets.
/// </summary>
public enum VideoFrameratePreset
{
    Fps24,
    Fps25,
    Fps30,
    Fps50,
    Fps60,
    Fps120
}

/// <summary>
/// Aspect ratio frame presets.
/// </summary>
public enum AspectRatioPreset
{
    Ratio16x9,
    Ratio9x16,
    Ratio1x1,
    Ratio4x3,
    Ratio21x9
}

/// <summary>
/// Subtitle file format presets.
/// </summary>
public enum SubtitleFormatPreset
{
    Srt,
    Vtt,
    Ass,
    Ttml,
    SubRip
}

/// <summary>
/// Subtitle on-screen positioning presets.
/// </summary>
public enum SubtitlePositionPreset
{
    Bottom,
    Top,
    Middle,
    LowerThird
}

/// <summary>
/// Quality preset levels for export rendering.
/// </summary>
public enum ExportQualityLevel
{
    Draft,
    Low,
    Medium,
    High,
    Ultra,
    Lossless
}

#endregion

#region Helper Extensions & String Parsing

/// <summary>
/// Extension methods and string parsers for EngineLayerEnums.
/// </summary>
public static class EngineLayerEnumExtensions
{
    public static AspectRatioPreset ParseAspectRatioPreset(string? s, AspectRatioPreset defaultValue = AspectRatioPreset.Ratio16x9)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return s.Trim() switch
            {
                "16:9" => AspectRatioPreset.Ratio16x9,
                "9:16" => AspectRatioPreset.Ratio9x16,
                "1:1" => AspectRatioPreset.Ratio1x1,
                "4:3" => AspectRatioPreset.Ratio4x3,
                "21:9" => AspectRatioPreset.Ratio21x9,
                _ => Enum.TryParse<AspectRatioPreset>(s, true, out var r) ? r : defaultValue
            };
        }

    public static AudioCodecType ParseAudioCodecType(string? s, AudioCodecType defaultValue = AudioCodecType.Aac)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioCodecType>(s, true, out var r) ? r : defaultValue;

    public static AudioContainerType ParseAudioContainerType(string? s, AudioContainerType defaultValue = AudioContainerType.Mp3)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioContainerType>(s, true, out var r) ? r : defaultValue;

    public static AudioMixBalanceMode ParseAudioMixBalanceMode(string? s, AudioMixBalanceMode defaultValue = AudioMixBalanceMode.EqualBalanced)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioMixBalanceMode>(s, true, out var r) ? r : defaultValue;

    public static BeatActionCategory ParseBeatActionCategory(string? s, BeatActionCategory defaultValue = BeatActionCategory.Dialogue)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<BeatActionCategory>(s, true, out var r) ? r : defaultValue;

    public static CacheInvalidationTrigger ParseCacheInvalidationTrigger(string? s, CacheInvalidationTrigger defaultValue = CacheInvalidationTrigger.UserEdit)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CacheInvalidationTrigger>(s, true, out var r) ? r : defaultValue;

    public static CacheStrategyType ParseCacheStrategyType(string? s, CacheStrategyType defaultValue = CacheStrategyType.Memory)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CacheStrategyType>(s, true, out var r) ? r : defaultValue;

    public static CameraLensSpec ParseCameraLensSpec(string? s, CameraLensSpec defaultValue = CameraLensSpec.Lens35mm)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            if (s.Contains("14mm", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Lens14mm;
            if (s.Contains("24mm", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Lens24mm;
            if (s.Contains("35mm", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Lens35mm;
            if (s.Contains("50mm", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Lens50mm;
            if (s.Contains("85mm", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Lens85mm;
            if (s.Contains("135mm", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Lens135mm;
            if (s.Contains("200mm", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Lens200mm;
            if (s.Contains("anamorphic", StringComparison.OrdinalIgnoreCase)) return CameraLensSpec.Anamorphic;
            return Enum.TryParse<CameraLensSpec>(s, true, out var r) ? r : defaultValue;
        }

    public static CameraMovementPattern ParseCameraMovementPattern(string? s, CameraMovementPattern defaultValue = CameraMovementPattern.Static)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            var lower = s.ToLowerInvariant().Trim();
            if (lower.Contains("push")) return CameraMovementPattern.PushIn;
            if (lower.Contains("pull")) return CameraMovementPattern.PullOut;
            if (lower.Contains("pan left") || lower.Contains("pan_left")) return CameraMovementPattern.PanLeft;
            if (lower.Contains("pan right") || lower.Contains("pan_right")) return CameraMovementPattern.PanRight;
            if (lower.Contains("tilt up") || lower.Contains("tilt_up")) return CameraMovementPattern.TiltUp;
            if (lower.Contains("tilt down") || lower.Contains("tilt_down")) return CameraMovementPattern.TiltDown;
            if (lower.Contains("tracking")) return CameraMovementPattern.Tracking;
            if (lower.Contains("crane")) return CameraMovementPattern.Crane;
            if (lower.Contains("handheld")) return CameraMovementPattern.Handheld;
            return Enum.TryParse<CameraMovementPattern>(s, true, out var r) ? r : defaultValue;
        }

    public static ClipDurationBudgetMode ParseClipDurationBudgetMode(string? s, ClipDurationBudgetMode defaultValue = ClipDurationBudgetMode.Dynamic)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ClipDurationBudgetMode>(s, true, out var r) ? r : defaultValue;

    public static ClipGenRetryStrategy ParseClipGenRetryStrategy(string? s, ClipGenRetryStrategy defaultValue = ClipGenRetryStrategy.RetrySamePrompt)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ClipGenRetryStrategy>(s, true, out var r) ? r : defaultValue;

    public static ColorPalettePreset ParseColorPalettePreset(string? s, ColorPalettePreset defaultValue = ColorPalettePreset.Natural)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ColorPalettePreset>(s, true, out var r) ? r : defaultValue;

    public static ErrorSeverityLevel ParseErrorSeverityLevel(string? s, ErrorSeverityLevel defaultValue = ErrorSeverityLevel.Error)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ErrorSeverityLevel>(s, true, out var r) ? r : defaultValue;

    public static ExportQualityLevel ParseExportQualityLevel(string? s, ExportQualityLevel defaultValue = ExportQualityLevel.High)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ExportQualityLevel>(s, true, out var r) ? r : defaultValue;

    public static JobPriorityLevel ParseJobPriorityLevel(string? s, JobPriorityLevel defaultValue = JobPriorityLevel.Normal)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<JobPriorityLevel>(s, true, out var r) ? r : defaultValue;

    public static LightingStyleType ParseLightingStyleType(string? s, LightingStyleType defaultValue = LightingStyleType.Daylight)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<LightingStyleType>(s, true, out var r) ? r : defaultValue;

    public static MediaAssetType ParseMediaAssetType(string? s, MediaAssetType defaultValue = MediaAssetType.Video)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<MediaAssetType>(s, true, out var r) ? r : defaultValue;

    public static MusicGenrePreset ParseMusicGenrePreset(string? s, MusicGenrePreset defaultValue = MusicGenrePreset.Cinematic)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<MusicGenrePreset>(s, true, out var r) ? r : defaultValue;

    public static MusicTempoCategory ParseMusicTempoCategory(string? s, MusicTempoCategory defaultValue = MusicTempoCategory.Medium)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<MusicTempoCategory>(s, true, out var r) ? r : defaultValue;

    public static PacingMoodBand ParsePacingMoodBand(string? s, PacingMoodBand defaultValue = PacingMoodBand.Moderate)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<PacingMoodBand>(s, true, out var r) ? r : defaultValue;

    public static RetryPolicyStrategy ParseRetryPolicyStrategy(string? s, RetryPolicyStrategy defaultValue = RetryPolicyStrategy.ExponentialBackoff)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<RetryPolicyStrategy>(s, true, out var r) ? r : defaultValue;

    public static ShotAngleType ParseShotAngleType(string? s, ShotAngleType defaultValue = ShotAngleType.EyeLevel)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ShotAngleType>(s, true, out var r) ? r : defaultValue;

    public static SilenceDetectorPreset ParseSilenceDetectorPreset(string? s, SilenceDetectorPreset defaultValue = SilenceDetectorPreset.Balanced)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SilenceDetectorPreset>(s, true, out var r) ? r : defaultValue;

    public static SpeechSubstitutionScope ParseSpeechSubstitutionScope(string? s, SpeechSubstitutionScope defaultValue = SpeechSubstitutionScope.None)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SpeechSubstitutionScope>(s, true, out var r) ? r : defaultValue;

    public static StorageTierKind ParseStorageTierKind(string? s, StorageTierKind defaultValue = StorageTierKind.Hot)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<StorageTierKind>(s, true, out var r) ? r : defaultValue;

    public static SubtitleFormatPreset ParseSubtitleFormatPreset(string? s, SubtitleFormatPreset defaultValue = SubtitleFormatPreset.Srt)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleFormatPreset>(s, true, out var r) ? r : defaultValue;

    public static SubtitlePositionPreset ParseSubtitlePositionPreset(string? s, SubtitlePositionPreset defaultValue = SubtitlePositionPreset.Bottom)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitlePositionPreset>(s, true, out var r) ? r : defaultValue;

    public static VideoCodecType ParseVideoCodecType(string? s, VideoCodecType defaultValue = VideoCodecType.H264)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoCodecType>(s, true, out var r) ? r : defaultValue;

    public static VideoContainerKind ParseVideoContainerKind(string? s, VideoContainerKind defaultValue = VideoContainerKind.Mp4)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoContainerKind>(s, true, out var r) ? r : defaultValue;

    public static VideoFrameratePreset ParseVideoFrameratePreset(string? s, VideoFrameratePreset defaultValue = VideoFrameratePreset.Fps30)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            var lower = s.ToLowerInvariant().Trim();
            if (lower.Contains("24")) return VideoFrameratePreset.Fps24;
            if (lower.Contains("25")) return VideoFrameratePreset.Fps25;
            if (lower.Contains("30")) return VideoFrameratePreset.Fps30;
            if (lower.Contains("50")) return VideoFrameratePreset.Fps50;
            if (lower.Contains("60")) return VideoFrameratePreset.Fps60;
            if (lower.Contains("120")) return VideoFrameratePreset.Fps120;
            return Enum.TryParse<VideoFrameratePreset>(s, true, out var r) ? r : defaultValue;
        }

    public static VideoResolutionPreset ParseVideoResolutionPreset(string? s, VideoResolutionPreset defaultValue = VideoResolutionPreset.Res1080p)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            var lower = s.ToLowerInvariant().Trim();
            return lower switch
            {
                "720p" => VideoResolutionPreset.Res720p,
                "1080p" => VideoResolutionPreset.Res1080p,
                "1440p" or "2k" => VideoResolutionPreset.Res1440p,
                "4k" or "2160p" => VideoResolutionPreset.Res4k,
                "8k" or "4320p" => VideoResolutionPreset.Res8k,
                _ => Enum.TryParse<VideoResolutionPreset>(s, true, out var r) ? r : defaultValue
            };
        }

    public static VoiceAgeGroupFilter ParseVoiceAgeGroupFilter(string? s, VoiceAgeGroupFilter defaultValue = VoiceAgeGroupFilter.Adult)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceAgeGroupFilter>(s, true, out var r) ? r : defaultValue;

    public static VoiceGenderFilter ParseVoiceGenderFilter(string? s, VoiceGenderFilter defaultValue = VoiceGenderFilter.Unspecified)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceGenderFilter>(s, true, out var r) ? r : defaultValue;

    public static VoiceProviderKind ParseVoiceProviderKind(string? s, VoiceProviderKind defaultValue = VoiceProviderKind.ElevenLabs)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceProviderKind>(s, true, out var r) ? r : defaultValue;

    public static WorkerPoolName ParseWorkerPoolName(string? s, WorkerPoolName defaultValue = WorkerPoolName.GeneralWorkerPool)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            var lower = s.ToLowerInvariant().Trim();
            if (lower.Contains("api")) return WorkerPoolName.ApiWorkerPool;
            if (lower.Contains("local")) return WorkerPoolName.LocalWorkerPool;
            if (lower.Contains("image")) return WorkerPoolName.ImageWorkerPool;
            if (lower.Contains("video")) return WorkerPoolName.VideoWorkerPool;
            if (lower.Contains("audio")) return WorkerPoolName.AudioWorkerPool;
            return Enum.TryParse<WorkerPoolName>(s, true, out var r) ? r : defaultValue;
        }

    public static WorkerPoolQueueStrategy ParseWorkerPoolQueueStrategy(string? s, WorkerPoolQueueStrategy defaultValue = WorkerPoolQueueStrategy.Fifo)
            => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<WorkerPoolQueueStrategy>(s, true, out var r) ? r : defaultValue;

    public static string ToApiString(this JobPriorityLevel val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this WorkerPoolName val) => val switch
        {
            WorkerPoolName.ApiWorkerPool => "api_pool",
            WorkerPoolName.LocalWorkerPool => "local_pool",
            WorkerPoolName.ImageWorkerPool => "image_pool",
            WorkerPoolName.VideoWorkerPool => "video_pool",
            WorkerPoolName.AudioWorkerPool => "audio_pool",
            _ => "general_pool"
        };

    public static string ToApiString(this WorkerPoolQueueStrategy val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ErrorSeverityLevel val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this CacheStrategyType val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this CacheInvalidationTrigger val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this RetryPolicyStrategy val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ShotAngleType val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this CameraLensSpec val) => val switch
        {
            CameraLensSpec.Lens14mm => "14mm",
            CameraLensSpec.Lens24mm => "24mm",
            CameraLensSpec.Lens35mm => "35mm",
            CameraLensSpec.Lens50mm => "50mm",
            CameraLensSpec.Lens85mm => "85mm",
            CameraLensSpec.Lens135mm => "135mm",
            CameraLensSpec.Lens200mm => "200mm",
            CameraLensSpec.Anamorphic => "anamorphic",
            _ => "35mm"
        };

    public static string ToApiString(this CameraMovementPattern val) => val switch
        {
            CameraMovementPattern.Static => "static",
            CameraMovementPattern.PushIn => "push_in",
            CameraMovementPattern.PullOut => "pull_out",
            CameraMovementPattern.PanLeft => "pan_left",
            CameraMovementPattern.PanRight => "pan_right",
            CameraMovementPattern.TiltUp => "tilt_up",
            CameraMovementPattern.TiltDown => "tilt_down",
            CameraMovementPattern.Tracking => "tracking",
            CameraMovementPattern.Crane => "crane",
            CameraMovementPattern.Handheld => "handheld",
            _ => "static"
        };

    public static string ToApiString(this BeatActionCategory val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this LightingStyleType val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ColorPalettePreset val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this PacingMoodBand val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ClipDurationBudgetMode val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ClipGenRetryStrategy val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VoiceProviderKind val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VoiceGenderFilter val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VoiceAgeGroupFilter val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this AudioCodecType val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this AudioContainerType val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this SpeechSubstitutionScope val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this SilenceDetectorPreset val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this AudioMixBalanceMode val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this MusicGenrePreset val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this MusicTempoCategory val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this MediaAssetType val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this StorageTierKind val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VideoCodecType val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VideoContainerKind val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VideoResolutionPreset val) => val switch
        {
            VideoResolutionPreset.Res720p => "720p",
            VideoResolutionPreset.Res1080p => "1080p",
            VideoResolutionPreset.Res1440p => "1440p",
            VideoResolutionPreset.Res4k => "4k",
            VideoResolutionPreset.Res8k => "8k",
            _ => "1080p"
        };

    public static string ToApiString(this VideoFrameratePreset val) => val switch
        {
            VideoFrameratePreset.Fps24 => "24fps",
            VideoFrameratePreset.Fps25 => "25fps",
            VideoFrameratePreset.Fps30 => "30fps",
            VideoFrameratePreset.Fps50 => "50fps",
            VideoFrameratePreset.Fps60 => "60fps",
            VideoFrameratePreset.Fps120 => "120fps",
            _ => "30fps"
        };

    public static string ToApiString(this AspectRatioPreset val) => val switch
        {
            AspectRatioPreset.Ratio16x9 => "16:9",
            AspectRatioPreset.Ratio9x16 => "9:16",
            AspectRatioPreset.Ratio1x1 => "1:1",
            AspectRatioPreset.Ratio4x3 => "4:3",
            AspectRatioPreset.Ratio21x9 => "21:9",
            _ => "16:9"
        };

    public static string ToApiString(this SubtitleFormatPreset val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this SubtitlePositionPreset val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ExportQualityLevel val) => val.ToString().ToLowerInvariant();

}

#endregion
