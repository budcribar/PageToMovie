namespace PageToMovie.Cut.Cut;

/// <summary>
/// JIT Play: first ready window is the hop-sliced clip at the playhead
/// (native audio). Same-scene hard cuts stay native. Compose grows a
/// prefix in the background; seek past that prefix waits.
/// </summary>
public static class CutJitPlay
{
    public readonly record struct Window(
        CutClip Clip,
        int Index,
        double LocalStart,
        double LocalEnd,
        double TimelineStart,
        double TimelineEnd);

    public static Window? At(IReadOnlyList<CutClip> clips, double timelineSec)
    {
        if (clips.Count == 0 || timelineSec < 0)
            return null;
        var cursor = 0.0;
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var sliced = CutTimelineLayout.SlicedSeconds(clip);
            var visual = sliced > 0.05 ? sliced : CutTimelineLayout.PlaceholderSec;
            var clipEnd = cursor + visual;
            if (timelineSec < clipEnd - 0.0001 || i == clips.Count - 1)
            {
                var offset = Math.Clamp(timelineSec - cursor, 0, visual);
                return WindowFromOffset(clip, i, cursor, offset);
            }

            cursor = clipEnd;
        }

        return null;
    }

    public static double TimelineStartOf(IReadOnlyList<CutClip> clips, int index)
    {
        var cursor = 0.0;
        for (var i = 0; i < clips.Count && i < index; i++)
            cursor += VisualSeconds(clips[i]);
        return cursor;
    }

    public static double TimelineEndOf(IReadOnlyList<CutClip> clips, int index)
    {
        if (clips.Count == 0 || index < 0)
            return 0;
        return TimelineStartOf(clips, index) + VisualSeconds(clips[Math.Min(index, clips.Count - 1)]);
    }

    public static bool IsHardPlayJoin(IReadOnlyList<CutClip> clips, int leftIndex)
    {
        if (leftIndex < 0 || leftIndex >= clips.Count - 1)
            return false;
        var left = clips[leftIndex];
        var right = clips[leftIndex + 1];
        if (right.Card.Enabled && right.IsFirstOfScene(clips))
            return false;
        return !CutTimelineLayout.ShowsJoinTick(left.JoinToNext(right));
    }

    /// <summary>
    /// How far Play can go without waiting: native hop windows through
    /// undetectable hard cuts, or the composed prefix, whichever is longer.
    /// </summary>
    public static double ReadyThroughSec(IReadOnlyList<CutClip> clips, int prefixClipCount)
    {
        var native = NativeReachableThrough(clips);
        var composed = prefixClipCount <= 0
            ? 0
            : TimelineEndOf(clips, Math.Min(prefixClipCount, clips.Count) - 1);
        return Math.Max(native, composed);
    }

    public static double NativeReachableThrough(IReadOnlyList<CutClip> clips)
    {
        if (clips.Count == 0)
            return 0;
        var end = TimelineEndOf(clips, 0);
        for (var i = 0; i < clips.Count - 1; i++)
        {
            if (!IsHardPlayJoin(clips, i))
                break;
            end = TimelineEndOf(clips, i + 1);
        }

        return end;
    }

    public static double TotalSec(IReadOnlyList<CutClip> clips) =>
        clips.Count == 0 ? 0 : TimelineEndOf(clips, clips.Count - 1);

    public static bool NeedsWait(double playhead, double readyThroughSec) =>
        NeedsWait(playhead, readyThroughSec, readyThroughSec);

    /// <summary>
    /// Seek past the ready prefix waits. Sitting on the ready edge while
    /// more timeline remains (scene-change dissolve not stitched yet) also
    /// waits — do not play the S01 prefix at EOF (that looks like Stop).
    /// </summary>
    public static bool NeedsWait(double playhead, double readyThroughSec, double totalSec)
    {
        if (readyThroughSec > 0.05
            && totalSec > readyThroughSec + 0.05
            && playhead >= readyThroughSec - 0.001)
            return true;
        return playhead > readyThroughSec + 0.05;
    }

    /// <summary>Prefix video EOF is Stop only at the real timeline end.</summary>
    public static bool IsTimelineEnd(double playhead, double totalSec) =>
        totalSec <= 0.05 || playhead >= totalSec - 0.05;

    public static bool CanReuseFullPreview(string? moviePreviewUrl) =>
        CutComposeContract.CanReusePreview(moviePreviewUrl);

    public static double LocalToTimeline(Window window, double localSec) =>
        window.TimelineStart + Math.Max(0, localSec - window.LocalStart);

    public static double TimelineToLocal(Window window, double timelineSec) =>
        window.LocalStart + Math.Max(0, timelineSec - window.TimelineStart);

    private static Window WindowFromOffset(CutClip clip, int index, double clipStart, double offsetInKeep)
    {
        var keep = clip.KeepWindows();
        if (keep.Count == 0)
        {
            var local = clip.MarkIn + offsetInKeep;
            var endLocal = clip.MarkOut > clip.MarkIn
                ? clip.MarkOut
                : clip.MarkIn + VisualSeconds(clip);
            var span = Math.Max(0, endLocal - local);
            return new Window(clip, index, local, endLocal, clipStart + offsetInKeep, clipStart + offsetInKeep + span);
        }

        var remain = Math.Max(0, offsetInKeep);
        var before = 0.0;
        foreach (var w in keep)
        {
            var span = Math.Max(0, w.End - w.Start);
            if (remain <= span)
            {
                var local = w.Start + remain;
                var timelineStart = clipStart + before + remain;
                return new Window(clip, index, local, w.End, timelineStart, clipStart + before + span);
            }

            remain -= span;
            before += span;
        }

        var last = keep[^1];
        return new Window(clip, index, last.End, last.End, clipStart + before, clipStart + before);
    }

    private static double VisualSeconds(CutClip clip)
    {
        var sliced = CutTimelineLayout.SlicedSeconds(clip);
        return sliced > 0.05 ? sliced : CutTimelineLayout.PlaceholderSec;
    }
}
