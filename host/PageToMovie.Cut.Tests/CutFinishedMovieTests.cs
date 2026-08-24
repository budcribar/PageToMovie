using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutFinishedMovieTests
{
    [Fact]
    public void Fresh_finish_plays_movie_instead_of_stitch()
    {
        var clips = new[] { NewClip(1, 1, 4), NewClip(1, 2, 4) };
        var titles = new[] { new CutTextClip { Text = "Hi", StartSec = 1, Seconds = 2 } };
        var music = new CutMusic { FileName = "score.mp3" };
        var fp = CutPlayMerge.Fingerprint(clips, titles, "score.mp3", music);
        var json = CutProjectFile.Serialize(clips, "score.mp3", titles, fp, music);

        Assert.True(CutFinishedMovie.ShouldPlay(json, movieFilePresent: true));
        Assert.False(CutFinishedMovie.ShouldPlay(json, movieFilePresent: false));
    }

    [Fact]
    public void Missing_or_stale_fingerprint_falls_back_to_stitch()
    {
        var clips = new[] { NewClip(1, 1, 4), NewClip(2, 1, 5) };
        var jsonNoFp = CutProjectFile.Serialize(clips, null);
        Assert.False(CutFinishedMovie.ShouldPlay(jsonNoFp, movieFilePresent: true));
        Assert.False(CutFinishedMovie.ShouldPlay(null, movieFilePresent: true));
        Assert.False(CutFinishedMovie.ShouldPlay("", movieFilePresent: true));
        Assert.False(CutFinishedMovie.ShouldPlay("{not-json", movieFilePresent: true));

        var music = new CutMusic();
        var fp = CutPlayMerge.Fingerprint(clips, [], null, music);
        var json = CutProjectFile.Serialize(clips, null, movieFingerprint: fp, music: music);
        clips[0].ApplyInOut(1, 4);
        var stale = CutProjectFile.Serialize(clips, null, movieFingerprint: fp, music: music);
        Assert.False(CutFinishedMovie.ShouldPlay(stale, movieFilePresent: true));
        Assert.True(CutFinishedMovie.ShouldPlay(json, movieFilePresent: true));
    }

    [Fact]
    public void Review_full_movie_play_asks_cut_before_stitch()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var playback = File.ReadAllText(Path.Combine(
            host, "PageToMovie.Web", "Components", "Pages", "ReviewPlayback.cs"));
        var playWip = playback.IndexOf("internal async Task PlayWipAsync()", StringComparison.Ordinal);
        var playScene = playback.IndexOf("internal async Task PlaySceneAsync(", StringComparison.Ordinal);
        var playClip = playback.IndexOf("internal async Task PlayClipAsync(", StringComparison.Ordinal);
        var finished = playback.IndexOf("TryPlayFinishedCutAsync()", StringComparison.Ordinal);
        Assert.True(playWip >= 0 && finished > playWip && finished < playScene);
        Assert.Contains("CutFinishedMovie.ShouldPlay", playback, StringComparison.Ordinal);
        Assert.True(playScene >= 0 && !playback[playScene..playClip].Contains("TryPlayFinishedCutAsync()", StringComparison.Ordinal));
        Assert.True(playClip >= 0 && !playback[playClip..].Contains("TryPlayFinishedCutAsync()", StringComparison.Ordinal));
    }

    [Fact]
    public void TryRead_round_trips_marks_for_fingerprint_match()
    {
        var a = NewClip(1, 1, 10);
        a.ApplyInOut(0.5, 8);
        var b = NewClip(2, 1, 6);
        var clips = new[] { a, b };
        var music = new CutMusic { FileName = "score.mp3" };
        var fp = CutPlayMerge.Fingerprint(clips, [], "score.mp3", music);
        var json = CutProjectFile.Serialize(clips, "score.mp3", movieFingerprint: fp, music: music);

        Assert.True(CutProjectFile.TryRead(json, out var read, out _, out var loaded, out var loadedMusic));
        Assert.Equal(fp, loaded);
        Assert.Equal("score.mp3", loadedMusic.FileName);
        Assert.Equal(0.5, read[0].MarkIn);
        Assert.Equal(8, read[0].MarkOut);
        Assert.True(CutPlayMerge.IsFreshMerge(loaded, read, [], loadedMusic.FileName, loadedMusic));
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
