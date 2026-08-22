using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutPlaySwapTests
{
    [Fact]
    public void Hop_never_assigns_src_on_the_visible_player()
    {
        Assert.False(CutPlaySwap.ShouldSetSrcOnVisible);
        Assert.True(CutPlaySwap.HoldOutgoingUntilIncomingHasFrame);
        Assert.True(CutPlaySwap.ShouldPrimeNextHop);
        Assert.True(CutPlaySwap.UseStandbyForHop(sameUrl: false));
        Assert.True(CutPlaySwap.UseStandbyForHop(sameUrl: true));
        Assert.False(CutPlaySwap.CanShowIncoming(incomingHasFrame: false));
        Assert.True(CutPlaySwap.CanShowIncoming(incomingHasFrame: true));
    }

    [Fact]
    public void Primed_standby_matches_the_next_hop_window()
    {
        Assert.True(CutPlaySwap.PrimedMatches("blob:clip-b", 2, "blob:clip-b", 2.01));
        Assert.False(CutPlaySwap.PrimedMatches("blob:clip-b", 2, "blob:clip-c", 2));
        Assert.False(CutPlaySwap.PrimedMatches("blob:clip-b", 2, "blob:clip-b", 2.2));
        Assert.False(CutPlaySwap.PrimedMatches(null, 0, "blob:clip-b", 0));
    }

    [Fact]
    public void Same_scene_hard_cut_primes_the_next_native_hop()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 5);
        b.ApplyInOut(1, 5);
        var clips = new[] { a, b };
        var current = CutJitPlay.At(clips, 0);
        Assert.NotNull(current);

        var next = CutPlaySwap.NextHardHop(clips, current.Value);
        Assert.NotNull(next);
        Assert.Equal(b, next.Value.Clip);
        Assert.Equal(1, next.Value.LocalStart, 5);
        Assert.False(CutPlaySwap.ShouldPrimeMovie(clips, current.Value));
    }

    [Fact]
    public void Scene_change_dissolve_primes_the_composed_join_not_a_native_hop()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 4);
        var c = NewClip(2, 1, 5);
        var clips = new[] { a, b, c };
        var first = CutJitPlay.At(clips, 0);
        var lastNative = CutJitPlay.At(clips, 7.9);
        Assert.NotNull(first);
        Assert.NotNull(lastNative);

        Assert.NotNull(CutPlaySwap.NextHardHop(clips, first.Value));
        Assert.Null(CutPlaySwap.NextHardHop(clips, lastNative.Value));
        Assert.True(CutPlaySwap.ShouldPrimeMovie(clips, lastNative.Value));
        Assert.False(CutPlaySwap.ShouldPrimeMovie(clips, first.Value));
    }

    [Fact]
    public void Preview_markup_freezes_while_playing_so_src_cannot_reset()
    {
        Assert.True(CutPlayClock.FreezePreviewMarkup(isPlaying: true));
        Assert.False(CutPlayClock.FreezePreviewMarkup(isPlaying: false));
        Assert.False(CutPlayClock.BlazorOwnsVideoSrc(isPlaying: true));
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
