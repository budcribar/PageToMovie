using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine;

#region Video & FFmpeg Remux Enums (101-120)

/// <summary>
/// Video codec names supported for FFmpeg remuxing and transcoding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoCodecName
{
    H264,
    H265,
    Vp8,
    Vp9,
    Av1,
    ProRes,
    DnxHd,
    Copy
}

/// <summary>
/// Pixel formats used in FFmpeg video encoding and filtering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoPixelFormat
{
    Yuv420p,
    Yuv422p,
    Yuv444p,
    Yuv420p10le,
    Rgb24,
    Rgba,
    Nv12
}

/// <summary>
/// Color space standard specifications for video processing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoColorSpace
{
    Bt709,
    Bt2020,
    Bt601,
    Smpte170m,
    Unspecified
}

/// <summary>
/// Bitrate control modes for FFmpeg video encoding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoBitrateControlMode
{
    Cbr,
    Vbr,
    Crf,
    TwoPass,
    Auto
}

/// <summary>
/// Group-of-Pictures (GOP) structural settings for video streams.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoGopStructure
{
    ClosedGop,
    OpenGop,
    AllIntra,
    FixedLength,
    Auto
}

/// <summary>
/// FFmpeg encoder preset speeds balancing compression efficiency and encoding time.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FfmpegPresetSpeed
{
    Ultrafast,
    Superfast,
    Veryfast,
    Faster,
    Fast,
    Medium,
    Slow,
    Slower,
    Veryslow,
    Placebo
}

/// <summary>
/// Log verbosity levels for FFmpeg process execution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FfmpegLogVerbosity
{
    Quiet,
    Panic,
    Fatal,
    Error,
    Warning,
    Info,
    Verbose,
    Debug,
    Trace
}

/// <summary>
/// Scaling algorithms used when resizing video frames in FFmpeg filters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoScaleAlgorithm
{
    Bicubic,
    Bilinear,
    Lanczos,
    Spline,
    Neighbor,
    FastBilinear
}

/// <summary>
/// Aspect ratio preservation and fitting strategies for video output.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoAspectMode
{
    Letterbox,
    Pillarbox,
    CropToFill,
    Stretch,
    Pad,
    Preserve
}

/// <summary>
/// Placement positions for video watermarks or overlay logos.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoWatermarkPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
    Custom
}

/// <summary>
/// Visual transition types between adjacent video clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoTransitionType
{
    Fade,
    Dissolve,
    WipeLeft,
    WipeRight,
    SlideUp,
    SlideDown,
    ZoomIn,
    ZoomOut,
    Crossfade,
    None
}

/// <summary>
/// Font family choices for burned-in subtitles and captions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleFontFamily
{
    Arial,
    Roboto,
    Inter,
    Helvetica,
    TimesNewRoman,
    CourierNew,
    Montserrat,
    Custom
}

/// <summary>
/// Sizing category presets for subtitle text overlays.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleFontSizeCategory
{
    Small,
    Medium,
    Large,
    ExtraLarge,
    Custom
}

/// <summary>
/// Screen alignment presets for subtitle placement.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleAlignmentPreset
{
    BottomCenter,
    BottomLeft,
    BottomRight,
    TopCenter,
    TopLeft,
    TopRight,
    MiddleCenter,
    Custom
}

/// <summary>
/// Border and background rendering modes for subtitle text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleBorderMode
{
    None,
    Outline,
    OpaqueBox,
    Shadow,
    OutlineAndShadow
}

/// <summary>
/// Execution pipeline stages for video remux and concat jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemuxJobStage
{
    Pending,
    AnalyzingInput,
    ExtractingTracks,
    TranscodingVideo,
    TranscodingAudio,
    Concatenating,
    BurningSubtitles,
    MuxingOutput,
    ValidatingOutput,
    Completed,
    Failed
}

/// <summary>
/// Hardware acceleration decoders and encoders for FFmpeg execution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoHardwareAcceleration
{
    None,
    Cuda,
    Nvenc,
    Qsv,
    Vaapi,
    Videotoolbox,
    Dxva2,
    Auto
}

/// <summary>
/// Frame rate interpolation algorithms for retiming video footage.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoFrameInterpolation
{
    None,
    Minterpolate,
    FrameBlend,
    MotionCompensated,
    OpticalFlow
}

/// <summary>
/// Time position reference points for video thumbnail generation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoThumbnailTimeRef
{
    Start,
    Middle,
    End,
    FirstKeyframe,
    CustomOffset,
    Percentage
}

/// <summary>
/// Naming patterns for generated export video files.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExportFileNamingPattern
{
    ProjectAndTimestamp,
    SceneAndBeat,
    TitleOnly,
    SequentialIndex,
    Custom
}

