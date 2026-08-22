using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutPlayMergeTests
{
    [Fact]
    public void Play_does_not_hop_take_files_once_a_merge_exists()
    {
        Assert.False(CutPlayMerge.ShouldHopTakeFiles);
        Assert.False(CutPlayMerge.ShouldReplaceMergeSrcWhilePlaying);
        Assert.True(CutPlayMerge.HoldOutgoingUntilMergeHasFrame);
        Assert.True(CutPlayMerge.ShouldPrimeMerge);
        Assert.False(CutPlayMerge.CanShowMerge(mergeHasFrame: false));
        Assert.True(CutPlayMerge.CanShowMerge(mergeHasFrame: true));
        Assert.True(CutPlayMerge.IsMovieFileName("movie.mp4"));
        Assert.True(CutPlayMerge.IsMovieFileName("Movie.MP4"));
        Assert.False(CutPlayMerge.IsMovieFileName("scene_01_clip_01_take_01.mp4"));
    }

    [Fact]
    public void First_start_is_one_hop_window_then_the_merge()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 5);
        var c = NewClip(2, 1, 5);
        var clips = new[] { a, b, c };
        var first = CutJitPlay.At(clips, 0);
        Assert.NotNull(first);

        Assert.Equal(4, first.Value.TimelineEnd, 5);
        Assert.False(CutPlayMerge.ShouldPlayMerge(null, clips, 0, 1, first));
        Assert.True(CutPlayMerge.ShouldPlayFirstStart(first, 1, playMerge: false));
        Assert.False(CutPlayMerge.ShouldPlayMerge("blob:p1", clips, 1, 1, first));
        Assert.True(CutPlayMerge.ShouldPlayMerge("blob:p2", clips, 2, 1, first));
        Assert.False(CutPlayMerge.ShouldPlayFirstStart(first, 1, playMerge: true));
        Assert.True(CutPlayMerge.ShouldPlayMerge("blob:p2", clips, 2, 4, first));
        Assert.False(CutPlayMerge.ShouldPlayFirstStart(first, 4, playMerge: false));
        Assert.False(CutPlayMerge.ShouldPlayMerge("blob:p1", clips, 1, 4, first));
        Assert.False(CutPlayMerge.ShouldPlayMerge("blob:p2", clips, 2, 9, first));
    }

    [Fact]
    public void Prefix_switches_first_start_to_merge_and_does_not_restart()
    {
        Assert.True(CutPlayMerge.ShouldSwitchToMergeOnPrefix(wantPlay: true, waiting: false, playingFirstStart: true));
        Assert.True(CutPlayMerge.ShouldSwitchToMergeOnPrefix(wantPlay: true, waiting: true, playingFirstStart: false));
        Assert.False(CutPlayMerge.ShouldSwitchToMergeOnPrefix(wantPlay: true, waiting: false, playingFirstStart: false));
        Assert.False(CutPlayClock.ShouldRestartNativeOnPrefixGrow);
        Assert.False(CutPlayClock.ShouldReplaceMergeSrcWhilePlaying);
        Assert.True(CutPlayClock.ShouldSwitchToMergeOnPrefix(true, false, true));
    }

    [Fact]
    public void Fingerprint_matches_only_the_same_cut()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 4);
        a.ApplyInOut(0.5, 4);
        var clips = new[] { a, b };
        var titles = new[] { new CutTextClip { Text = "Hi", StartSec = 1, Seconds = 2 } };
        var fp = CutPlayMerge.Fingerprint(clips, titles, "score.mp3");
        Assert.True(CutPlayMerge.IsFreshMerge(fp, clips, titles, "score.mp3"));
        Assert.False(CutPlayMerge.IsFreshMerge(fp, clips, titles, "other.mp3"));
        a.ApplyInOut(1, 4);
        Assert.False(CutPlayMerge.IsFreshMerge(fp, clips, titles, "score.mp3"));
        Assert.False(CutPlayMerge.IsFreshMerge(null, clips, titles, "score.mp3"));

        var music = new CutMusic { FileName = "score.mp3" };
        music.SetStart(12);
        music.ApplyInOut(1, 8);
        var placed = CutPlayMerge.Fingerprint(clips, titles, "score.mp3", music);
        Assert.NotEqual(fp, placed);
        Assert.True(CutPlayMerge.IsFreshMerge(placed, clips, titles, "score.mp3", music));
    }

    [Fact]
    public void Prefix_ended_waits_at_the_ready_edge_and_does_not_rewind()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 4);
        var c = NewClip(2, 1, 5);
        var clips = new[] { a, b, c };
        var s01End = CutPlayMerge.MergeReadyThroughSec(clips, 2);

        Assert.Equal(8, s01End, 5);
        Assert.False(CutPlayMerge.ShouldContinueAfterPrefixEnded);
        Assert.False(CutPlayClock.ShouldContinuePlayOnPrefixEnded);
        Assert.False(CutPlayMerge.EndedIsStop(s01End, CutJitPlay.TotalSec(clips)));
        Assert.False(CutPlayMerge.PrefixEndedIsStop(s01End, s01End, CutJitPlay.TotalSec(clips), 2, clips.Length));
        Assert.True(CutPlayMerge.TryWaitEdgeAfterPrefixEnded(clips, 2, s01End, out var edge));
        Assert.Equal(8, edge, 5);
        Assert.False(CutPlayMerge.ShouldPlayMerge("blob:s01", clips, 2, edge, CutJitPlay.At(clips, 0)));
        Assert.True(CutPlayMerge.WouldRewindMerge(currentPlayhead: 8, targetPlayhead: 4));
        Assert.False(CutPlayMerge.ShouldSeekMergeWhilePlaying(userSeek: false));
        Assert.True(CutPlayMerge.ShouldSeekMergeWhilePlaying(userSeek: true));

        Assert.True(CutPlayMerge.PrefixEndedIsStop(13, 13, 13, 3, clips.Length));
        Assert.True(CutPlayMerge.EndedIsStop(13, 13));
        Assert.False(CutPlayMerge.TryWaitEdgeAfterPrefixEnded(clips, 3, 13, out _));
        Assert.True(CutPlayMerge.TryWaitEdgeAfterPrefixEnded(clips, 3, s01End, out var hopEdge));
        Assert.Equal(s01End, hopEdge, 5);
        Assert.Equal(3, CutPlayMerge.CoveredClipCount("blob:full", "blob:full", prefixClipCount: 1, clipCount: 3));
        Assert.Equal(2, CutPlayMerge.CoveredClipCount("blob:s01", "blob:full", prefixClipCount: 2, clipCount: 3));
    }

    [Fact]
    public void Last_scene_playhead_stays_at_the_marker_not_scene_start()
    {
        var s01a = NewClip(1, 1, 10);
        var s01b = NewClip(1, 2, 10);
        var s02a = NewClip(2, 1, 10);
        var s02b = NewClip(2, 2, 10);
        var s02c = NewClip(2, 3, 10);
        var clips = new[] { s01a, s01b, s02a, s02b, s02c };
        const double midLastScene = 35;
        var first = CutJitPlay.At(clips, midLastScene);
        Assert.NotNull(first);
        Assert.Equal(s02b, first.Value.Clip);
        Assert.Equal(20, CutJitPlay.SceneStartSec(clips, midLastScene), 5);
        Assert.Equal(35, CutPlayMerge.PlaySeekSec(clips, midLastScene), 5);
        Assert.NotEqual(CutJitPlay.SceneStartSec(clips, midLastScene), CutPlayMerge.PlaySeekSec(clips, midLastScene));
        Assert.True(CutPlayMerge.ShouldPlayFirstStart(first, midLastScene, playMerge: false));
        Assert.True(CutPlayMerge.ShouldPlayMerge("blob:full", clips, 5, midLastScene, first));
        Assert.False(CutPlayMerge.ShouldPlayMerge("blob:s01", clips, 2, midLastScene, first));
    }

    [Fact]
    public void Join_change_keeps_the_playhead_and_does_not_loop_a_stale_prefix()
    {
        var a = NewClip(1, 1, 10);
        var b = NewClip(2, 1, 10);
        var c = NewClip(2, 2, 10);
        var clips = new[] { a, b, c };
        const double midScene = 15;
        a.JoinOverride = CutJoinKind.Dissolve;
        var before = CutPlayMerge.PlayheadAfterJoinChange(clips, midScene);
        a.JoinOverride = CutJoinKind.FadeWhite;

        Assert.False(CutPlayMerge.ShouldResetPlayheadOnJoinChange);
        Assert.False(CutPlayClock.ShouldResetPlayheadOnJoinChange);
        Assert.False(CutPlayMerge.ShouldSeekToSceneStartOnJoinChange);
        Assert.Equal(midScene, before, 5);
        Assert.Equal(midScene, CutPlayMerge.PlayheadAfterJoinChange(clips, midScene), 5);
        Assert.NotEqual(CutJitPlay.SceneStartSec(clips, midScene), CutPlayMerge.PlayheadAfterJoinChange(clips, midScene));
        Assert.False(CutPlayMerge.ShouldLoopPrefixWhileRebuilding);
        Assert.False(CutPlayMerge.AcceptPrefix(prefixGen: 1, playGen: 2));
        Assert.True(CutPlayMerge.AcceptPrefix(2, 2));
        Assert.False(CutPlayMerge.ComposeRunOwnsFlag(1, 2));
        Assert.True(CutPlayMerge.ShouldClearProgressWhenComposeEnds);
        Assert.True(CutPlayClock.ShouldShowPlayComposeOverlay(waiting: true, composing: true));
        Assert.False(CutPlayClock.ShouldShowPlayComposeOverlay(waiting: true, composing: false));
        Assert.False(CutPlayClock.ShouldShowPlayComposeOverlay(waiting: false, composing: true));
        Assert.False(CutPlayClock.ShouldShowPlayComposeOverlay(waiting: true, composing: true, showingMerge: true));
        Assert.True(CutPlayClock.ShouldRenderAfterComposeSettles);
    }

    [Fact]
    public void Stop_and_scrub_keep_the_playhead_where_it_is()
    {
        var a = NewClip(1, 1, 10);
        var b = NewClip(2, 1, 10);
        var c = NewClip(2, 2, 10);
        var clips = new[] { a, b, c };
        const double midLast = 15;

        Assert.False(CutPlayMerge.ShouldResetPlayheadOnStop);
        Assert.False(CutPlayClock.ShouldResetPlayheadOnStop);
        Assert.Equal(12.5, CutPlayMerge.PlayheadAfterStop(12.5), 5);
        Assert.Equal(0, CutPlayMerge.PlayheadAfterStop(0), 5);
        Assert.False(CutPlayMerge.ShouldSnapPlayheadOnScrubEnd);
        Assert.False(CutPlayClock.ShouldSnapPlayheadOnScrubEnd);
        Assert.False(CutPlayClock.ShouldPaintPlayheadWhilePaused);
        Assert.Equal(midLast, CutPlayMerge.ScrubCommitSec(clips, midLast), 5);
        Assert.NotEqual(CutJitPlay.SceneStartSec(clips, midLast), CutPlayMerge.ScrubCommitSec(clips, midLast));
        Assert.Equal(0, CutPlayMerge.ScrubCommitSec(clips, 0), 5);
    }

    [Fact]
    public void Scene_join_resume_plays_forward_once_the_prefix_covers_s02()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 4);
        var c = NewClip(2, 1, 5);
        var clips = new[] { a, b, c };
        var first = CutJitPlay.At(clips, 0);
        Assert.False(CutPlayMerge.ShouldPlayMerge("blob:s01", clips, 2, 8, first));
        Assert.True(CutPlayMerge.ShouldPlayMerge("blob:s01s02", clips, 3, 8, first));
        Assert.Equal(8, CutPlayMerge.PlaySeekSec(clips, 8), 5);
    }

    [Fact]
    public void Hop_eof_waits_for_merge_swap_and_does_not_stop()
    {
        var s01 = NewClip(1, 1, 5.04);
        s01.JoinOverride = CutJoinKind.FadeWhite;
        var s02 = NewClip(2, 1, 20);
        s02.JoinOverride = CutJoinKind.Dissolve;
        var s03 = NewClip(3, 1, 40);
        var s04 = NewClip(4, 1, 39.63);
        var clips = new[] { s01, s02, s03, s04 };
        var first = CutJitPlay.At(clips, 0);
        Assert.NotNull(first);
        var hopEnd = first.Value.TimelineEnd;
        var total = CutJitPlay.TotalSec(clips);
        var s02End = CutPlayMerge.MergeReadyThroughSec(clips, 2);

        Assert.Equal(5.04, hopEnd, 5);
        Assert.True(total > hopEnd + 1);
        Assert.False(CutJitPlay.IsTimelineEnd(hopEnd, total));
        Assert.False(CutPlayMerge.EndedIsStop(hopEnd, total));
        Assert.False(CutPlayMerge.PrefixEndedIsStop(hopEnd, hopEnd, total, coveredClipCount: clips.Length, clipCount: clips.Length));
        Assert.True(CutPlayMerge.TryWaitEdgeAfterPrefixEnded(clips, clips.Length, hopEnd, out var wait));
        Assert.Equal(hopEnd, wait, 5);

        Assert.False(CutPlayMerge.ShouldPlayMerge("blob:s01", clips, 1, hopEnd, first));
        Assert.True(CutPlayMerge.ShouldPlayMerge("blob:full", clips, clips.Length, hopEnd, first));
        Assert.True(CutPlayMerge.ShouldPlayMerge("blob:full", clips, clips.Length, 0, first));
        Assert.True(CutPlayMerge.MergeCoversTimeline(clips, clips.Length));
        Assert.False(CutPlayMerge.ShouldPlayFirstStart(first, hopEnd, playMerge: true));
        Assert.True(CutPlayMerge.ShouldSwitchToMergeOnPrefix(true, waiting: true, playingFirstStart: false));
        Assert.True(CutPlayMerge.ShouldRetryMergeSwap(wantPlay: true, showingMerge: false, "blob:full"));
        Assert.False(CutPlayMerge.ShouldRetryMergeSwap(wantPlay: true, showingMerge: true, "blob:full"));

        Assert.False(CutPlayMerge.ShouldReusePlayingMovie(
            samePlayer: true, boundUrl: "blob:full", url: "blob:full",
            mergeHasFrame: false, playhead: hopEnd, playingMergeEnd: total));
        Assert.True(CutPlayMerge.ShouldReusePlayingMovie(
            samePlayer: true, boundUrl: "blob:full", url: "blob:full",
            mergeHasFrame: true, playhead: hopEnd, playingMergeEnd: total));

        Assert.True(CutPlayMerge.ShouldPlayMerge("blob:full", clips, clips.Length, s02End, first));
        Assert.False(CutPlayMerge.EndedIsStop(s02End, total));
        Assert.Equal("Fade to white", CutTransitionMap.TickLabel(s01.JoinToNext(s02)));
        Assert.Equal("Dissolve", CutTransitionMap.TickLabel(s02.JoinToNext(s03)));

        var fade = CutComposeContract.XfadeSecondsFor(hopEnd);
        Assert.Equal(hopEnd - fade, CutPlayMerge.HandoffSeekSec(clips, hopEnd, firstSwapToMerge: true), 5);
        Assert.Equal(hopEnd, CutPlayMerge.HandoffSeekSec(clips, hopEnd, firstSwapToMerge: false), 5);
        Assert.Equal(0, CutPlayMerge.JoinLeadInAt(clips, 1.0), 5);
        Assert.Equal(fade, CutPlayMerge.JoinLeadInAt(clips, hopEnd), 5);
        Assert.Equal(CutComposeContract.XfadeSecondsFor(20), CutPlayMerge.JoinLeadInAt(clips, s02End), 5);
        Assert.Equal(s02End, CutPlayMerge.PlaySeekSec(clips, s02End), 5);
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
