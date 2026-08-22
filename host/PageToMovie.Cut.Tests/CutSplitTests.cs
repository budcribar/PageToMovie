using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutSplitTests
{
    [Fact]
    public void Split_at_playhead_makes_two_adjacent_windows_of_the_same_take()
    {
        var clip = NewClip(1, 1, duration: 10);
        var clips = new List<CutClip> { clip };

        Assert.True(CutSplit.CanAt(clips, 4));
        Assert.True(CutSplit.TryAt(clips, 4, out var right));
        Assert.Equal(2, clips.Count);
        Assert.Same(clip, clips[0]);
        Assert.Same(right, clips[1]);
        Assert.Equal(1, clip.Scene);
        Assert.Equal(1, right!.Scene);
        Assert.Equal(1, clip.Clip);
        Assert.Equal(1, right.Clip);
        Assert.Equal(clip.FileName, right.FileName);
        Assert.Equal("scene_01_clip_01_take_01.mp4", right.FileName);
        Assert.Equal(0, clip.MarkIn);
        Assert.Equal(4, clip.MarkOut);
        Assert.Equal(4, right.MarkIn);
        Assert.Equal(10, right.MarkOut);

        var layout = CutTimelineLayout.Build(clips, pxPerSec: 10);
        Assert.Equal(4, layout.Lanes[0].WidthSec);
        Assert.Equal(4, layout.Lanes[1].StartSec);
        Assert.Equal(6, layout.Lanes[1].WidthSec);
        Assert.Equal(10, layout.TotalSec);
        Assert.Equal(0, CutTimelineLayout.SameSceneGapSec);
        var block = Assert.Single(layout.VideoBlocks);
        Assert.Equal("S01", block.Label);
        Assert.Equal(2, block.ClipCount);
        Assert.Equal(10, block.WidthSec);
        Assert.True(block.TrimIn);
        Assert.True(block.TrimOut);
        Assert.True(layout.Lanes[0].TrimIn);
        Assert.False(layout.Lanes[0].TrimOut);
        Assert.False(layout.Lanes[1].TrimIn);
        Assert.True(layout.Lanes[1].TrimOut);
    }

    [Fact]
    public void Split_inside_stitched_scene_stays_one_block_with_no_gap()
    {
        var a = NewClip(1, 1, duration: 5);
        var b = NewClip(1, 2, duration: 5);
        var clips = new List<CutClip> { a, b };

        Assert.True(CutSplit.TryAt(clips, 2, out _));
        Assert.Equal(3, clips.Count);
        Assert.Equal(0, clips[0].MarkIn);
        Assert.Equal(2, clips[0].MarkOut);
        Assert.Equal(2, clips[1].MarkIn);
        Assert.Equal(5, clips[1].MarkOut);
        Assert.Equal(a.FileName, clips[1].FileName);
        Assert.Equal(b.FileName, clips[2].FileName);

        var layout = CutTimelineLayout.Build(clips, pxPerSec: 10);
        Assert.Equal(2, layout.Lanes[1].StartSec);
        Assert.Equal(5, layout.Lanes[2].StartSec);
        Assert.Equal(10, Assert.Single(layout.VideoBlocks).WidthSec);
        Assert.Equal("S01", layout.VideoBlocks[0].Label);
        Assert.Equal(0, CutTimelineLayout.SameSceneGapSec);
    }

    [Fact]
    public void Split_is_hop_aware()
    {
        var clip = NewClip(1, 2, duration: 10);
        clip.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        clip.SetDuration(10);
        var clips = new List<CutClip> { clip };

        Assert.True(CutSplit.TryAt(clips, 2, out var right));
        Assert.Equal(5, clip.MarkIn);
        Assert.Equal(7, clip.MarkOut);
        Assert.Equal(7, right!.MarkIn);
        Assert.Equal(10, right.MarkOut);
        Assert.Equal(5, right.SelectedTake!.TrimMinSec);
        Assert.Equal(10, right.SelectedTake.TrimMaxSec);

        var layout = CutTimelineLayout.Build(clips, pxPerSec: 10);
        Assert.Equal(5, Assert.Single(layout.VideoBlocks).WidthSec);
    }

    [Fact]
    public void Split_rejects_edges_and_keeps_scene_joins_on_the_right()
    {
        var clip = NewClip(1, 1, duration: 6);
        clip.FountainTransition = "DISSOLVE TO:";
        clip.JoinOverride = CutJoinKind.FadeWhite;
        var clips = new List<CutClip> { clip };

        Assert.False(CutSplit.CanAt(clips, 0));
        Assert.False(CutSplit.CanAt(clips, 6));
        Assert.False(CutSplit.TryAt(clips, 0.05, out _));
        Assert.Single(clips);

        Assert.True(CutSplit.TryAt(clips, 3, out var right));
        Assert.Null(clip.FountainTransition);
        Assert.Null(clip.JoinOverride);
        Assert.Equal("DISSOLVE TO:", right!.FountainTransition);
        Assert.Equal(CutJoinKind.FadeWhite, right.JoinOverride);
    }

    [Fact]
    public void Split_keeps_range_deletes_on_the_correct_window()
    {
        var clip = NewClip(1, 1, duration: 10);
        Assert.True(CutRangeDelete.TryAdd(clip.RangeDeletes, 1, 2, clip.MarkIn, clip.MarkOut, out _));
        Assert.True(CutRangeDelete.TryAdd(clip.RangeDeletes, 7, 8, clip.MarkIn, clip.MarkOut, out _));
        var clips = new List<CutClip> { clip };

        Assert.True(CutSplit.TryAt(clips, 4, out var right));
        var leftDel = Assert.Single(clip.RangeDeletes);
        Assert.Equal(1, leftDel.Start);
        Assert.Equal(2, leftDel.End);
        var rightDel = Assert.Single(right!.RangeDeletes);
        Assert.Equal(7, rightDel.Start);
        Assert.Equal(8, rightDel.End);
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
