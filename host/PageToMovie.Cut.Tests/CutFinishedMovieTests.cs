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
    public void Muting_a_clip_survives_a_save_and_changes_the_merge_fingerprint()
    {
        var clips = new[] { NewClip(1, 1, 4), NewClip(1, 2, 4) };
        var music = new CutMusic();
        var before = CutPlayMerge.Fingerprint(clips, [], null, music);

        clips[0].Muted = true;
        var after = CutPlayMerge.Fingerprint(clips, [], null, music);
        Assert.NotEqual(before, after);

        // A saved cut round-trips the flag, so a reopened project keeps the silence.
        var json = CutProjectFile.Serialize(clips, null, [], after, music);
        Assert.True(CutProjectFile.TryRead(json, out var read, out _, out _, out _));
        Assert.True(read.Single(c => c.Scene == 1 && c.Clip == 1).Muted);
        Assert.False(read.Single(c => c.Scene == 1 && c.Clip == 2).Muted);

        // And the saved movie is stale against the un-muted fingerprint it was built with.
        Assert.False(CutFinishedMovie.ShouldPlay(
            CutProjectFile.Serialize(clips, null, [], before, music), movieFilePresent: true));
    }

    [Fact]
    public void Extras_report_the_music_and_titles_a_stitch_would_drop()
    {
        var clips = new[] { NewClip(1, 1, 4) };
        var titles = new[] { new CutTextClip { Text = "Mary had a little lamb", StartSec = 0, Seconds = 3 } };
        var music = new CutMusic { FileName = "score.mp3" };

        var both = CutFinishedMovie.ExtrasInSavedCut(
            CutProjectFile.Serialize(clips, "score.mp3", titles, null, music));
        Assert.True(both.Music);
        Assert.True(both.Titles);
        Assert.True(both.Any);

        var titlesOnly = CutFinishedMovie.ExtrasInSavedCut(
            CutProjectFile.Serialize(clips, null, titles, null, new CutMusic()));
        Assert.False(titlesOnly.Music);
        Assert.True(titlesOnly.Titles);

        var musicOnly = CutFinishedMovie.ExtrasInSavedCut(
            CutProjectFile.Serialize(clips, "score.mp3", [], null, music));
        Assert.True(musicOnly.Music);
        Assert.False(musicOnly.Titles);

        // A picture-only cut loses nothing in a stitch, and neither does an unreadable file.
        var plain = CutFinishedMovie.ExtrasInSavedCut(CutProjectFile.Serialize(clips, null));
        Assert.False(plain.Any);
        Assert.False(CutFinishedMovie.ExtrasInSavedCut(null).Any);
        Assert.False(CutFinishedMovie.ExtrasInSavedCut("{not-json").Any);
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
    public void ChooseUrl_prefers_finished_movie_over_stitch()
    {
        Assert.Equal("blob:cut", CutFinishedMovie.ChooseUrl("blob:cut", "blob:stitch"));
        Assert.Equal("blob:cut", CutFinishedMovie.ChooseUrl("blob:cut", null));
        Assert.Equal("blob:stitch", CutFinishedMovie.ChooseUrl(null, "blob:stitch"));
        Assert.Equal("blob:stitch", CutFinishedMovie.ChooseUrl("", "blob:stitch"));
        Assert.Equal("blob:stitch", CutFinishedMovie.ChooseUrl("   ", "blob:stitch"));
        Assert.Null(CutFinishedMovie.ChooseUrl(null, null));
        Assert.Null(CutFinishedMovie.ChooseUrl("", "  "));
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
    public void Review_share_reuses_play_finished_cut_helper()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var share = File.ReadAllText(Path.Combine(
            host, "PageToMovie.Web", "Components", "Pages", "ReviewShare.cs"));

        var ensure = share.IndexOf("internal async Task<string?> EnsureShareableMovieUrlAsync()", StringComparison.Ordinal);
        var stitch = share.IndexOf("StitchShareableMovieAsync()", StringComparison.Ordinal);
        var choose = share.IndexOf("CutFinishedMovie.ChooseUrl", StringComparison.Ordinal);
        var resolve = share.IndexOf("TryResolveFinishedCutUrlAsync()", StringComparison.Ordinal);
        Assert.True(ensure >= 0 && resolve > ensure && choose > resolve && stitch > choose);
    }

    [Fact]
    public void Web_cut_chrome_is_redirect_only()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var nav = File.ReadAllText(Path.Combine(host, "PageToMovie.Web", "Components", "Layout", "NavMenu.razor"));
        var strip = File.ReadAllText(Path.Combine(host, "PageToMovie.Web", "Components", "Shared", "StudioProcessStrip.razor"));
        var routes = File.ReadAllText(Path.Combine(host, "PageToMovie.Web", "AppRoutes.cs"));
        var cutPage = File.ReadAllText(Path.Combine(host, "PageToMovie.Web", "Components", "Pages", "Cut.razor"));

        Assert.DoesNotContain("href=\"/cut\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("nav-cut", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("studio-step-cut", strip, StringComparison.Ordinal);
        Assert.Contains("Redirect to Review Finish", routes, StringComparison.Ordinal);
        Assert.Contains("@page \"/cut\"", cutPage, StringComparison.Ordinal);
        Assert.Contains("/review?tab=finish", cutPage, StringComparison.Ordinal);
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
