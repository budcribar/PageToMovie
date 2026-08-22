using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutPlayClockTests
{
    [Fact]
    public void Timeupdate_and_native_hops_do_not_re_render()
    {
        Assert.False(CutPlayClock.ShouldRenderOnTimeUpdate);
        Assert.False(CutPlayClock.ShouldRenderOnNativeAdvance);
        Assert.False(CutPlayClock.ShouldRebindPlayback(samePlayer: true));
        Assert.True(CutPlayClock.ShouldRebindPlayback(samePlayer: false));
        Assert.False(CutPlayClock.BlazorOwnsVideoSrc(isPlaying: true));
        Assert.True(CutPlayClock.BlazorOwnsVideoSrc(isPlaying: false));
        Assert.True(CutPlayClock.FreezePreviewMarkup(isPlaying: true));
        Assert.False(CutPlayClock.FreezePreviewMarkup(isPlaying: false));
    }

    [Fact]
    public void Prefix_swap_resumes_wait_only_and_does_not_restart_native()
    {
        Assert.True(CutPlayClock.ShouldResumeOnPrefix(wantPlay: true, waiting: true));
        Assert.False(CutPlayClock.ShouldResumeOnPrefix(wantPlay: true, waiting: false));
        Assert.False(CutPlayClock.ShouldResumeOnPrefix(wantPlay: false, waiting: true));
        Assert.False(CutPlayClock.ShouldContinuePlayOnPrefixEnded);
        Assert.False(CutPlayClock.ShouldResetPlayheadOnStop);
        Assert.False(CutPlayClock.ShouldResetPlayheadOnJoinChange);
        Assert.False(CutPlayClock.ShouldSnapPlayheadOnScrubEnd);
        Assert.False(CutPlayClock.ShouldPaintPlayheadWhilePaused);
        Assert.False(CutPlayClock.ShouldRestartNativeOnPrefixGrow);
        Assert.False(CutPlayClock.ShouldReplaceMergeSrcWhilePlaying);
        Assert.True(CutPlayClock.ShouldSwitchToMergeOnPrefix(true, false, true));
        Assert.False(CutPlayClock.ShouldRenderOnPrefix(waiting: false, playing: true));
        Assert.True(CutPlayClock.ShouldRenderOnPrefix(waiting: true, playing: true));
        Assert.True(CutPlayClock.ShouldRenderOnPrefix(waiting: false, playing: false));
        Assert.False(CutPlayClock.ShouldRenderOnProgress(overlayVisible: false));
        Assert.True(CutPlayClock.ShouldRenderOnProgress(overlayVisible: true));
        Assert.True(CutPlayClock.ShouldShowPlayComposeOverlay(waiting: true, composing: true));
        Assert.False(CutPlayClock.ShouldShowPlayComposeOverlay(waiting: true, composing: false));
        Assert.False(CutPlayClock.ShouldShowPlayComposeOverlay(waiting: true, composing: true, showingMerge: true));
        Assert.True(CutPlayClock.ShouldRenderAfterComposeSettles);
        Assert.True(CutPlayClock.ShouldRenderAfterMergeSwap);
        Assert.True(CutPlayClock.ShouldSwitchToMergeOnPrefix(true, false, false, atPlayingFileEnd: true));
    }

    [Fact]
    public void Native_advance_and_playhead_px_match_the_clock()
    {
        Assert.False(CutPlayClock.ShouldAdvanceNative(4.9, 5));
        Assert.True(CutPlayClock.ShouldAdvanceNative(4.97, 5));
        Assert.Equal(180, CutPlayClock.PlayheadLeftPx(5, 36), 5);
        Assert.Equal("0:05.50", CutPlayClock.Clock(5.5));
        Assert.Equal(CutTimelineLayout.Clock(12.25), CutPlayClock.Clock(12.25));
    }
}
