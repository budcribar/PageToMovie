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
}
