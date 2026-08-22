using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Play file policy: one merged movie. First Play may start on the
/// hop-sliced take at the playhead; once a merge/prefix covers playback,
/// switch to that file and stay there. Clip/scene edges are times on
/// that file — not take-MP4 hops.
/// </summary>
public static class CutPlayMerge
{
    public const string MovieFileName = "movie.mp4";

    /// <summary>Long-term Play never swaps take MP4s at clip edges.</summary>
    public static bool ShouldHopTakeFiles => false;

    /// <summary>Prefix growth must not replace the playing merge mid-file.</summary>
    public static bool ShouldReplaceMergeSrcWhilePlaying => false;

    /// <summary>The one allowed src change: first-start take → merge.</summary>
    public static bool HoldOutgoingUntilMergeHasFrame => true;

    public static bool CanShowMerge(bool mergeHasFrame) => mergeHasFrame;

    public static bool ShouldPrimeMerge => true;

    public static bool IsMovieFileName(string? fileName) =>
        string.Equals(CutClipNaming.FileNameOnly(fileName), MovieFileName, StringComparison.OrdinalIgnoreCase);

    public static double MergeReadyThroughSec(IReadOnlyList<CutClip> clips, int prefixClipCount)
    {
        if (prefixClipCount <= 0 || clips.Count == 0)
            return 0;
        return CutJitPlay.TimelineEndOf(clips, Math.Min(prefixClipCount, clips.Count) - 1);
    }

    public static bool MergeCovers(IReadOnlyList<CutClip> clips, int prefixClipCount, double playhead) =>
        MergeReadyThroughSec(clips, prefixClipCount) >= playhead - 0.001;

    public static bool ShouldPlayMerge(
        string? mergeUrl,
        IReadOnlyList<CutClip> clips,
        int prefixClipCount,
        double playhead,
        CutJitPlay.Window? firstStart)
    {
        if (string.IsNullOrWhiteSpace(mergeUrl) || prefixClipCount <= 0)
            return false;
        var mergeEnd = MergeReadyThroughSec(clips, prefixClipCount);
        if (playhead > mergeEnd + 0.05)
            return false;
        var total = CutJitPlay.TotalSec(clips);
        if (total > mergeEnd + 0.05 && playhead >= mergeEnd - 0.001)
            return false;
        if (firstStart is not { } start)
            return true;
        if (playhead >= start.TimelineEnd - 0.001)
            return mergeEnd >= playhead - 0.001;
        return mergeEnd > start.TimelineEnd + 0.05;
    }

    public static bool ShouldPlayFirstStart(CutJitPlay.Window? firstStart, double playhead, bool playMerge) =>
        !playMerge
        && firstStart is { } window
        && playhead < window.TimelineEnd - 0.001;

    public static bool ShouldSwitchToMergeOnPrefix(bool wantPlay, bool waiting, bool playingFirstStart) =>
        wantPlay && (waiting || playingFirstStart);

    public static bool IsFreshMerge(
        string? savedFingerprint,
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts,
        string? audioFileName) =>
        !string.IsNullOrWhiteSpace(savedFingerprint)
        && string.Equals(savedFingerprint, Fingerprint(clips, texts, audioFileName), StringComparison.Ordinal);

    public static string Fingerprint(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts,
        string? audioFileName)
    {
        var sb = new StringBuilder();
        sb.Append(audioFileName ?? "");
        foreach (var clip in clips)
        {
            sb.Append('|').Append(clip.Scene).Append(':').Append(clip.Clip);
            sb.Append('@').Append(Num(clip.MarkIn)).Append('-').Append(Num(clip.MarkOut));
            foreach (var span in clip.RangeDeletes)
                sb.Append('~').Append(Num(span.Start)).Append('-').Append(Num(span.End));
            sb.Append('J').Append(clip.JoinOverride is { } join
                ? CutTransitionMap.WireName(join)
                : clip.FountainTransition ?? "");
            if (clip.Card.Enabled)
                sb.Append("C").Append(clip.Card.Text).Append('/').Append(Num(clip.Card.HoldSeconds));
        }

        foreach (var title in texts ?? [])
        {
            sb.Append("#").Append(title.Text)
                .Append('@').Append(Num(title.StartSec))
                .Append('x').Append(Num(title.HoldSeconds));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Num(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}
