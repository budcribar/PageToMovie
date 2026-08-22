using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTextPlaceTests
{
    [Fact]
    public void Move_slides_a_title_by_dt()
    {
        var start = CutTextPlace.Move(
            originStart: 4,
            hold: 2,
            dt: 1.5,
            prevEnd: 0,
            nextStart: 20,
            movieEnd: 20);
        Assert.Equal(5.5, start);
    }

    [Fact]
    public void Move_clamps_so_two_titles_never_overlap()
    {
        var a = new CutTextPlace.Span(0, 3);
        var b = new CutTextPlace.Span(8, 2);
        CutTextPlace.Neighbors(4, 2, [a, b], movieEnd: 20, out var prev, out var next);
        Assert.Equal(3, prev);
        Assert.Equal(8, next);

        Assert.Equal(3, CutTextPlace.Move(4, 2, dt: -10, prev, next, 20));
        Assert.Equal(6, CutTextPlace.Move(4, 2, dt: 10, prev, next, 20));
        Assert.False(CutTextPlace.OverlapsAny(3, 2, [a, b]));
        Assert.False(CutTextPlace.OverlapsAny(6, 2, [a, b]));
    }

    [Fact]
    public void ResolveDrop_nudges_to_the_nearest_legal_slot()
    {
        var neighbor = new CutTextPlace.Span(4, 2);
        Assert.Equal(2, CutTextPlace.ResolveDrop(desiredStart: 3, hold: 2, [neighbor], movieEnd: 20));
        Assert.Equal(6, CutTextPlace.ResolveDrop(desiredStart: 5, hold: 2, [neighbor], movieEnd: 20));
        Assert.False(CutTextPlace.OverlapsAny(2, 2, [neighbor]));
        Assert.False(CutTextPlace.OverlapsAny(6, 2, [neighbor]));
    }

    [Fact]
    public void Paste_and_duplicate_do_not_land_on_an_existing_title()
    {
        var titles = new List<CutTextClip> { Title("A", start: 2, hold: 4) };
        var source = titles[0];

        var copy = CutTextEdit.Duplicate(titles, source);
        Assert.Equal(2, titles.Count);
        Assert.Equal(6, copy.StartSec);
        Assert.Equal(4, copy.HoldSeconds);
        Assert.False(CutTextPlace.Overlaps(
            new CutTextPlace.Span(source.StartSec, source.HoldSeconds),
            new CutTextPlace.Span(copy.StartSec, copy.HoldSeconds)));

        var payload = CutTextEdit.Copy(source);
        var start = CutTextEdit.PasteStart(3.5, source);
        var pasted = CutTextEdit.Paste(titles, payload, start);
        Assert.Equal(10, pasted.StartSec);
        Assert.All(Pairwise(titles), pair =>
            Assert.False(CutTextPlace.Overlaps(
                new CutTextPlace.Span(pair.Left.StartSec, pair.Left.HoldSeconds),
                new CutTextPlace.Span(pair.Right.StartSec, pair.Right.HoldSeconds))));
    }

    [Fact]
    public void Add_at_a_playhead_inside_a_title_uses_the_next_gap()
    {
        var titles = new List<CutTextClip> { Title("A", start: 1, hold: 3) };
        var added = CutTextTrack.Add(titles, startSec: 2);
        Assert.Equal(4, added.StartSec);
        Assert.Equal(CutCard.DefaultHoldSeconds, added.HoldSeconds);
        Assert.False(CutTextPlace.Overlaps(
            new CutTextPlace.Span(1, 3),
            new CutTextPlace.Span(added.StartSec, added.HoldSeconds)));
    }

    [Fact]
    public void Trim_handles_stop_at_neighbors()
    {
        var others = new List<CutTextPlace.Span>
        {
            new(0, 2),
            new(8, 2),
        };

        var (inStart, inHold) = CutTextPlace.TrimIn(4, 3, dt: -10, others, movieEnd: 20);
        Assert.Equal(2, inStart);
        Assert.Equal(5, inHold);

        var (_, outHold) = CutTextPlace.TrimOut(4, 3, dt: 10, others, movieEnd: 20);
        Assert.Equal(4, outHold);
    }

    [Fact]
    public void Cards_on_the_same_row_block_title_placement()
    {
        var card = new CutTextPlace.Span(5, 2);
        var start = CutTextPlace.Place(desiredStart: 5.5, hold: 2, [card], movieEnd: 20);
        Assert.Equal(7, start);
        Assert.False(CutTextPlace.OverlapsAny(start, 2, [card]));
    }

    private static CutTextClip Title(string text, double start, double hold) =>
        new()
        {
            Id = CutTextClip.NewId(),
            Text = text,
            StartSec = start,
            Seconds = hold,
        };

    private static IEnumerable<(CutTextClip Left, CutTextClip Right)> Pairwise(IReadOnlyList<CutTextClip> titles)
    {
        for (var i = 0; i < titles.Count; i++)
        {
            for (var j = i + 1; j < titles.Count; j++)
                yield return (titles[i], titles[j]);
        }
    }
}
