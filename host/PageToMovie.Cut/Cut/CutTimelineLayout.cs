using System.Globalization;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// One video track, Film order. Width is the hop-aware sliced duration
/// (keep windows after range-delete). Joins sit on the boundary.
/// </summary>
public sealed class CutTimelineLayout
{
    public const double MinPxPerSec = 8;
    public const double MaxPxPerSec = 220;
    public const double DefaultPxPerSec = 36;
    public const double PlaceholderSec = 2;
    public const double MinHandleSpanSeconds = ClipInOut.MinSpanSeconds;

    public required IReadOnlyList<CutTimelineLane> Lanes { get; init; }
    public required IReadOnlyList<CutTimelineJoinTick> Joins { get; init; }
    public required IReadOnlyList<CutTimelineRulerMark> Ruler { get; init; }
    public double TotalSec { get; init; }
    public double PlayableSec { get; init; }
    public double PxPerSec { get; init; }
    public double WidthPx { get; init; }

    public static CutTimelineLayout Build(IReadOnlyList<CutClip> clips, double pxPerSec)
    {
        var px = Math.Clamp(pxPerSec, MinPxPerSec, MaxPxPerSec);
        var lanes = new List<CutTimelineLane>(clips.Count);
        var joins = new List<CutTimelineJoinTick>();
        var cursor = 0.0;
        var playable = 0.0;
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var sliced = SlicedSeconds(clip);
            var visual = sliced > 0.05 ? sliced : PlaceholderSec;
            lanes.Add(new CutTimelineLane(
                clip, i, cursor, visual, cursor * px, visual * px, sliced));
            if (!clip.Missing && sliced > 0.05)
                playable += sliced;
            cursor += visual;
        }

        for (var i = 0; i < clips.Count - 1; i++)
        {
            var left = clips[i];
            var right = clips[i + 1];
            var kind = left.JoinToNext(right);
            var sceneChange = right.Scene != left.Scene;
            var at = lanes[i + 1].StartSec;
            joins.Add(new CutTimelineJoinTick(i, kind, sceneChange, at, at * px));
        }

