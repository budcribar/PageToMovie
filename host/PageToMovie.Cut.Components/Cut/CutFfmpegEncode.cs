namespace PageToMovie.Cut.Cut;

/// <summary>
/// WMP-safe H.264 argv for every Cut compose/export encode.
/// SSoT for tokens consumed by <c>cut.js</c> <c>h264EncodeArgs</c>.
/// </summary>
public static class CutFfmpegEncode
{
    public const string Preset = "ultrafast";
    public const string Crf = "23";
    public const string AudioBitrate = "128k";

    public static IReadOnlyList<string> VideoArgs { get; } =
    [
        "-c:v", CutComposeContract.ExportVideoCodec,
        "-preset", Preset,
        "-crf", Crf,
        "-pix_fmt", CutComposeContract.ExportPixelFormat,
        "-profile:v", CutComposeContract.ExportVideoProfile,
    ];

    public static IReadOnlyList<string> AudioArgs { get; } =
    [
        "-c:a", CutComposeContract.ExportAudioCodec,
        "-b:a", AudioBitrate,
    ];

    public static IReadOnlyList<string> MovArgs { get; } =
    [
        "-movflags", CutComposeContract.ExportMovFlags,
    ];

    public static IReadOnlyList<string> Argv(CutFfmpegEncodePath path) => path switch
    {
        CutFfmpegEncodePath.Trim => WithAudio(),
        CutFfmpegEncodePath.Overlay => WithAudio(),
        CutFfmpegEncodePath.OverlaySilent => WithoutAudio(),
        CutFfmpegEncodePath.Still => WithoutAudio(),
        CutFfmpegEncodePath.Xfade => WithAudio(),
        CutFfmpegEncodePath.Concat => WithAudio(),
        CutFfmpegEncodePath.ConcatSilent => WithoutAudio(),
        CutFfmpegEncodePath.Mix => MixArgv(),
        _ => WithAudio(),
    };

    public static IReadOnlyList<CutFfmpegEncodePath> ComposeExportPaths { get; } =
    [
        CutFfmpegEncodePath.Trim,
        CutFfmpegEncodePath.Overlay,
        CutFfmpegEncodePath.OverlaySilent,
        CutFfmpegEncodePath.Still,
        CutFfmpegEncodePath.Xfade,
        CutFfmpegEncodePath.Concat,
        CutFfmpegEncodePath.ConcatSilent,
        CutFfmpegEncodePath.Mix,
    ];

    private static string[] WithAudio() => [.. VideoArgs, .. AudioArgs, .. MovArgs];

    private static string[] WithoutAudio() => [.. VideoArgs, "-an", .. MovArgs];

    private static string[] MixArgv() =>
    [
        "-filter_complex", CutMusicMix.ComplexFilter(
            CutMusicMix.DefaultVolumePercent,
            CutMusicMix.DefaultFadeSec,
            CutMusicMix.DefaultFadeSec,
            0,
            0),
        "-map", "0:v", "-map", "[a]",
        "-t", "VIDEO_DURATION",
        "-c:v", "copy",
        .. AudioArgs,
        .. MovArgs,
    ];
}

public enum CutFfmpegEncodePath
{
    Trim,
    Overlay,
    OverlaySilent,
    Still,
    Xfade,
    Concat,
    ConcatSilent,
    Mix,
}
