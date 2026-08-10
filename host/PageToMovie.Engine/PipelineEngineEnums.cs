namespace PageToMovie.Engine;

public enum LightingCondition
{
    Daylight,
    NightInterior,
    GoldenHour,
    NeonLight
}

public enum CameraAngle
{
    LowAngle,
    HighAngle,
    EyeLevel,
    BirdEye
}

public enum SubtitlePosition
{
    Bottom,
    Top,
    Middle
}

public enum FfmpegFilterKind
{
    SilenceDetect,
    DrawText,
    Scale,
    Concat
}

public enum AudioMixingMode
{
    DialogueHeavy,
    Balanced,
    MusicHeavy
}

public enum CacheInvalidationReason
{
    JobCompleted,
    UserEdit,
    ProjectDeleted
}

public enum VoiceCloneEngine
{
    ElevenLabsV2,
    AzureSpeech,
    SystemSynthetic
}

public enum ImageGenEngine
{
    GrokImagine,
    Dalle3,
    Flux,
    Imagen
}

public enum VideoGenEngine
{
    GrokVideo,
    Veo,
    Runway,
    Kling
}

public enum AudioCodec
{
    Mp3,
    Aac,
    Pcm,
    Opus
}

public enum VideoCodec
{
    H264,
    Hevc,
    Vp9,
    Av1
}

public enum Stage2JobType
{
    Plan,
    Generate,
    Remux
}
