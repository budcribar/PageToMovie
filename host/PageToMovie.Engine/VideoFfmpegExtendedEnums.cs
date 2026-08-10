using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine;

#region Extended Video & FFmpeg Enums (101-120)

/// <summary>
/// Video codec names supported for FFmpeg remuxing and transcoding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoCodecNameKind
{
    H264,
    H265,
    Vp8,
    Vp9,
    Av1,
    ProRes,
    DnxHd,
    Copy,
    Unknown
}

/// <summary>
/// Pixel formats used in FFmpeg video encoding and filtering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoPixelFormatKind
{
    Yuv420p,
    Yuv422p,
    Yuv444p,
    Yuv420p10le,
    Rgb24,
    Rgba,
    Nv12,
    Auto
}

/// <summary>
/// Color space standard specifications for video processing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoColorSpaceKind
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
public enum VideoBitrateControlKind
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
public enum VideoGopStructureKind
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
public enum FfmpegPresetSpeedKind
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
public enum FfmpegLogVerbosityKind
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
/// Video scaling algorithms for FFmpeg resizer filter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoScaleAlgorithmKind
{
    Bilinear,
    Bicubic,
    Lanczos,
    Spline,
    NearestNeighbor,
    Area,
    Auto
}

/// <summary>
/// Aspect ratio scaling and cropping mode for video clip sizing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoAspectModeKind
{
    Fit,
    Fill,
    Stretch,
    Pad,
    Crop,
    Original
}

/// <summary>
/// Watermark positioning presets on exported video frames.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoWatermarkPositionKind
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
    Custom
}

/// <summary>
/// Visual transition effect types between consecutive video clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoTransitionTypeKind
{
    None,
    Fade,
    Dissolve,
    WipeLeft,
    WipeRight,
    WipeUp,
    WipeDown,
    ZoomIn,
    ZoomOut,
    Crossfade
}

/// <summary>
/// Font families supported for burned subtitle overlays.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleFontFamilyKind
{
    Arial,
    Helvetica,
    TimesNewRoman,
    CourierNew,
    Verdana,
    Georgia,
    Custom
}

/// <summary>
/// Font size category classification for subtitle text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleFontSizeKind
{
    Small,
    Medium,
    Large,
    ExtraLarge,
    Custom
}

/// <summary>
/// Alignment and position anchor presets for subtitle rendering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleAlignmentKind
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>
/// Subtitle border and background box styling options.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleBorderModeKind
{
    None,
    Outline,
    OpaqueBox,
    Shadow,
    OutlineAndShadow
}

/// <summary>
/// Processing stages for FFmpeg remux and assembly pipeline jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemuxJobStageKind
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
/// Hardware acceleration API backends for FFmpeg decoding and encoding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoHardwareAccelKind
{
    None,
    Nvenc,
    Qsv,
    Vaapi,
    Videotoolbox,
    Cuda,
    Amf,
    Auto
}

/// <summary>
/// Frame interpolation strategies for converting clip framerates.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoFrameInterpolationKind
{
    None,
    Minterpolate,
    FrameBlend,
    MotionCompensated,
    OpticalFlow
}

/// <summary>
/// Reference points for selecting thumbnail image frames from clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoThumbnailTimeKind
{
    Start,
    Middle,
    End,
    FirstKeyframe,
    CustomOffset,
    Percentage
}

