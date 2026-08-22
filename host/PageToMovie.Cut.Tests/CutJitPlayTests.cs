using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutJitPlayTests
{
    [Fact]
    public void First_window_is_hop_sliced_clip_at_playhead()
    {
        var c1 = NewClip(1, 1, duration: 8);
        c1.ApplyInOut(2, 7);
        var c2 = NewClip(1, 2, duration: 5);
        var window = CutJitPlay.At([c1, c2], 0);
        Assert.NotNull(window);
        Assert.Equal(c1, window.Value.Clip);
        Assert.Equal(2, window.Value.LocalStart);
        Assert.Equal(7, window.Value.LocalEnd);
        Assert.Equal(0, window.Value.TimelineStart);
        Assert.Equal(5, window.Value.TimelineEnd);
        Assert.Equal(4, CutJitPlay.TimelineToLocal(window.Value, 2), 5);
        Assert.Equal(2, CutJitPlay.LocalToTimeline(window.Value, 4), 5);
    }

    [Fact]
    public void Hop_window_inside_stitched_scene_stays_native()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 10);
        b.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        b.SetDuration(10);
        var clips = new[] { a, b };

        var window = CutJitPlay.At(clips, 5.5);
        Assert.NotNull(window);
        Assert.Equal(b, window.Value.Clip);
        Assert.Equal(6.5, window.Value.LocalStart, 5);
        Assert.Equal(10, window.Value.LocalEnd, 5);
        Assert.Equal(5.5, window.Value.TimelineStart, 5);
        Assert.True(CutJitPlay.IsHardPlayJoin(clips, 0));
        Assert.Equal(9, CutJitPlay.NativeReachableThrough(clips));
        Assert.False(CutJitPlay.NeedsWait(8.9, CutJitPlay.ReadyThroughSec(clips, 0)));
    }

    [Fact]
    public void Native_ready_runs_through_same_scene_hard_cuts()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 4);
        var c = NewClip(2, 1, duration: 4);
        var clips = new[] { a, b, c };

        Assert.True(CutJitPlay.IsHardPlayJoin(clips, 0));
        Assert.False(CutJitPlay.IsHardPlayJoin(clips, 1));
        Assert.Equal(8, CutJitPlay.NativeReachableThrough(clips));
        Assert.Equal(8, CutJitPlay.ReadyThroughSec(clips, prefixClipCount: 0));
        Assert.False(CutJitPlay.NeedsWait(7.9, 8));
        Assert.True(CutJitPlay.NeedsWait(8.1, 8));
    }

    [Fact]
    public void Prefix_extends_ready_past_scene_dissolve()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(2, 1, duration: 5);
        var clips = new[] { a, b };

        Assert.Equal(4, CutJitPlay.ReadyThroughSec(clips, 0));
        Assert.True(CutJitPlay.NeedsWait(4.2, CutJitPlay.ReadyThroughSec(clips, 0)));
        Assert.Equal(9, CutJitPlay.ReadyThroughSec(clips, 2));
        Assert.False(CutJitPlay.NeedsWait(4.2, CutJitPlay.ReadyThroughSec(clips, 2)));
    }

    [Fact]
    public void Scene_change_waits_until_prefix_covers_the_dissolve()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(1, 2, duration: 4);
        var c = NewClip(2, 1, duration: 5);
        var clips = new[] { a, b, c };
        var total = CutJitPlay.TotalSec(clips);

        Assert.Equal(13, total);
        Assert.Equal(8, CutJitPlay.NativeReachableThrough(clips));
        Assert.False(CutJitPlay.NeedsWait(7.9, 8, total));
        Assert.True(CutJitPlay.NeedsWait(8, 8, total));
        Assert.True(CutJitPlay.NeedsWait(8.1, 8, total));
        Assert.False(CutJitPlay.NeedsWait(8, CutJitPlay.ReadyThroughSec(clips, 3), total));
        Assert.False(CutJitPlay.IsTimelineEnd(8, total));
        Assert.True(CutJitPlay.IsTimelineEnd(12.97, total));
    }

    [Fact]
    public void Incoming_scene_card_blocks_native_join()
    {
        var a = NewClip(1, 1, duration: 4);
        var b = NewClip(2, 1, duration: 4);
        a.FountainTransition = "CUT TO:";
        b.Card.Enabled = true;
        b.Card.Text = "Chapter 2";
        var clips = new[] { a, b };

        Assert.False(CutJitPlay.IsHardPlayJoin(clips, 0));
        Assert.Equal(4, CutJitPlay.NativeReachableThrough(clips));
    }

    [Fact]
    public void Prefix_grow_does_not_restart_native_play()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 4);
        var c = NewClip(2, 1, 5);
        var clips = new[] { a, b, c };

        Assert.Equal(8, CutJitPlay.NativeReachableThrough(clips));
        Assert.False(CutJitPlay.NeedsWait(3.5, CutJitPlay.ReadyThroughSec(clips, 1), CutJitPlay.TotalSec(clips)));
        Assert.True(CutPlayClock.ShouldResumeOnPrefix(wantPlay: true, waiting: true));
        Assert.False(CutPlayClock.ShouldResumeOnPrefix(wantPlay: true, waiting: false));
        Assert.False(CutPlayClock.ShouldRestartNativeOnPrefixGrow);
    }

    [Fact]
    public void Cached_full_preview_still_skips_compose()
    {
        Assert.True(CutJitPlay.CanReuseFullPreview("blob:cut-preview"));
        Assert.False(CutJitPlay.CanReuseFullPreview(null));
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