#endregion

#region Extensions & Parsing

/// <summary>
/// Extension methods and string parsers for VideoFFmpegEnums.
/// </summary>
public static class VideoFfmpegEnumExtensions
{
    private const string CustomApi = "custom";

    public static string ToApiString(this VideoCodecName val) => val switch
    {
        VideoCodecName.H264 => "h264",
        VideoCodecName.H265 => "h265",
        VideoCodecName.Vp8 => "vp8",
        VideoCodecName.Vp9 => "vp9",
        VideoCodecName.Av1 => "av1",
        VideoCodecName.ProRes => "prores",
        VideoCodecName.DnxHd => "dnxhd",
        VideoCodecName.Copy => "copy",
        _ => "h264"
    };

    public static string ToApiString(this VideoPixelFormat val) => val switch
    {
        VideoPixelFormat.Yuv420p => "yuv420p",
        VideoPixelFormat.Yuv422p => "yuv422p",
        VideoPixelFormat.Yuv444p => "yuv444p",
        VideoPixelFormat.Yuv420p10le => "yuv420p10le",
        VideoPixelFormat.Rgb24 => "rgb24",
        VideoPixelFormat.Rgba => "rgba",
        VideoPixelFormat.Nv12 => "nv12",
        _ => "yuv420p"
    };

    public static string ToApiString(this VideoColorSpace val) => val switch
    {
        VideoColorSpace.Bt709 => "bt709",
        VideoColorSpace.Bt2020 => "bt2020",
        VideoColorSpace.Bt601 => "bt601",
        VideoColorSpace.Smpte170m => "smpte170m",
        VideoColorSpace.Unspecified => "unspecified",
        _ => "bt709"
    };

    public static string ToApiString(this VideoBitrateControlMode val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VideoGopStructure val) => val switch
    {
        VideoGopStructure.ClosedGop => "closed_gop",
        VideoGopStructure.OpenGop => "open_gop",
        VideoGopStructure.AllIntra => "all_intra",
        VideoGopStructure.FixedLength => "fixed_length",
        VideoGopStructure.Auto => "auto",
        _ => "auto"
    };

    public static string ToApiString(this FfmpegPresetSpeed val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this FfmpegLogVerbosity val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VideoScaleAlgorithm val) => val switch
    {
        VideoScaleAlgorithm.Bicubic => "bicubic",
        VideoScaleAlgorithm.Bilinear => "bilinear",
        VideoScaleAlgorithm.Lanczos => "lanczos",
        VideoScaleAlgorithm.Spline => "spline",
        VideoScaleAlgorithm.Neighbor => "neighbor",
        VideoScaleAlgorithm.FastBilinear => "fast_bilinear",
        _ => "bicubic"
    };

    public static string ToApiString(this VideoAspectMode val) => val switch
    {
        VideoAspectMode.Letterbox => "letterbox",
        VideoAspectMode.Pillarbox => "pillarbox",
        VideoAspectMode.CropToFill => "crop_to_fill",
        VideoAspectMode.Stretch => "stretch",
        VideoAspectMode.Pad => "pad",
        VideoAspectMode.Preserve => "preserve",
        _ => "letterbox"
    };

    public static string ToApiString(this VideoWatermarkPosition val) => val switch
    {
        VideoWatermarkPosition.TopLeft => "top_left",
        VideoWatermarkPosition.TopRight => "top_right",
        VideoWatermarkPosition.BottomLeft => "bottom_left",
        VideoWatermarkPosition.BottomRight => "bottom_right",
        VideoWatermarkPosition.Center => "center",
        VideoWatermarkPosition.Custom => CustomApi,
        _ => "bottom_right"
    };

    public static string ToApiString(this VideoTransitionType val) => val switch
    {
        VideoTransitionType.Fade => "fade",
        VideoTransitionType.Dissolve => "dissolve",
        VideoTransitionType.WipeLeft => "wipe_left",
        VideoTransitionType.WipeRight => "wipe_right",
        VideoTransitionType.SlideUp => "slide_up",
        VideoTransitionType.SlideDown => "slide_down",
        VideoTransitionType.ZoomIn => "zoom_in",
        VideoTransitionType.ZoomOut => "zoom_out",
        VideoTransitionType.Crossfade => "crossfade",
        VideoTransitionType.None => "none",
        _ => "none"
    };

    public static string ToApiString(this SubtitleFontFamily val) => val switch
    {
        SubtitleFontFamily.Arial => "arial",
        SubtitleFontFamily.Roboto => "roboto",
        SubtitleFontFamily.Inter => "inter",
        SubtitleFontFamily.Helvetica => "helvetica",
        SubtitleFontFamily.TimesNewRoman => "times_new_roman",
        SubtitleFontFamily.CourierNew => "courier_new",
        SubtitleFontFamily.Montserrat => "montserrat",
        SubtitleFontFamily.Custom => CustomApi,
        _ => "inter"
    };

