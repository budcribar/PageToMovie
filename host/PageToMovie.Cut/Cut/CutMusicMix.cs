using System.Globalization;
using System.Text;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// One-track music mix under native clip VO. Volume and afade are
/// the inspector values — Play and movie.mp4 share this filter.
/// </summary>
public static class CutMusicMix
{
    public const int DefaultVolumePercent = 100;
    public const double DefaultFadeSec = 0;
    public const double MaxFadeSeconds = 12;

    public static int ClampVolume(int percent) => Math.Clamp(percent, 0, 100);

    public static double ClampFade(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return 0;
        return Math.Min(MaxFadeSeconds, seconds);
    }

    public static double GainOf(int volumePercent) =>
        ClampVolume(volumePercent) / 100.0;

    public static string VolumeChain(
        int volumePercent,
        double fadeInSec,
        double fadeOutSec,
        double startSec,
        double holdSec)
    {
        var gain = GainOf(volumePercent);
        var start = startSec > 0 && !double.IsNaN(startSec) && !double.IsInfinity(startSec)
            ? startSec
            : 0;
        var hold = holdSec > 0 && !double.IsNaN(holdSec) && !double.IsInfinity(holdSec)
            ? holdSec
            : 0;
        var fadeIn = ClampFade(fadeInSec);
        var fadeOut = ClampFade(fadeOutSec);
        if (hold > 0.05)
        {
            fadeIn = Math.Min(fadeIn, hold);
            fadeOut = Math.Min(fadeOut, Math.Max(0, hold - fadeIn));
        }

        var sb = new StringBuilder();
        sb.Append("volume=").Append(Num(gain));
        if (fadeIn > 0.001)
            sb.Append(",afade=t=in:st=").Append(Num(start)).Append(":d=").Append(Num(fadeIn));
        if (fadeOut > 0.001)
        {
            var outAt = hold > 0.05 ? Math.Max(start, start + hold - fadeOut) : start;
            sb.Append(",afade=t=out:st=").Append(Num(outAt)).Append(":d=").Append(Num(fadeOut));
        }

        return sb.ToString();
    }

    public static string ComplexFilter(
        int volumePercent,
        double fadeInSec,
        double fadeOutSec,
        double startSec,
        double holdSec) =>
        "[1:a]"
        + VolumeChain(volumePercent, fadeInSec, fadeOutSec, startSec, holdSec)
        + ",apad[bg];[0:a][bg]amix=inputs=2:duration=first:dropout_transition=0[a]";

    public static string ComplexFilter(CutMusic music)
    {
        ArgumentNullException.ThrowIfNull(music);
        return ComplexFilter(
            music.VolumePercent,
            music.FadeInSec,
            music.FadeOutSec,
            music.StartSec,
            music.SlicedDurationSec);
    }

    public static string MusicOnlyFilter(
        int volumePercent,
        double fadeInSec,
        double fadeOutSec,
        double startSec,
        double holdSec) =>
        "[1:a]"
        + VolumeChain(volumePercent, fadeInSec, fadeOutSec, startSec, holdSec)
        + ",apad[a]";

    public static string MusicOnlyFilter(CutMusic music)
    {
        ArgumentNullException.ThrowIfNull(music);
        return MusicOnlyFilter(
            music.VolumePercent,
            music.FadeInSec,
            music.FadeOutSec,
            music.StartSec,
            music.SlicedDurationSec);
    }

    private static string Num(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
