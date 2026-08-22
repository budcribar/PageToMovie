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
