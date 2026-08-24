namespace PageToMovie.Cut.Cut;

/// <summary>
/// Range-delete inside a clip's in/out window. Concat closes the gap.
/// Whole-clip delete is rejected.
/// </summary>
public static class CutRangeDelete
{
    public const double MinSpanSeconds = 0.1;

    public static (double Start, double End)? Clamp(
        double start,
        double end,
        double markIn,
        double markOut)
    {
        var window = ClipInOut.Clamp(markIn, markOut, Math.Max(markOut, markIn));
        if (window.MarkOut - window.MarkIn < MinSpanSeconds * 2)
            return null;

        var lo = Math.Max(window.MarkIn, Math.Min(start, end));
        var hi = Math.Min(window.MarkOut, Math.Max(start, end));
        if (double.IsNaN(lo) || double.IsNaN(hi) || hi - lo < MinSpanSeconds)
            return null;

        var usable = window.MarkOut - window.MarkIn;
        if (hi - lo >= usable - 0.001)
            return null;

        return (lo, hi);
    }

    public static bool TryAdd(
        IList<CutRangeSpan> list,
        double start,
        double end,
        double markIn,
        double markOut,
        out CutRangeSpan? added)
    {
        added = null;
        var clamped = Clamp(start, end, markIn, markOut);
        if (clamped is null)
            return false;
        var span = new CutRangeSpan { Start = clamped.Value.Start, End = clamped.Value.End };
        list.Add(span);
        added = span;
        return true;
    }

    public static IReadOnlyList<(double Start, double End)> KeepWindows(
        double markIn,
        double markOut,
        IEnumerable<(double Start, double End)> deletes)
    {
        var window = ClipInOut.Clamp(markIn, markOut, Math.Max(markOut, markIn));
        if (window.MarkOut - window.MarkIn < MinSpanSeconds)
            return [];

        var clamped = new List<(double Start, double End)>();
        foreach (var delete in deletes)
        {
            var span = Clamp(delete.Start, delete.End, window.MarkIn, window.MarkOut);
            if (span is { } one)
                clamped.Add(one);
        }

        var merged = Merge(clamped.OrderBy(d => d.Start).ToList());

        var keep = new List<(double Start, double End)>();
        var cursor = window.MarkIn;
        foreach (var del in merged)
        {
            if (del.Start - cursor >= MinSpanSeconds)
                keep.Add((cursor, del.Start));
            cursor = Math.Max(cursor, del.End);
        }

        if (window.MarkOut - cursor >= MinSpanSeconds)
            keep.Add((cursor, window.MarkOut));

        return keep;
    }

    private static List<(double Start, double End)> Merge(IReadOnlyList<(double Start, double End)> ordered)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var d in ordered)
        {
            if (merged.Count == 0 || d.Start > merged[^1].End + 0.0001)
                merged.Add(d);
            else
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, d.End));
        }

        return merged;
    }
}
