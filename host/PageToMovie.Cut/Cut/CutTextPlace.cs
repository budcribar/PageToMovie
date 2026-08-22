namespace PageToMovie.Cut.Cut;

/// <summary>
/// One text row: titles and scene cards never occupy the same time.
/// Move/trim push against neighbors; add/paste/duplicate land in a free gap.
/// </summary>
public static class CutTextPlace
{
    public const double Epsilon = 1e-4;

    public readonly record struct Span(double StartSec, double Seconds)
    {
        public double EndSec => StartSec + Math.Max(0, Seconds);
    }

    public static List<Span> FromTitles(IEnumerable<CutTextClip> titles, string? exceptId = null)
    {
        var spans = new List<Span>();
        foreach (var title in titles)
        {
            if (exceptId is not null && title.Id == exceptId)
                continue;
            spans.Add(new Span(Math.Max(0, title.StartSec), title.HoldSeconds));
        }

        return spans;
    }

    public static List<Span> FromBlocks(IEnumerable<CutTextBlock> blocks, string? exceptId = null)
    {
        var spans = new List<Span>();
        foreach (var block in blocks)
        {
            if (exceptId is not null && block.Id == exceptId)
                continue;
            spans.Add(new Span(Math.Max(0, block.StartSec), Math.Max(0, block.Seconds)));
        }

        return spans;
    }

    public static bool Overlaps(Span a, Span b) =>
        a.StartSec < b.EndSec - Epsilon && b.StartSec < a.EndSec - Epsilon;

    public static bool OverlapsAny(double startSec, double hold, IReadOnlyList<Span> others)
    {
        var probe = new Span(startSec, hold);
        foreach (var other in others)
        {
            if (Overlaps(probe, other))
                return true;
        }

        return false;
    }

    public static void Neighbors(
        double startSec,
        double hold,
        IReadOnlyList<Span> others,
        double movieEnd,
        out double prevEnd,
        out double nextStart)
    {
        prevEnd = 0;
        nextStart = HasMovieEnd(movieEnd) ? movieEnd : double.PositiveInfinity;
        var end = startSec + hold;
        foreach (var other in others)
        {
            if (other.EndSec <= startSec + Epsilon)
                prevEnd = Math.Max(prevEnd, other.EndSec);
            else if (other.StartSec >= end - Epsilon)
                nextStart = Math.Min(nextStart, other.StartSec);
        }
    }

    /// <summary>
    /// Slide a fixed-hold clip by <paramref name="dt"/> inside its current gap.
    /// Continuous — no hop to the other side of a neighbor.
    /// </summary>
    public static double Move(
        double originStart,
        double hold,
        double dt,
        double prevEnd,
        double nextStart,
        double movieEnd)
    {
        hold = PositiveHold(hold);
        var start = originStart + dt;
        var min = Math.Max(0, prevEnd);
        var max = nextStart - hold;
        if (HasMovieEnd(movieEnd))
            max = Math.Min(max, movieEnd - hold);
        if (max < min)
            return min;
        return Math.Clamp(start, min, max);
    }

