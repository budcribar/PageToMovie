namespace PageToMovie.Cut.Cut;

/// <summary>
/// Clipchamp-style scissors: split the take/window at the playhead.
/// Same take file, two adjacent trim windows — no new MP4 on disk.
/// Same-scene pieces abut (no gap). Scene-bookend handles stay on the
/// first/last clips of the scene.
/// </summary>
public static class CutSplit
{
    public const double MinPieceSeconds = ClipInOut.MinSpanSeconds;

    public static bool CanAt(IReadOnlyList<CutClip> clips, double playheadSec) =>
        TryLocate(clips, playheadSec, out _, out _, out _);

    public static bool TryAt(IList<CutClip> clips, double playheadSec, out CutClip? right)
    {
        right = null;
        IReadOnlyList<CutClip> read = clips as IReadOnlyList<CutClip> ?? [..clips];
        if (!TryLocate(read, playheadSec, out var index, out var left, out var local))
            return false;

        var oldOut = left.MarkOut;
        var oldJoin = left.JoinOverride;
        var oldFountain = left.FountainTransition;
        PartitionDeletes(left, local, out var leftDeletes, out var rightDeletes);

        right = CloneWindow(left, local, oldOut);
        right.JoinOverride = oldJoin;
        right.FountainTransition = oldFountain;
        right.RangeDeletes.Clear();
        foreach (var span in rightDeletes)
            CutRangeDelete.TryAdd(right.RangeDeletes, span.Start, span.End, right.MarkIn, right.MarkOut, out _);

        left.ApplyInOut(left.MarkIn, local);
        left.RangeDeletes.Clear();
        foreach (var span in leftDeletes)
            CutRangeDelete.TryAdd(left.RangeDeletes, span.Start, span.End, left.MarkIn, left.MarkOut, out _);
        left.JoinOverride = null;
        left.FountainTransition = null;

        clips.Insert(index + 1, right);
        return true;
    }

    /// <summary>
    /// New in-memory window of the same take file (hop, url, duration).
    /// Independent marks — not a new <c>_take_NN.mp4</c>.
    /// </summary>
    public static CutClip CloneWindow(CutClip source, double markIn, double markOut)
    {
        var clone = new CutClip
        {
            Scene = source.Scene,
            Clip = source.Clip,
            ActiveTakeNumber = source.ActiveTakeNumber,
            PointerRelativePath = source.PointerRelativePath,
        };
        foreach (var take in source.Takes)
            clone.Takes.Add(take.CloneIdentity());
        clone.SeedSelection();
        clone.ApplyInOut(markIn, markOut);
        return clone;
    }

    private static bool TryLocate(
        IReadOnlyList<CutClip> clips,
        double playheadSec,
        out int index,
        out CutClip clip,
        out double localSec)
    {
        index = -1;
        clip = null!;
        localSec = 0;
        if (clips.Count == 0)
            return false;
        var hit = CutTimelineLayout.HitTest(clips, playheadSec);
        if (hit is null || hit.Value.Clip.SelectedTake is null)
            return false;

        clip = hit.Value.Clip;
        index = hit.Value.Index;
        localSec = hit.Value.LocalSec;
        foreach (var window in clip.KeepWindows())
        {
            if (localSec >= window.Start + MinPieceSeconds
                && localSec <= window.End - MinPieceSeconds)
                return true;
        }

        return false;
    }

    private static void PartitionDeletes(
        CutClip clip,
        double local,
        out List<CutRangeSpan> left,
        out List<CutRangeSpan> right)
    {
        left = [];
        right = [];
        foreach (var span in clip.RangeDeletes)
        {
            if (span.End <= local + 0.0001)
                left.Add(span);
            else if (span.Start >= local - 0.0001)
                right.Add(span);
        }
    }
}
