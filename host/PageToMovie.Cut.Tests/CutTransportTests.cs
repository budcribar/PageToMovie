using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTransportTests
{
    [Fact]
    public void Playhead_is_high_contrast_white()
    {
        Assert.Equal("cut-tl-playhead", CutTransport.PlayheadClass);
        Assert.Equal("#ffffff", CutTransport.PlayheadColor);
    }

    [Fact]
    public void Play_becomes_stop_while_playing()
    {
        Assert.Equal("Play", CutTransport.PlayTitle(false));
        Assert.Equal("▶", CutTransport.PlayGlyph(false));
        Assert.Equal("cut-tl-play", CutTransport.PlayButtonClass(false));

        Assert.Equal("Stop", CutTransport.PlayTitle(true));
        Assert.Equal("⏹", CutTransport.PlayGlyph(true));
        Assert.Equal("cut-tl-play is-stop", CutTransport.PlayButtonClass(true));
        Assert.Contains(CutTransport.StopClass, CutTransport.PlayButtonClass(true), StringComparison.Ordinal);
    }

    [Fact]
    public void Play_enables_once_any_current_take_is_ready()
    {
        var ready = NewClip(1, 1, preview: "blob:take");
        var missing = NewClip(1, 2);
        Assert.False(CutTransport.CanPlay([]));
        Assert.False(CutTransport.CanPlay([missing]));
        Assert.True(CutTransport.CanPlay([ready]));
        Assert.True(CutTransport.CanPlay([missing, ready]));
        Assert.Single(CutTransport.PlayableClips([missing, ready]));
    }

    [Fact]
    public void Add_text_stays_above_the_first_title_tile()
    {
        Assert.Equal("cut-tl-text-add", CutTransport.TextAddClass);
        Assert.Equal("cut-tl-text-clip", CutTransport.TextClipClass);
        Assert.Equal("cut-text-menu", CutTransport.TextMenuClass);
        Assert.True(CutTransport.TextAddZIndex > CutTransport.TextClipZIndex);
        Assert.True(CutTransport.TextAddZIndex > 4);
    }

    private static CutClip NewClip(int scene, int clip, string? preview = null)
    {
        var c = new CutClip { Scene = scene, Clip = clip };
        c.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            RelativePath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            PreviewUrl = preview,
        });
        c.ActiveTakeNumber = 1;
        c.SeedSelection();
        return c;
    }
}
