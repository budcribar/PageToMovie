namespace PageToMovie.Cut.Cut;

/// <summary>
/// Compose audio + preview-cache contract. Play and Make movie keep each
/// clip's native audio on the hop/trim window. Optional music mixes under.
/// Hard-cut concat keeps audio. Visual dissolves try acrossfade; if that
/// cannot mix audio, hard-cut audio through the join.
/// Cut-to-black is a short black hold at the join — not a scene card.
/// </summary>
public static class CutComposeContract
{
    public const bool KeepNativeClipAudio = true;
    public const bool PadCardSilence = false;

    /// <summary>Instant black hold between scenes. Not a chapter card.</summary>
    public const double CutToBlackHoldSeconds = 0.4;

    /// <summary>
    /// ffmpeg xfade duration cap. Same formula as Cut <c>xfadeAsync</c>:
    /// <c>min(0.5, max(0.2, leftSec / 4))</c>.
    /// </summary>
    public const double XfadeSeconds = 0.5;

    public const double XfadeMinSeconds = 0.2;

    public static bool CanReusePreview(string? moviePreviewUrl) =>
        !string.IsNullOrWhiteSpace(moviePreviewUrl);

    public static bool JoinInsertsBlackHold(CutJoinKind kind) =>
        kind == CutJoinKind.CutToBlack;

    /// <summary>
    /// Cut-to-black is a join look. Fountain <c>[[CARD:]]</c> / Add text
    /// stay on the text row — the join never invents a "Scene" card.
    /// </summary>
    public static bool JoinIsSceneCard(CutJoinKind kind)
    {
        _ = kind;
        return false;
    }

    public static double HoldSeconds(CutJoinKind kind) =>
        JoinInsertsBlackHold(kind) ? CutToBlackHoldSeconds : 0;

    public static bool JoinIsXfade(CutJoinKind kind) =>
        AudioJoin(kind) == CutComposeAudioJoin.AcrossfadeOrHardCut;

    public static double XfadeSecondsFor(double leftSec)
    {
        if (leftSec <= 0 || double.IsNaN(leftSec) || double.IsInfinity(leftSec))
            return XfadeMinSeconds;
        return Math.Min(XfadeSeconds, Math.Max(XfadeMinSeconds, leftSec / 4));
    }

    public static CutComposeAudioJoin AudioJoin(CutJoinKind kind) =>
        kind switch
        {
            CutJoinKind.Dissolve or CutJoinKind.Dip or CutJoinKind.FadeWhite
                or CutJoinKind.FadeIn or CutJoinKind.FadeOut
                => CutComposeAudioJoin.AcrossfadeOrHardCut,
            _ => CutComposeAudioJoin.KeepThroughConcat,
        };

    public const string ExportVideoCodec = "libx264";
    public const string ExportPixelFormat = "yuv420p";
    public const string ExportVideoProfile = "main";
    public const string ExportAudioCodec = "aac";
    public const string ExportMovFlags = "+faststart";

    /// <summary>
    /// Music mix must keep the video length. <c>-shortest</c> against a
    /// shorter score clips movie.mp4 (1:28 vs a 1:44 timeline).
    /// </summary>
    public const bool MixMustNotShortenToMusic = true;

    public static bool ExportArgvIsWmpSafe(IReadOnlyList<string> argv, bool expectAudio)
    {
        if (argv is null || argv.Count == 0)
            return false;
        if (!ContainsPair(argv, "-c:v", ExportVideoCodec))
            return false;
        if (!ContainsPair(argv, "-pix_fmt", ExportPixelFormat))
            return false;
        if (!ContainsPair(argv, "-profile:v", ExportVideoProfile))
            return false;
        if (!ContainsToken(argv, ExportMovFlags))
            return false;
        if (CopiesVideo(argv))
            return false;
        if (expectAudio && !ContainsPair(argv, "-c:a", ExportAudioCodec))
            return false;
        return true;
    }

    public static bool MixKeepsVideoDuration(IReadOnlyList<string> argv) =>
        MixMustNotShortenToMusic && !ContainsToken(argv, "-shortest");

    public static double ComposedDurationSec(IReadOnlyList<CutClip> clips)
    {
        var visual = CutJitPlay.TotalSec(clips);
        if (clips.Count == 0)
            return 0;
        var overlap = 0.0;
        var hold = 0.0;
        for (var i = 0; i < clips.Count - 1; i++)
        {
            var join = clips[i].JoinToNext(clips[i + 1]);
            hold += HoldSeconds(join);
            if (!JoinIsXfade(join))
                continue;
            var leftSec = CutJitPlay.TimelineEndOf(clips, i) - CutJitPlay.TimelineStartOf(clips, i);
            overlap += XfadeSecondsFor(leftSec);
        }

        return Math.Max(0, visual - overlap + hold);
    }

    private static bool ContainsToken(IReadOnlyList<string> argv, string token)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsPair(IReadOnlyList<string> argv, string flag, string value)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (string.Equals(argv[i], flag, StringComparison.Ordinal)
                && string.Equals(argv[i + 1], value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool CopiesVideo(IReadOnlyList<string> argv)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (!string.Equals(argv[i + 1], "copy", StringComparison.Ordinal))
                continue;
            if (string.Equals(argv[i], "-c:v", StringComparison.Ordinal)
                || string.Equals(argv[i], "-c", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

public enum CutComposeAudioJoin
{
    KeepThroughConcat,
    AcrossfadeOrHardCut,
}