/// <summary>
/// Standard file naming patterns for exported video files.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExportFileNamingPatternKind
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
/// Extension methods and string parsers for VideoFfmpegExtendedEnums.
/// </summary>
public static class VideoFfmpegExtendedEnumExtensions
{
    public static string ToApiString(this VideoCodecNameKind val) => val switch
    {
        VideoCodecNameKind.H264 => "h264",
        VideoCodecNameKind.H265 => "h265",
        VideoCodecNameKind.Vp8 => "vp8",
        VideoCodecNameKind.Vp9 => "vp9",
        VideoCodecNameKind.Av1 => "av1",
        VideoCodecNameKind.ProRes => "prores",
        VideoCodecNameKind.DnxHd => "dnxhd",
        VideoCodecNameKind.Copy => "copy",
        _ => "unknown"
    };
    public static VideoCodecNameKind ParseVideoCodecNameKind(string? s, VideoCodecNameKind defaultValue = VideoCodecNameKind.H264)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var lower = s.ToLowerInvariant().Trim();
        if (lower.Contains("264")) return VideoCodecNameKind.H264;
        if (lower.Contains("265") || lower.Contains("hevc")) return VideoCodecNameKind.H265;
        if (lower.Contains("vp8")) return VideoCodecNameKind.Vp8;
        if (lower.Contains("vp9")) return VideoCodecNameKind.Vp9;
        if (lower.Contains("av1")) return VideoCodecNameKind.Av1;
        if (lower.Contains("prores")) return VideoCodecNameKind.ProRes;
        if (lower.Contains("dnx")) return VideoCodecNameKind.DnxHd;
        if (lower.Contains("copy")) return VideoCodecNameKind.Copy;
        return Enum.TryParse<VideoCodecNameKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static VideoCodecNameKind ToVideoCodecNameKind(this string? s, VideoCodecNameKind defaultValue = VideoCodecNameKind.H264)
        => ParseVideoCodecNameKind(s, defaultValue);

    public static string ToApiString(this VideoPixelFormatKind val) => val switch
    {
        VideoPixelFormatKind.Yuv420p => "yuv420p",
        VideoPixelFormatKind.Yuv422p => "yuv422p",
        VideoPixelFormatKind.Yuv444p => "yuv444p",
        VideoPixelFormatKind.Yuv420p10le => "yuv420p10le",
        VideoPixelFormatKind.Rgb24 => "rgb24",
        VideoPixelFormatKind.Rgba => "rgba",
        VideoPixelFormatKind.Nv12 => "nv12",
        _ => "auto"
    };
    public static VideoPixelFormatKind ParseVideoPixelFormatKind(string? s, VideoPixelFormatKind defaultValue = VideoPixelFormatKind.Yuv420p)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoPixelFormatKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoPixelFormatKind ToVideoPixelFormatKind(this string? s, VideoPixelFormatKind defaultValue = VideoPixelFormatKind.Yuv420p)
        => ParseVideoPixelFormatKind(s, defaultValue);

    public static string ToApiString(this VideoColorSpaceKind val) => val switch
    {
        VideoColorSpaceKind.Bt709 => "bt709",
        VideoColorSpaceKind.Bt2020 => "bt2020",
        VideoColorSpaceKind.Bt601 => "bt601",
        VideoColorSpaceKind.Smpte170m => "smpte170m",
        _ => "unspecified"
    };
    public static VideoColorSpaceKind ParseVideoColorSpaceKind(string? s, VideoColorSpaceKind defaultValue = VideoColorSpaceKind.Bt709)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoColorSpaceKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoColorSpaceKind ToVideoColorSpaceKind(this string? s, VideoColorSpaceKind defaultValue = VideoColorSpaceKind.Bt709)
        => ParseVideoColorSpaceKind(s, defaultValue);

    public static string ToApiString(this VideoBitrateControlKind val) => val.ToString().ToLowerInvariant();
    public static VideoBitrateControlKind ParseVideoBitrateControlKind(string? s, VideoBitrateControlKind defaultValue = VideoBitrateControlKind.Crf)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoBitrateControlKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoBitrateControlKind ToVideoBitrateControlKind(this string? s, VideoBitrateControlKind defaultValue = VideoBitrateControlKind.Crf)
        => ParseVideoBitrateControlKind(s, defaultValue);

    public static string ToApiString(this VideoGopStructureKind val) => val switch
    {
        VideoGopStructureKind.ClosedGop => "closed_gop",
        VideoGopStructureKind.OpenGop => "open_gop",
        VideoGopStructureKind.AllIntra => "all_intra",
        VideoGopStructureKind.FixedLength => "fixed_length",
        _ => "auto"
    };
    public static VideoGopStructureKind ParseVideoGopStructureKind(string? s, VideoGopStructureKind defaultValue = VideoGopStructureKind.Auto)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoGopStructureKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoGopStructureKind ToVideoGopStructureKind(this string? s, VideoGopStructureKind defaultValue = VideoGopStructureKind.Auto)
        => ParseVideoGopStructureKind(s, defaultValue);

