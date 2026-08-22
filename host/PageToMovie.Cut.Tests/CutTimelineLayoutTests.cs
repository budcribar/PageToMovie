using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTimelineLayoutTests
{
    [Fact]
    public void Widths_follow_hop_sliced_duration_and_ripple()
    {
        var c1 = NewClip(1, 1, duration: 5);
        var c2 = NewClip(1, 2, duration: 10);
        c2.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        c2.SetDuration(10);

        var layout = CutTimelineLayout.Build([c1, c2], pxPerSec: 10);
        Assert.Equal(2, layout.Lanes.Count);
        Assert.Equal(0, layout.Lanes[0].StartSec);
        Assert.Equal(5, layout.Lanes[0].WidthSec);
        Assert.Equal(50, layout.Lanes[0].WidthPx);
        Assert.Equal(5, layout.Lanes[1].StartSec);
        Assert.Equal(5, layout.Lanes[1].WidthSec);
        Assert.Equal(50, layout.Lanes[1].WidthPx);
        Assert.Equal(10, layout.TotalSec);

        CutTimelineLayout.TrimOut(c1, 3);
        layout = CutTimelineLayout.Build([c1, c2], pxPerSec: 10);
        Assert.Equal(3, layout.Lanes[0].WidthSec);
        Assert.Equal(3, layout.Lanes[1].StartSec);
        Assert.Equal(8, layout.TotalSec);
    }

    [Fact]
    public void Trim_handles_stay_inside_hop_window()
    {
        var clip = NewClip(1, 2, duration: 10);
        clip.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        clip.SetDuration(10);

        CutTimelineLayout.TrimIn(clip, 0);
        Assert.Equal(5, clip.MarkIn);
        CutTimelineLayout.TrimIn(clip, 8);
        Assert.True(clip.MarkIn >= 5);
        Assert.True(clip.MarkOut - clip.MarkIn >= ClipInOut.MinSpanSeconds);

        clip.ApplyInOut(5, 10);
        CutTimelineLayout.TrimOut(clip, 12);
        Assert.Equal(10, clip.MarkOut);
        CutTimelineLayout.TrimOut(clip, 6);
        Assert.True(clip.MarkOut <= 10);
        Assert.True(clip.MarkOut - clip.MarkIn >= ClipInOut.MinSpanSeconds);
    }

    [Fact]
    public void Ruler_range_delete_maps_to_clip_and_closes_gap()
    {
        var c1 = NewClip(1, 1, duration: 5);
        var c2 = NewClip(1, 2, duration: 5);
        Assert.True(CutTimelineLayout.TryDeleteTimelineRange([c1, c2], 2, 3, out var applied));
        Assert.Equal(1, applied);
        var del = Assert.Single(c1.RangeDeletes);
        Assert.Equal(2, del.Start);
        Assert.Equal(3, del.End);
        Assert.Empty(c2.RangeDeletes);

        var layout = CutTimelineLayout.Build([c1, c2], pxPerSec: 20);
        Assert.Equal(4, layout.Lanes[0].WidthSec);
        Assert.Equal(4, layout.Lanes[1].StartSec);
        Assert.Equal(9, layout.TotalSec);
    }

    [Fact]
    public void Ruler_range_delete_can_span_two_clips()
    {
        var c1 = NewClip(1, 1, duration: 5);
        var c2 = NewClip(1, 2, duration: 5);
        Assert.True(CutTimelineLayout.TryDeleteTimelineRange([c1, c2], 4, 6.5, out var applied));
        Assert.Equal(2, applied);
        Assert.Equal(4, Assert.Single(c1.RangeDeletes).Start);
        Assert.Equal(0, Assert.Single(c2.RangeDeletes).Start);
        Assert.Equal(1.5, c2.RangeDeletes[0].End);

        var layout = CutTimelineLayout.Build([c1, c2], pxPerSec: 10);
        Assert.Equal(4, layout.Lanes[0].WidthSec);
        Assert.Equal(3.5, layout.Lanes[1].WidthSec, 5);
    }

    [Fact]
    public void Range_delete_rejects_whole_clip()
    {
        var c1 = NewClip(1, 1, duration: 5);
        Assert.False(CutTimelineLayout.TryDeleteTimelineRange([c1], 0, 5, out var applied));
        Assert.Equal(0, applied);
        Assert.Empty(c1.RangeDeletes);
    }

    [Fact]
    public void Join_ticks_sit_between_clips_and_honor_scene_and_fountain()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 4);
        var c = NewClip(2, 1, duration: 4);
        a.FountainTransition = "CUT TO:";
        b.FountainTransition = "DISSOLVE TO:";

        var layout = CutTimelineLayout.Build([a, b, c], pxPerSec: 10);
        Assert.Equal(2, layout.Joins.Count);
        Assert.Equal(CutJoinKind.Cut, layout.Joins[0].Kind);
        Assert.False(layout.Joins[0].SceneChange);
        Assert.Equal(4, layout.Joins[0].AtSec);
        Assert.Equal(40, layout.Joins[0].AtPx);
        Assert.Equal(CutJoinKind.Dissolve, layout.Joins[1].Kind);
        Assert.True(layout.Joins[1].SceneChange);
        Assert.Equal(8, layout.Joins[1].AtSec);

        b.JoinOverride = CutJoinKind.FadeWhite;
        layout = CutTimelineLayout.Build([a, b, c], pxPerSec: 10);
        Assert.Equal(CutJoinKind.FadeWhite, layout.Joins[1].Kind);
        Assert.Equal("Fade to white", CutTransitionMap.TickLabel(layout.Joins[1].Kind));
    }

    [Fact]
    public void HitTest_maps_stitched_time_through_keep_windows()
    {
        var clip = NewClip(1, 1, duration: 10);
        clip.ApplyInOut(2, 8);
        Assert.True(CutRangeDelete.TryAdd(clip.RangeDeletes, 4, 5, clip.MarkIn, clip.MarkOut, out _));
        var hit = CutTimelineLayout.HitTest([clip], 2.5);
        Assert.NotNull(hit);
        Assert.Equal(5.5, hit.Value.LocalSec, 5);
    }

    private static CutClip NewClip(int scene, int clip, double duration)
    {
        var c = new CutClip { Scene = scene, Clip = clip };
        c.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            RelativePath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
        });
        c.ActiveTakeNumber = 1;
        c.SeedSelection();
        c.SetDuration(duration);
        return c;
    }
}
