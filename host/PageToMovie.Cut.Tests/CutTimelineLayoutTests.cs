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
        Assert.Equal(7.5, Assert.Single(layout.VideoBlocks).WidthSec, 5);
        Assert.Equal("S01", layout.VideoBlocks[0].Label);
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
    public void Video_track_is_one_contiguous_scene_block()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 6);
        var c = NewClip(2, 1, duration: 3);
        a.SelectedTake!.Filmstrip.Add("a1");
        a.SelectedTake.Filmstrip.Add("a2");
        b.SelectedTake!.Filmstrip.Add("b1");
        c.SelectedTake!.Filmstrip.Add("c1");

        var layout = CutTimelineLayout.Build([a, b, c], pxPerSec: 10);
        Assert.Equal(3, layout.Lanes.Count);
        Assert.Equal(2, layout.Scenes.Count);
        Assert.Equal(2, layout.VideoBlocks.Count);

        Assert.Equal(layout.Lanes[0].StartSec + layout.Lanes[0].WidthSec + CutTimelineLayout.SameSceneGapSec,
            layout.Lanes[1].StartSec);
        Assert.Equal(0, CutTimelineLayout.SameSceneGapSec);

        var s01 = layout.VideoBlocks[0];
        Assert.Equal("S01", s01.Label);
        Assert.Equal(CutTimelineLayout.SceneLabel(1), s01.Label);
        Assert.DoesNotContain("C01", s01.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("C02", s01.Label, StringComparison.Ordinal);
        Assert.Equal(2, s01.ClipCount);
        Assert.Equal(0, s01.FirstIndex);
        Assert.Equal(1, s01.LastIndex);
        Assert.Equal(0, s01.StartSec);
        Assert.Equal(10, s01.WidthSec);
        Assert.Equal(100, s01.WidthPx);
        Assert.True(s01.TrimIn);
        Assert.True(s01.TrimOut);
        Assert.Equal(new[] { "a1", "a2", "b1" }, s01.Filmstrip);
        Assert.True(CutTimelineLayout.BlockContains(s01, a, [a, b, c]));
        Assert.True(CutTimelineLayout.BlockContains(s01, b, [a, b, c]));
        Assert.False(CutTimelineLayout.BlockContains(s01, c, [a, b, c]));

        var s02 = layout.VideoBlocks[1];
        Assert.Equal("S02", s02.Label);
        Assert.Equal(1, s02.ClipCount);
        Assert.Equal(10, s02.StartSec);
        Assert.Equal(3, s02.WidthSec);
        Assert.Equal(new[] { "c1" }, s02.Filmstrip);
        Assert.True(s02.TrimIn);
        Assert.True(s02.TrimOut);
    }

    [Fact]
    public void Scene_block_width_follows_hop_slice()
    {
        var c1 = NewClip(1, 1, duration: 5);
        var c2 = NewClip(1, 2, duration: 10);
        c2.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        c2.SetDuration(10);

        var layout = CutTimelineLayout.Build([c1, c2], pxPerSec: 10);
        var block = Assert.Single(layout.VideoBlocks);
        Assert.Equal("S01", block.Label);
        Assert.Equal(10, block.WidthSec);
        Assert.Equal(0, block.StartSec);

        CutTimelineLayout.TrimOut(c1, 3);
        layout = CutTimelineLayout.Build([c1, c2], pxPerSec: 10);
        Assert.Equal(8, Assert.Single(layout.VideoBlocks).WidthSec);
        Assert.Equal(3, layout.Lanes[1].StartSec);
    }

    [Fact]
    public void HitTest_walks_stitched_scene_into_hop_window()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 10);
        b.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        b.SetDuration(10);

        var hit = CutTimelineLayout.HitTest([a, b], 5.5);
        Assert.NotNull(hit);
        Assert.Equal(b, hit.Value.Clip);
        Assert.Equal(1, hit.Value.Index);
        Assert.Equal(6.5, hit.Value.LocalSec, 5);
    }

    [Fact]
    public void Marks_scenes_not_same_scene_hard_cuts()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 4);
        var c = NewClip(2, 1, duration: 4);
        a.FountainTransition = "CUT TO:";
        b.FountainTransition = "DISSOLVE TO:";

        var layout = CutTimelineLayout.Build([a, b, c], pxPerSec: 10);
        Assert.Equal(3, layout.Lanes.Count);
        Assert.Equal(2, layout.Scenes.Count);
        Assert.Equal(2, layout.VideoBlocks.Count);
        Assert.Equal("S01", layout.Scenes[0].Label);
        Assert.Equal("S01", layout.VideoBlocks[0].Label);
        Assert.Equal(layout.Scenes[0].WidthSec, layout.VideoBlocks[0].WidthSec);
        Assert.Equal(2, layout.Scenes[0].ClipCount);
        Assert.Equal(0, layout.Scenes[0].StartSec);
        Assert.Equal(8, layout.Scenes[0].WidthSec);
        Assert.Equal("S02", layout.Scenes[1].Label);
        Assert.Equal(1, layout.Scenes[1].ClipCount);
        Assert.Equal(8, layout.Scenes[1].StartSec);

        var tick = Assert.Single(layout.Joins);
        Assert.Equal(CutJoinKind.Dissolve, tick.Kind);
        Assert.True(tick.SceneChange);
        Assert.Equal(8, tick.AtSec);
        Assert.Equal(80, tick.AtPx);

        b.FountainTransition = "CUT TO:";
        layout = CutTimelineLayout.Build([a, b, c], pxPerSec: 10);
        var cutTick = Assert.Single(layout.Joins);
        Assert.Equal(CutJoinKind.Cut, cutTick.Kind);
        Assert.Equal(8, cutTick.AtSec);
        Assert.Equal(2, layout.Scenes.Count);

        b.JoinOverride = CutJoinKind.FadeWhite;
        layout = CutTimelineLayout.Build([a, b, c], pxPerSec: 10);
        Assert.Equal(CutJoinKind.FadeWhite, Assert.Single(layout.Joins).Kind);
        Assert.Equal("Fade to white", CutTransitionMap.TickLabel(layout.Joins[0].Kind));

        b.JoinOverride = CutJoinKind.CutToBlack;
        layout = CutTimelineLayout.Build([a, b, c], pxPerSec: 10);
        var black = Assert.Single(layout.Joins);
        Assert.Equal(CutJoinKind.CutToBlack, black.Kind);
        Assert.Equal(8, black.AtSec);
        Assert.Equal(layout.Scenes[1].StartSec, black.AtSec);
    }

    [Fact]
    public void Zoom_and_fit_stay_inside_px_per_sec_bounds()
    {
        Assert.Equal(36, CutTimelineLayout.FitPxPerSec(0, 800));
        Assert.Equal(40, CutTimelineLayout.FitPxPerSec(20, 800), 5);
        Assert.Equal(CutTimelineLayout.MinPxPerSec, CutTimelineLayout.FitPxPerSec(400, 80));
        Assert.Equal(CutTimelineLayout.MaxPxPerSec, CutTimelineLayout.FitPxPerSec(2, 2000));

        var inOnce = CutTimelineLayout.ZoomInPxPerSec(CutTimelineLayout.DefaultPxPerSec);
        Assert.Equal(CutTimelineLayout.DefaultPxPerSec * CutTimelineLayout.ZoomFactor, inOnce, 5);
        Assert.Equal(CutTimelineLayout.MaxPxPerSec, CutTimelineLayout.ZoomInPxPerSec(CutTimelineLayout.MaxPxPerSec));
        Assert.Equal(CutTimelineLayout.MinPxPerSec, CutTimelineLayout.ZoomOutPxPerSec(CutTimelineLayout.MinPxPerSec));
        Assert.Equal(CutTimelineLayout.DefaultPxPerSec, CutTimelineLayout.ZoomOutPxPerSec(inOnce), 5);
        Assert.Equal(CutTimelineLayout.MaxPxPerSec, CutTimelineLayout.ClampPxPerSec(999));
    }

    [Fact]
    public void Trim_handles_only_on_scene_bookends()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 4);
        var c = NewClip(1, 3, duration: 4);
        var d = NewClip(2, 1, duration: 4);
        var clips = new[] { a, b, c, d };

        Assert.True(CutTimelineLayout.ShowsTrimIn(clips, 0));
        Assert.False(CutTimelineLayout.ShowsTrimOut(clips, 0));
        Assert.False(CutTimelineLayout.ShowsTrimIn(clips, 1));
        Assert.False(CutTimelineLayout.ShowsTrimOut(clips, 1));
        Assert.False(CutTimelineLayout.ShowsTrimIn(clips, 2));
        Assert.True(CutTimelineLayout.ShowsTrimOut(clips, 2));
        Assert.True(CutTimelineLayout.ShowsTrimIn(clips, 3));
        Assert.True(CutTimelineLayout.ShowsTrimOut(clips, 3));

        var layout = CutTimelineLayout.Build(clips, pxPerSec: 10);
        Assert.True(layout.Lanes[0].TrimIn);
        Assert.False(layout.Lanes[0].TrimOut);
        Assert.False(layout.Lanes[1].TrimIn);
        Assert.False(layout.Lanes[1].TrimOut);
        Assert.False(layout.Lanes[2].TrimIn);
        Assert.True(layout.Lanes[2].TrimOut);
        Assert.True(layout.Lanes[3].TrimIn);
        Assert.True(layout.Lanes[3].TrimOut);

        Assert.Equal(2, layout.VideoBlocks.Count);
        Assert.True(layout.VideoBlocks[0].TrimIn);
        Assert.True(layout.VideoBlocks[0].TrimOut);
        Assert.Equal(0, layout.VideoBlocks[0].FirstIndex);
        Assert.Equal(2, layout.VideoBlocks[0].LastIndex);
        Assert.True(layout.VideoBlocks[1].TrimIn);
        Assert.True(layout.VideoBlocks[1].TrimOut);

        Assert.True(a.IsFirstOfScene(clips));
        Assert.False(a.IsLastOfScene(clips));
        Assert.False(b.IsFirstOfScene(clips));
        Assert.False(b.IsLastOfScene(clips));
        Assert.True(c.IsLastOfScene(clips));
        Assert.True(d.IsFirstOfScene(clips));
        Assert.True(d.IsLastOfScene(clips));
    }

    [Theory]
    [InlineData(CutJoinKind.Cut, false)]
    [InlineData(CutJoinKind.Unset, false)]
    [InlineData(CutJoinKind.Dissolve, true)]
    [InlineData(CutJoinKind.Dip, true)]
    [InlineData(CutJoinKind.FadeWhite, true)]
    [InlineData(CutJoinKind.CutToBlack, true)]
    public void Join_tick_only_for_visible_scene_look(CutJoinKind kind, bool show) =>
        Assert.Equal(show, CutTimelineLayout.ShowsJoinTick(kind));

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