    public static string ToApiString(this SubtitleFontSizeCategory val) => val switch
    {
        SubtitleFontSizeCategory.Small => "small",
        SubtitleFontSizeCategory.Medium => "medium",
        SubtitleFontSizeCategory.Large => "large",
        SubtitleFontSizeCategory.ExtraLarge => "extra_large",
        SubtitleFontSizeCategory.Custom => CustomApi,
        _ => "medium"
    };

    public static string ToApiString(this SubtitleAlignmentPreset val) => val switch
    {
        SubtitleAlignmentPreset.BottomCenter => "bottom_center",
        SubtitleAlignmentPreset.BottomLeft => "bottom_left",
        SubtitleAlignmentPreset.BottomRight => "bottom_right",
        SubtitleAlignmentPreset.TopCenter => "top_center",
        SubtitleAlignmentPreset.TopLeft => "top_left",
        SubtitleAlignmentPreset.TopRight => "top_right",
        SubtitleAlignmentPreset.MiddleCenter => "middle_center",
        SubtitleAlignmentPreset.Custom => CustomApi,
        _ => "bottom_center"
    };

    public static string ToApiString(this SubtitleBorderMode val) => val switch
    {
        SubtitleBorderMode.None => "none",
        SubtitleBorderMode.Outline => "outline",
        SubtitleBorderMode.OpaqueBox => "opaque_box",
        SubtitleBorderMode.Shadow => "shadow",
        SubtitleBorderMode.OutlineAndShadow => "outline_and_shadow",
        _ => "outline"
    };

    public static string ToApiString(this RemuxJobStage val) => val switch
    {
        RemuxJobStage.Pending => "pending",
        RemuxJobStage.AnalyzingInput => "analyzing_input",
        RemuxJobStage.ExtractingTracks => "extracting_tracks",
        RemuxJobStage.TranscodingVideo => "transcoding_video",
        RemuxJobStage.TranscodingAudio => "transcoding_audio",
        RemuxJobStage.Concatenating => "concatenating",
        RemuxJobStage.BurningSubtitles => "burning_subtitles",
        RemuxJobStage.MuxingOutput => "muxing_output",
        RemuxJobStage.ValidatingOutput => "validating_output",
        RemuxJobStage.Completed => "completed",
        RemuxJobStage.Failed => "failed",
        _ => "pending"
    };

    public static string ToApiString(this VideoHardwareAcceleration val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this VideoFrameInterpolation val) => val switch
    {
        VideoFrameInterpolation.None => "none",
        VideoFrameInterpolation.Minterpolate => "minterpolate",
        VideoFrameInterpolation.FrameBlend => "frame_blend",
        VideoFrameInterpolation.MotionCompensated => "motion_compensated",
        VideoFrameInterpolation.OpticalFlow => "optical_flow",
        _ => "none"
    };

    public static string ToApiString(this VideoThumbnailTimeRef val) => val switch
    {
        VideoThumbnailTimeRef.Start => "start",
        VideoThumbnailTimeRef.Middle => "middle",
        VideoThumbnailTimeRef.End => "end",
        VideoThumbnailTimeRef.FirstKeyframe => "first_keyframe",
        VideoThumbnailTimeRef.CustomOffset => "custom_offset",
        VideoThumbnailTimeRef.Percentage => "percentage",
        _ => "first_keyframe"
    };

    public static string ToApiString(this ExportFileNamingPattern val) => val switch
    {
        ExportFileNamingPattern.ProjectAndTimestamp => "project_timestamp",
        ExportFileNamingPattern.SceneAndBeat => "scene_beat",
        ExportFileNamingPattern.TitleOnly => "title_only",
        ExportFileNamingPattern.SequentialIndex => "sequential_index",
        ExportFileNamingPattern.Custom => CustomApi,
        _ => "project_timestamp"
    };