        return new CutTimelineLayout
        {
            Lanes = lanes,
            Joins = joins,
            Ruler = BuildRuler(cursor, px),
            TotalSec = cursor,
            PlayableSec = playable,
            PxPerSec = px,
            WidthPx = Math.Max(cursor * px, 1),
        };
    }

    public static double SlicedSeconds(CutClip clip)
    {
        if (clip.SelectedTake is null)
            return 0;
        if (clip.HasDuration)
        {
            var keep = clip.KeepWindows();
            var sum = 0.0;
            foreach (var w in keep)
                sum += Math.Max(0, w.End - w.Start);
            return sum;
        }

        if (clip.MarkOut > clip.MarkIn)
            return clip.MarkOut - clip.MarkIn;
        return 0;
    }

    public static double FitPxPerSec(double totalSec, double viewWidthPx)
    {
        if (totalSec <= 0.05 || viewWidthPx <= 40)
            return DefaultPxPerSec;
        return Math.Clamp(viewWidthPx / totalSec, MinPxPerSec, MaxPxPerSec);
    }

    public static double TimelineToPx(double timelineSec, double pxPerSec) =>
        timelineSec * Math.Clamp(pxPerSec, MinPxPerSec, MaxPxPerSec);

    public static double PxToTimeline(double px, double pxPerSec)
    {
        var pps = Math.Clamp(pxPerSec, MinPxPerSec, MaxPxPerSec);
        return pps <= 0 ? 0 : px / pps;
    }

    /// <summary>Map a stitched-timeline second onto a clip's file time (keep windows).</summary>
    public static (CutClip Clip, int Index, double LocalSec)? HitTest(
        IReadOnlyList<CutClip> clips, double timelineSec)
    {
        if (clips.Count == 0 || timelineSec < 0)
            return null;
        var cursor = 0.0;
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var sliced = SlicedSeconds(clip);
            var visual = sliced > 0.05 ? sliced : PlaceholderSec;
            if (timelineSec < cursor + visual || i == clips.Count - 1)
            {
                var offset = Math.Clamp(timelineSec - cursor, 0, visual);
                return (clip, i, LocalFromKeepOffset(clip, offset));
            }

            cursor += visual;
        }

        return null;
    }

    public static bool TryDeleteTimelineRange(
        IReadOnlyList<CutClip> clips,
        double timelineStart,
        double timelineEnd,
        out int applied)
    {
        applied = 0;
        var lo = Math.Min(timelineStart, timelineEnd);
        var hi = Math.Max(timelineStart, timelineEnd);
        if (hi - lo < CutRangeDelete.MinSpanSeconds)
            return false;

        var cursor = 0.0;
        foreach (var clip in clips)
        {
            var sliced = SlicedSeconds(clip);
            var visual = sliced > 0.05 ? sliced : PlaceholderSec;
            var clipStart = cursor;
            var clipEnd = cursor + visual;
            cursor += visual;
            if (clip.Missing || sliced <= 0.05)
                continue;
            if (hi <= clipStart + 0.0001 || lo >= clipEnd - 0.0001)
                continue;

            var overlapStart = Math.Max(lo, clipStart);
            var overlapEnd = Math.Min(hi, clipEnd);
            var localStart = LocalFromKeepOffset(clip, overlapStart - clipStart);
            var localEnd = LocalFromKeepOffset(clip, overlapEnd - clipStart);
            if (CutRangeDelete.TryAdd(clip.RangeDeletes, localStart, localEnd, clip.MarkIn, clip.MarkOut, out _))
                applied++;
        }

        return applied > 0;
    }

    public static void TrimIn(CutClip clip, double newIn)
    {
        if (clip.SelectedTake is null)
            return;
        var min = clip.SelectedTake.TrimMinSec;
        var max = clip.SelectedTake.TrimMaxSec;
        if (max - min < MinHandleSpanSeconds)
            return;
        var inn = Math.Clamp(newIn, min, max - MinHandleSpanSeconds);
        clip.ApplyInOut(inn, clip.MarkOut);
    }

    public static void TrimOut(CutClip clip, double newOut)
    {
        if (clip.SelectedTake is null)
            return;
        var min = clip.SelectedTake.TrimMinSec;
        var max = clip.SelectedTake.TrimMaxSec;
        if (max - min < MinHandleSpanSeconds)
            return;
        var outt = Math.Clamp(newOut, min + MinHandleSpanSeconds, max);
        clip.ApplyInOut(clip.MarkIn, outt);
    }

    public static IReadOnlyList<CutJoinKind> EditableJoins { get; } =
    [
        CutJoinKind.Cut,
        CutJoinKind.Dissolve,
        CutJoinKind.Dip,
        CutJoinKind.FadeWhite,
        CutJoinKind.CutToBlack,
    ];

    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            seconds = 0;
        var m = (int)(seconds / 60);
        var s = seconds - m * 60;
        return string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00.00}");
    }

    public static IReadOnlyList<CutTimelineRulerMark> BuildRuler(double totalSec, double pxPerSec)
    {
        var step = pxPerSec >= 60 ? 1.0 : pxPerSec >= 28 ? 2.0 : 5.0;
        var marks = new List<CutTimelineRulerMark>();
        if (totalSec <= 0)
            return marks;
        var last = Math.Ceiling(totalSec / step) * step;
        for (var t = 0.0; t <= last + 0.001; t += step)
        {
            marks.Add(new CutTimelineRulerMark(t, t * pxPerSec, Clock(t), (int)Math.Round(t / step) % 2 == 0));
        }

        return marks;
    }

    internal static double LocalFromKeepOffset(CutClip clip, double offsetInKeep)
    {
        var keep = clip.KeepWindows();
        if (keep.Count == 0)
            return clip.MarkIn + Math.Max(0, offsetInKeep);
        var remain = Math.Max(0, offsetInKeep);
        foreach (var w in keep)
        {
            var span = w.End - w.Start;
            if (remain <= span)
                return w.Start + remain;
            remain -= span;
        }

        return keep[^1].End;
    }
}

public readonly record struct CutTimelineLane(
    CutClip Clip,
    int Index,
    double StartSec,
    double WidthSec,
    double StartPx,
    double WidthPx,
    double SlicedSec);

public readonly record struct CutTimelineJoinTick(
    int AfterIndex,
    CutJoinKind Kind,
    bool SceneChange,
    double AtSec,
    double AtPx);

public readonly record struct CutTimelineRulerMark(
    double Sec,
    double Px,
    string Label,
    bool Major);