    /// <summary>
    /// Nearest legal start for a drop. If <paramref name="desiredStart"/> is free,
    /// keep it; otherwise nudge left or right into the closest gap that fits.
    /// </summary>
    public static double ResolveDrop(
        double desiredStart,
        double hold,
        IReadOnlyList<Span> others,
        double movieEnd)
    {
        hold = PositiveHold(hold);
        var want = SanitizeStart(desiredStart);
        if (HasMovieEnd(movieEnd) && movieEnd >= hold)
            want = Math.Min(want, movieEnd - hold);
        if (!OverlapsAny(want, hold, others))
            return want;

        var best = Place(want, hold, others, movieEnd);
        var bestDist = Math.Abs(best - want);
        foreach (var candidate in LegalStarts(hold, others, movieEnd))
        {
            var dist = Math.Abs(candidate - want);
            if (dist < bestDist - Epsilon)
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return best;
    }

    /// <summary>
    /// Next free start at or after <paramref name="desiredStart"/>.
    /// When that time is occupied, walk forward to the next gap (or after the last clip).
    /// </summary>
    public static double Place(
        double desiredStart,
        double hold,
        IReadOnlyList<Span> others,
        double movieEnd)
    {
        hold = PositiveHold(hold);
        var start = SanitizeStart(desiredStart);
        if (!OverlapsAny(start, hold, others))
            return FitInMovie(start, hold, others, movieEnd);

        var after = start;
        foreach (var block in others.OrderBy(s => s.StartSec))
        {
            if (block.EndSec <= after + Epsilon)
                continue;
            if (Overlaps(new Span(after, hold), block) || block.StartSec < after + hold - Epsilon)
            {
                after = block.EndSec;
                continue;
            }

            break;
        }

        return Math.Max(0, after);
    }

    public static (double Start, double Hold) TrimIn(
        double originStart,
        double originHold,
        double dt,
        IReadOnlyList<Span> others,
        double movieEnd)
    {
        Neighbors(originStart, originHold, others, movieEnd, out var prevEnd, out _);
        var end = originStart + originHold;
        var start = originStart + dt;
        start = Math.Max(start, Math.Max(0, prevEnd));
        start = Math.Min(start, end - CutCard.MinHoldSeconds);
        return (start, end - start);
    }

    public static (double Start, double Hold) TrimOut(
        double originStart,
        double originHold,
        double dt,
        IReadOnlyList<Span> others,
        double movieEnd)
    {
        Neighbors(originStart, originHold, others, movieEnd, out _, out var nextStart);
        var maxHold = nextStart - originStart;
        if (HasMovieEnd(movieEnd))
            maxHold = Math.Min(maxHold, movieEnd - originStart);
        maxHold = Math.Max(CutCard.MinHoldSeconds, maxHold);
        var hold = Math.Clamp(originHold + dt, CutCard.MinHoldSeconds, maxHold);
        return (originStart, hold);
    }

    public static double HoldMax(double startSec, IReadOnlyList<Span> others, double movieEnd)
    {
        Neighbors(startSec, CutCard.MinHoldSeconds, others, movieEnd, out _, out var nextStart);
        var max = nextStart - Math.Max(0, startSec);
        if (HasMovieEnd(movieEnd))
            max = Math.Min(max, movieEnd - Math.Max(0, startSec));
        return Math.Max(CutCard.MinHoldSeconds, max);
    }

    private static List<double> LegalStarts(double hold, IReadOnlyList<Span> others, double movieEnd)
    {
        var starts = new List<double>();
        var cursor = 0.0;
        foreach (var block in others.OrderBy(s => s.StartSec))
        {
            AddGapStarts(starts, cursor, block.StartSec, hold);
            cursor = Math.Max(cursor, block.EndSec);
        }

        if (HasMovieEnd(movieEnd))
            AddGapStarts(starts, cursor, movieEnd, hold);
        else
            starts.Add(cursor);

        return starts;
    }

    private static void AddGapStarts(List<double> starts, double gapLo, double gapHi, double hold)
    {
        if (gapHi - gapLo < hold - Epsilon)
            return;
        starts.Add(gapLo);
        starts.Add(gapHi - hold);
    }

    private static double FitInMovie(
        double start,
        double hold,
        IReadOnlyList<Span> others,
        double movieEnd)
    {
        start = SanitizeStart(start);
        if (!HasMovieEnd(movieEnd) || movieEnd < hold)
            return start;
        var clamped = Math.Min(start, movieEnd - hold);
        return OverlapsAny(clamped, hold, others) ? start : clamped;
    }

    private static double SanitizeStart(double startSec)
    {
        if (double.IsNaN(startSec) || double.IsInfinity(startSec) || startSec < 0)
            return 0;
        return startSec;
    }

    private static double PositiveHold(double hold) =>
        hold < CutCard.MinHoldSeconds ? CutCard.DefaultHoldSeconds : hold;

    private static bool HasMovieEnd(double movieEnd) =>
        !double.IsNaN(movieEnd) && !double.IsInfinity(movieEnd) && movieEnd > 0;
}