    public static string ToApiString(this FfmpegPresetSpeedKind val) => val.ToString().ToLowerInvariant();
    public static FfmpegPresetSpeedKind ParseFfmpegPresetSpeedKind(string? s, FfmpegPresetSpeedKind defaultValue = FfmpegPresetSpeedKind.Medium)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FfmpegPresetSpeedKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FfmpegPresetSpeedKind ToFfmpegPresetSpeedKind(this string? s, FfmpegPresetSpeedKind defaultValue = FfmpegPresetSpeedKind.Medium)
        => ParseFfmpegPresetSpeedKind(s, defaultValue);

    public static string ToApiString(this FfmpegLogVerbosityKind val) => val.ToString().ToLowerInvariant();
    public static FfmpegLogVerbosityKind ParseFfmpegLogVerbosityKind(string? s, FfmpegLogVerbosityKind defaultValue = FfmpegLogVerbosityKind.Info)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FfmpegLogVerbosityKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FfmpegLogVerbosityKind ToFfmpegLogVerbosityKind(this string? s, FfmpegLogVerbosityKind defaultValue = FfmpegLogVerbosityKind.Info)
        => ParseFfmpegLogVerbosityKind(s, defaultValue);

    public static string ToApiString(this VideoScaleAlgorithmKind val) => val switch
    {
        VideoScaleAlgorithmKind.Bilinear => "bilinear",
        VideoScaleAlgorithmKind.Bicubic => "bicubic",
        VideoScaleAlgorithmKind.Lanczos => "lanczos",
        VideoScaleAlgorithmKind.Spline => "spline",
        VideoScaleAlgorithmKind.NearestNeighbor => "nearest_neighbor",
        VideoScaleAlgorithmKind.Area => "area",
        _ => "auto"
    };
    public static VideoScaleAlgorithmKind ParseVideoScaleAlgorithmKind(string? s, VideoScaleAlgorithmKind defaultValue = VideoScaleAlgorithmKind.Bicubic)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoScaleAlgorithmKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoScaleAlgorithmKind ToVideoScaleAlgorithmKind(this string? s, VideoScaleAlgorithmKind defaultValue = VideoScaleAlgorithmKind.Bicubic)
        => ParseVideoScaleAlgorithmKind(s, defaultValue);

    public static string ToApiString(this VideoAspectModeKind val) => val.ToString().ToLowerInvariant();
    public static VideoAspectModeKind ParseVideoAspectModeKind(string? s, VideoAspectModeKind defaultValue = VideoAspectModeKind.Fit)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoAspectModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoAspectModeKind ToVideoAspectModeKind(this string? s, VideoAspectModeKind defaultValue = VideoAspectModeKind.Fit)
        => ParseVideoAspectModeKind(s, defaultValue);

    public static string ToApiString(this VideoWatermarkPositionKind val) => val switch
    {
        VideoWatermarkPositionKind.TopLeft => "top_left",
        VideoWatermarkPositionKind.TopRight => "top_right",
        VideoWatermarkPositionKind.BottomLeft => "bottom_left",
        VideoWatermarkPositionKind.BottomRight => "bottom_right",
        VideoWatermarkPositionKind.Center => "center",
        VideoWatermarkPositionKind.Custom => "custom",
        _ => "bottom_right"
    };
    public static VideoWatermarkPositionKind ParseVideoWatermarkPositionKind(string? s, VideoWatermarkPositionKind defaultValue = VideoWatermarkPositionKind.BottomRight)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoWatermarkPositionKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoWatermarkPositionKind ToVideoWatermarkPositionKind(this string? s, VideoWatermarkPositionKind defaultValue = VideoWatermarkPositionKind.BottomRight)
        => ParseVideoWatermarkPositionKind(s, defaultValue);

