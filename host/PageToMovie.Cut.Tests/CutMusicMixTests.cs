using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutMusicMixTests
{
    [Fact]
    public void Defaults_are_full_volume_and_no_fade()
    {
        var music = new CutMusic { FileName = "score.mp3" };
        music.SetDuration(20);
        music.ApplyInOut(0, 12);
        Assert.Equal(100, music.VolumePercent);
        Assert.Equal(0, music.FadeInSec);
        Assert.Equal(0, music.FadeOutSec);
        Assert.False(music.HasMixEdits);

        var filter = CutMusicMix.ComplexFilter(music);
        Assert.Contains("volume=1", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("afade", filter, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2:duration=first", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Mix_args_include_volume_and_afade()
    {
        var music = new CutMusic { FileName = "score.mp3" };
        music.SetDuration(30);
        music.SetStart(4);
        music.ApplyInOut(2, 14);
        music.SetVolumePercent(40);
        music.SetFadeIn(1.5);
        music.SetFadeOut(2);

        var chain = CutMusicMix.VolumeChain(
            music.VolumePercent, music.FadeInSec, music.FadeOutSec, music.StartSec, music.SlicedDurationSec);
        Assert.Contains("volume=0.4", chain, StringComparison.Ordinal);
        Assert.Contains("afade=t=in:st=4:d=1.5", chain, StringComparison.Ordinal);
        Assert.Contains("afade=t=out:st=14:d=2", chain, StringComparison.Ordinal);

        var mix = CutComposeService.ToJsMix("blob:score", music);
        Assert.Equal(0.4, mix.Volume, 5);
        Assert.Equal(1.5, mix.FadeIn, 5);
        Assert.Equal(2, mix.FadeOut, 5);
        Assert.Contains("volume=0.4", mix.Filter, StringComparison.Ordinal);
        Assert.Contains("afade=t=in", mix.Filter, StringComparison.Ordinal);
        Assert.Contains("afade=t=out", mix.Filter, StringComparison.Ordinal);
        Assert.Contains("volume=0.4", mix.FallbackFilter, StringComparison.Ordinal);
        Assert.Contains("apad[a]", mix.FallbackFilter, StringComparison.Ordinal);

        var argv = string.Join(' ', CutFfmpegEncode.Argv(CutFfmpegEncodePath.Mix));
        Assert.Contains("volume=1", argv, StringComparison.Ordinal);
        Assert.Contains("-filter_complex", argv, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_title_font_or_align_rebuilds_that_scene()
    {
        var clip = NewClip(1, 1, 8);
        var titles = new List<CutTextClip>
        {
            new() { Text = "Hello", StartSec = 1, Seconds = 2 },
        };
        var plan = CutMergeCache.Build([clip], titles, null, null);
        var saved = CutMergeCache.ManifestOf(plan);

        titles[0].Style.Font = CutTextFont.Impact;
        var font = CutMergeCache.Diff(CutMergeCache.Build([clip], titles, null, null), saved);
        Assert.Equal(new[] { 1 }, font.RebuildScenes);
        Assert.False(font.PictureFresh);

        titles[0].Style.Font = CutTextFont.Sans;
        titles[0].Style.Align = CutTextAlign.Right;
        var align = CutMergeCache.Diff(CutMergeCache.Build([clip], titles, null, null), saved);
        Assert.Equal(new[] { 1 }, align.RebuildScenes);
    }

    [Fact]
    public void Changing_volume_or_fade_remixes_cached_picture()
    {
        var clip = NewClip(1, 1, 8);
        var music = new CutMusic { FileName = "score.mp3" };
        music.SetDuration(20);
        music.ApplyInOut(0, 10);
        var plan = CutMergeCache.Build([clip], [], "score.mp3", music);
        var saved = CutMergeCache.ManifestOf(plan);

        music.SetVolumePercent(25);
        var quieter = CutMergeCache.Diff(CutMergeCache.Build([clip], [], "score.mp3", music), saved);
        Assert.True(quieter.PictureFresh);
        Assert.False(quieter.MusicFresh);
        Assert.True(quieter.RemixMusicOnly);

        music.SetVolumePercent(100);
        music.SetFadeIn(1);
        var faded = CutMergeCache.Diff(CutMergeCache.Build([clip], [], "score.mp3", music), saved);
        Assert.True(faded.PictureFresh);
        Assert.False(faded.MusicFresh);
        Assert.True(faded.RemixMusicOnly);
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
