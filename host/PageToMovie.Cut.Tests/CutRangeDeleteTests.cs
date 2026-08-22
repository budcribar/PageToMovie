using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutRangeDeleteTests
{
    [Fact]
    public void Clamp_rejects_whole_clip_span()
    {
        Assert.Null(CutRangeDelete.Clamp(0, 5, 0, 5));
        Assert.Null(CutRangeDelete.Clamp(-1, 99, 0, 4));
    }

    [Fact]
    public void Clamp_keeps_interior_span()
    {
        var span = CutRangeDelete.Clamp(1.2, 2.0, 0, 5);
        Assert.NotNull(span);
        Assert.Equal(1.2, span!.Value.Start, 5);
        Assert.Equal(2.0, span.Value.End, 5);
    }

    [Fact]
    public void Clamp_rejects_tiny_or_inverted_after_window()
    {
        Assert.Null(CutRangeDelete.Clamp(1.0, 1.02, 0, 5));
        Assert.Null(CutRangeDelete.Clamp(8, 9, 0, 5));
    }

    [Fact]
    public void KeepWindows_closes_the_gap()
    {
        var keep = CutRangeDelete.KeepWindows(0, 10, [(2, 4), (7, 8)]);
        Assert.Equal(3, keep.Count);
        Assert.Equal((0, 2), keep[0]);
        Assert.Equal((4, 7), keep[1]);
        Assert.Equal((8, 10), keep[2]);
    }

    [Fact]
    public void KeepWindows_merges_overlapping_deletes()
    {
        var keep = CutRangeDelete.KeepWindows(0, 6, [(1, 3), (2.5, 4)]);
        Assert.Equal([(0, 1), (4, 6)], keep);
    }

    [Fact]
    public void TryAdd_appends_clamped_span()
    {
        var list = new List<CutRangeSpan>();
        Assert.True(CutRangeDelete.TryAdd(list, 1, 2, 0, 5, out var added));
        Assert.Single(list);
        Assert.Equal(1, added!.Start);
        Assert.False(CutRangeDelete.TryAdd(list, 0, 5, 0, 5, out _));
        Assert.Single(list);
    }
}
