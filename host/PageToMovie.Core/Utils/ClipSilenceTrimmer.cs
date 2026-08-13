using System.Globalization;
using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Core.Utils;

/// <summary>
/// Pure helpers for silence cut-points. Lives in Core (not Engine) so both the
/// server and the Blazor WASM client (ffmpeg.wasm) call the same implementation
/// instead of keeping a hand-ported copy in JS.
/// </summary>
public static class ClipSilenceTrimmer
{
    public const double DefaultKeepTailSeconds = 0.35;
    public const double SpeechBreathTailSeconds = 0.90;

    /// <summary>Shortest a trimmed clip may end up — matches ClipDurationEstimator.MinSeconds.</summary>
    public const double MinClipSeconds = 3;

    private static readonly Regex SilenceEndRe = new(@"silence_end:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex SilenceStartRe = new(@"silence_start:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// If file starts with silence, return the timestamp to start decode (after silence + keepHead).
    /// </summary>
    public static double? ComputeLeadInPoint(
        string silenceDetectLog,
        double totalDuration,
        double keepHeadSeconds)
    {
        if (string.IsNullOrWhiteSpace(silenceDetectLog) || totalDuration < 1.0)
            return null;

        var starts = SilenceStartRe.Matches(silenceDetectLog)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();
        var ends = SilenceEndRe.Matches(silenceDetectLog)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();

        if (starts.Count == 0 || starts[0] > 0.35)
        {
            if (ends.Count > 0 && ends[0] > 0.3 && ends[0] < totalDuration * 0.5 &&
                (starts.Count == 0 || starts[0] > ends[0]))
            {
                var cut = Math.Max(0, ends[0] - keepHeadSeconds);
                if (cut >= 0.2 && totalDuration - cut >= MinClipSeconds - 0.25)
                    return cut;
            }
            return null;
        }

        var leadStart = starts[0];
        var end = ends.FirstOrDefault(e => e > leadStart + 0.05);
        if (end <= leadStart)
            return null;

        var leadLen = end - Math.Max(0, leadStart);
        if (leadLen < 0.25)
            return null;

        var startAt = Math.Max(0, end - keepHeadSeconds);
        if (startAt < 0.2)
            return null;
        if (totalDuration - startAt < MinClipSeconds - 0.25)
            return null;
        return startAt;
    }

    /// <summary>
    /// Cut after last real audio when the file ends in silence.
    /// </summary>
    public static double? ComputeCutPoint(
        string silenceDetectLog,
        double totalDuration,
        double keepTailSeconds)
    {
        if (string.IsNullOrWhiteSpace(silenceDetectLog) || totalDuration < 1.0)
            return null;
        if (double.IsNaN(totalDuration) || double.IsInfinity(totalDuration))
            return null;

        var starts = SilenceStartRe.Matches(silenceDetectLog)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();
        var ends = SilenceEndRe.Matches(silenceDetectLog)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();

        if (starts.Count == 0)
            return null;

        double? trailStart = null;
        foreach (var s in starts)
        {
            if (!ends.Any(e => e > s + 0.05))
                trailStart = s;
        }

        if (trailStart is null && ends.Count > 0)
        {
            var lastEnd = ends[^1];
            if (totalDuration - lastEnd < 0.35)
            {
                for (var i = starts.Count - 1; i >= 0; i--)
                {
                    if (starts[i] < lastEnd)
                    {
                        trailStart = starts[i];
                        break;
                    }
                }
            }
        }

        if (trailStart is null)
            return null;

        var silenceTail = totalDuration - trailStart.Value;
        if (silenceTail < 0.35)
            return null;

        var cut = trailStart.Value + keepTailSeconds;
        cut = Math.Min(cut, totalDuration - 0.05);
        if (cut >= totalDuration - 0.2)
            return null;
        if (cut < MinClipSeconds - 0.25)
            return null;
        return cut;
    }
}