    public static VideoCodecName ParseVideoCodecName(string? s, VideoCodecName defaultValue = VideoCodecName.H264)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var lower = s.ToLowerInvariant().Trim();
        if (lower.Contains("264")) return VideoCodecName.H264;
        if (lower.Contains("265") || lower.Contains("hevc")) return VideoCodecName.H265;
        if (lower.Contains("vp8")) return VideoCodecName.Vp8;
        if (lower.Contains("vp9")) return VideoCodecName.Vp9;
        if (lower.Contains("av1")) return VideoCodecName.Av1;
        if (lower.Contains("prores")) return VideoCodecName.ProRes;
        if (lower.Contains("dnx")) return VideoCodecName.DnxHd;
        if (lower.Contains("copy")) return VideoCodecName.Copy;
        return Enum.TryParse<VideoCodecName>(s, true, out var r) ? r : defaultValue;
    }

    public static VideoPixelFormat ParseVideoPixelFormat(string? s, VideoPixelFormat defaultValue = VideoPixelFormat.Yuv420p)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoPixelFormat>(s, true, out var r) ? r : defaultValue;

    public static VideoColorSpace ParseVideoColorSpace(string? s, VideoColorSpace defaultValue = VideoColorSpace.Bt709)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoColorSpace>(s, true, out var r) ? r : defaultValue;

    public static VideoBitrateControlMode ParseVideoBitrateControlMode(string? s, VideoBitrateControlMode defaultValue = VideoBitrateControlMode.Crf)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoBitrateControlMode>(s, true, out var r) ? r : defaultValue;

    public static VideoGopStructure ParseVideoGopStructure(string? s, VideoGopStructure defaultValue = VideoGopStructure.Auto)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoGopStructure>(s, true, out var r) ? r : defaultValue;

    public static FfmpegPresetSpeed ParseFfmpegPresetSpeed(string? s, FfmpegPresetSpeed defaultValue = FfmpegPresetSpeed.Medium)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FfmpegPresetSpeed>(s, true, out var r) ? r : defaultValue;

    public static FfmpegLogVerbosity ParseFfmpegLogVerbosity(string? s, FfmpegLogVerbosity defaultValue = FfmpegLogVerbosity.Info)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FfmpegLogVerbosity>(s, true, out var r) ? r : defaultValue;

    public static VideoScaleAlgorithm ParseVideoScaleAlgorithm(string? s, VideoScaleAlgorithm defaultValue = VideoScaleAlgorithm.Bicubic)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoScaleAlgorithm>(s, true, out var r) ? r : defaultValue;

    public static VideoAspectMode ParseVideoAspectMode(string? s, VideoAspectMode defaultValue = VideoAspectMode.Letterbox)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoAspectMode>(s, true, out var r) ? r : defaultValue;

    public static VideoWatermarkPosition ParseVideoWatermarkPosition(string? s, VideoWatermarkPosition defaultValue = VideoWatermarkPosition.BottomRight)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoWatermarkPosition>(s, true, out var r) ? r : defaultValue;

    public static VideoTransitionType ParseVideoTransitionType(string? s, VideoTransitionType defaultValue = VideoTransitionType.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoTransitionType>(s, true, out var r) ? r : defaultValue;

    public static SubtitleFontFamily ParseSubtitleFontFamily(string? s, SubtitleFontFamily defaultValue = SubtitleFontFamily.Inter)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleFontFamily>(s, true, out var r) ? r : defaultValue;

    public static SubtitleFontSizeCategory ParseSubtitleFontSizeCategory(string? s, SubtitleFontSizeCategory defaultValue = SubtitleFontSizeCategory.Medium)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleFontSizeCategory>(s, true, out var r) ? r : defaultValue;

    public static SubtitleAlignmentPreset ParseSubtitleAlignmentPreset(string? s, SubtitleAlignmentPreset defaultValue = SubtitleAlignmentPreset.BottomCenter)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleAlignmentPreset>(s, true, out var r) ? r : defaultValue;

    public static SubtitleBorderMode ParseSubtitleBorderMode(string? s, SubtitleBorderMode defaultValue = SubtitleBorderMode.Outline)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleBorderMode>(s, true, out var r) ? r : defaultValue;

    public static RemuxJobStage ParseRemuxJobStage(string? s, RemuxJobStage defaultValue = RemuxJobStage.Pending)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<RemuxJobStage>(s, true, out var r) ? r : defaultValue;

    public static VideoHardwareAcceleration ParseVideoHardwareAcceleration(string? s, VideoHardwareAcceleration defaultValue = VideoHardwareAcceleration.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoHardwareAcceleration>(s, true, out var r) ? r : defaultValue;

    public static VideoFrameInterpolation ParseVideoFrameInterpolation(string? s, VideoFrameInterpolation defaultValue = VideoFrameInterpolation.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoFrameInterpolation>(s, true, out var r) ? r : defaultValue;

    public static VideoThumbnailTimeRef ParseVideoThumbnailTimeRef(string? s, VideoThumbnailTimeRef defaultValue = VideoThumbnailTimeRef.FirstKeyframe)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoThumbnailTimeRef>(s, true, out var r) ? r : defaultValue;

    public static ExportFileNamingPattern ParseExportFileNamingPattern(string? s, ExportFileNamingPattern defaultValue = ExportFileNamingPattern.ProjectAndTimestamp)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ExportFileNamingPattern>(s, true, out var r) ? r : defaultValue;

}

#endregion