    public static string ToApiString(this VideoTransitionTypeKind val) => val switch
    {
        VideoTransitionTypeKind.WipeLeft => "wipe_left",
        VideoTransitionTypeKind.WipeRight => "wipe_right",
        VideoTransitionTypeKind.WipeUp => "wipe_up",
        VideoTransitionTypeKind.WipeDown => "wipe_down",
        VideoTransitionTypeKind.ZoomIn => "zoom_in",
        VideoTransitionTypeKind.ZoomOut => "zoom_out",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VideoTransitionTypeKind ParseVideoTransitionTypeKind(string? s, VideoTransitionTypeKind defaultValue = VideoTransitionTypeKind.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoTransitionTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoTransitionTypeKind ToVideoTransitionTypeKind(this string? s, VideoTransitionTypeKind defaultValue = VideoTransitionTypeKind.None)
        => ParseVideoTransitionTypeKind(s, defaultValue);

    public static string ToApiString(this SubtitleFontFamilyKind val) => val switch
    {
        SubtitleFontFamilyKind.TimesNewRoman => "times_new_roman",
        SubtitleFontFamilyKind.CourierNew => "courier_new",
        _ => val.ToString().ToLowerInvariant()
    };
    public static SubtitleFontFamilyKind ParseSubtitleFontFamilyKind(string? s, SubtitleFontFamilyKind defaultValue = SubtitleFontFamilyKind.Arial)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleFontFamilyKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SubtitleFontFamilyKind ToSubtitleFontFamilyKind(this string? s, SubtitleFontFamilyKind defaultValue = SubtitleFontFamilyKind.Arial)
        => ParseSubtitleFontFamilyKind(s, defaultValue);

    public static string ToApiString(this SubtitleFontSizeKind val) => val switch
    {
        SubtitleFontSizeKind.ExtraLarge => "extra_large",
        _ => val.ToString().ToLowerInvariant()
    };
    public static SubtitleFontSizeKind ParseSubtitleFontSizeKind(string? s, SubtitleFontSizeKind defaultValue = SubtitleFontSizeKind.Medium)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleFontSizeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SubtitleFontSizeKind ToSubtitleFontSizeKind(this string? s, SubtitleFontSizeKind defaultValue = SubtitleFontSizeKind.Medium)
        => ParseSubtitleFontSizeKind(s, defaultValue);

    public static string ToApiString(this SubtitleAlignmentKind val) => val switch
    {
        SubtitleAlignmentKind.TopLeft => "top_left",
        SubtitleAlignmentKind.TopCenter => "top_center",
        SubtitleAlignmentKind.TopRight => "top_right",
        SubtitleAlignmentKind.MiddleLeft => "middle_left",
        SubtitleAlignmentKind.MiddleCenter => "middle_center",
        SubtitleAlignmentKind.MiddleRight => "middle_right",
        SubtitleAlignmentKind.BottomLeft => "bottom_left",
        SubtitleAlignmentKind.BottomCenter => "bottom_center",
        SubtitleAlignmentKind.BottomRight => "bottom_right",
        _ => "bottom_center"
    };
    public static SubtitleAlignmentKind ParseSubtitleAlignmentKind(string? s, SubtitleAlignmentKind defaultValue = SubtitleAlignmentKind.BottomCenter)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleAlignmentKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SubtitleAlignmentKind ToSubtitleAlignmentKind(this string? s, SubtitleAlignmentKind defaultValue = SubtitleAlignmentKind.BottomCenter)
        => ParseSubtitleAlignmentKind(s, defaultValue);

    public static string ToApiString(this SubtitleBorderModeKind val) => val switch
    {
        SubtitleBorderModeKind.OpaqueBox => "opaque_box",
        SubtitleBorderModeKind.OutlineAndShadow => "outline_and_shadow",
        _ => val.ToString().ToLowerInvariant()
    };
    public static SubtitleBorderModeKind ParseSubtitleBorderModeKind(string? s, SubtitleBorderModeKind defaultValue = SubtitleBorderModeKind.Outline)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubtitleBorderModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SubtitleBorderModeKind ToSubtitleBorderModeKind(this string? s, SubtitleBorderModeKind defaultValue = SubtitleBorderModeKind.Outline)
        => ParseSubtitleBorderModeKind(s, defaultValue);

    public static string ToApiString(this RemuxJobStageKind val) => val switch
    {
        RemuxJobStageKind.Pending => "pending",
        RemuxJobStageKind.AnalyzingInput => "analyzing_input",
        RemuxJobStageKind.ExtractingTracks => "extracting_tracks",
        RemuxJobStageKind.TranscodingVideo => "transcoding_video",
        RemuxJobStageKind.TranscodingAudio => "transcoding_audio",
        RemuxJobStageKind.Concatenating => "concatenating",
        RemuxJobStageKind.BurningSubtitles => "burning_subtitles",
        RemuxJobStageKind.MuxingOutput => "muxing_output",
        RemuxJobStageKind.ValidatingOutput => "validating_output",
        RemuxJobStageKind.Completed => "completed",
        RemuxJobStageKind.Failed => "failed",
        _ => "pending"
    };
    public static RemuxJobStageKind ParseRemuxJobStageKind(string? s, RemuxJobStageKind defaultValue = RemuxJobStageKind.Pending)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<RemuxJobStageKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static RemuxJobStageKind ToRemuxJobStageKind(this string? s, RemuxJobStageKind defaultValue = RemuxJobStageKind.Pending)
        => ParseRemuxJobStageKind(s, defaultValue);

    public static string ToApiString(this VideoHardwareAccelKind val) => val.ToString().ToLowerInvariant();
    public static VideoHardwareAccelKind ParseVideoHardwareAccelKind(string? s, VideoHardwareAccelKind defaultValue = VideoHardwareAccelKind.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoHardwareAccelKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoHardwareAccelKind ToVideoHardwareAccelKind(this string? s, VideoHardwareAccelKind defaultValue = VideoHardwareAccelKind.None)
        => ParseVideoHardwareAccelKind(s, defaultValue);

    public static string ToApiString(this VideoFrameInterpolationKind val) => val switch
    {
        VideoFrameInterpolationKind.FrameBlend => "frame_blend",
        VideoFrameInterpolationKind.MotionCompensated => "motion_compensated",
        VideoFrameInterpolationKind.OpticalFlow => "optical_flow",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VideoFrameInterpolationKind ParseVideoFrameInterpolationKind(string? s, VideoFrameInterpolationKind defaultValue = VideoFrameInterpolationKind.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoFrameInterpolationKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoFrameInterpolationKind ToVideoFrameInterpolationKind(this string? s, VideoFrameInterpolationKind defaultValue = VideoFrameInterpolationKind.None)
        => ParseVideoFrameInterpolationKind(s, defaultValue);

    public static string ToApiString(this VideoThumbnailTimeKind val) => val switch
    {
        VideoThumbnailTimeKind.FirstKeyframe => "first_keyframe",
        VideoThumbnailTimeKind.CustomOffset => "custom_offset",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VideoThumbnailTimeKind ParseVideoThumbnailTimeKind(string? s, VideoThumbnailTimeKind defaultValue = VideoThumbnailTimeKind.FirstKeyframe)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VideoThumbnailTimeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VideoThumbnailTimeKind ToVideoThumbnailTimeKind(this string? s, VideoThumbnailTimeKind defaultValue = VideoThumbnailTimeKind.FirstKeyframe)
        => ParseVideoThumbnailTimeKind(s, defaultValue);

    public static string ToApiString(this ExportFileNamingPatternKind val) => val switch
    {
        ExportFileNamingPatternKind.ProjectAndTimestamp => "project_timestamp",
        ExportFileNamingPatternKind.SceneAndBeat => "scene_beat",
        ExportFileNamingPatternKind.TitleOnly => "title_only",
        ExportFileNamingPatternKind.SequentialIndex => "sequential_index",
        ExportFileNamingPatternKind.Custom => "custom",
        _ => "project_timestamp"
    };
    public static ExportFileNamingPatternKind ParseExportFileNamingPatternKind(string? s, ExportFileNamingPatternKind defaultValue = ExportFileNamingPatternKind.ProjectAndTimestamp)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ExportFileNamingPatternKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ExportFileNamingPatternKind ToExportFileNamingPatternKind(this string? s, ExportFileNamingPatternKind defaultValue = ExportFileNamingPatternKind.ProjectAndTimestamp)
        => ParseExportFileNamingPatternKind(s, defaultValue);
}

#endregion
