using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTextStyleTests
{
    [Fact]
    public void Defaults_are_centered_white_medium_no_bar_no_fade()
    {
        var style = new CutTextStyle();
        Assert.True(style.IsDefault);
        Assert.Equal(CutTextPosition.Center, style.Position);
        Assert.Equal(CutTextSize.M, style.Size);
        Assert.Equal(CutTextColor.White, style.Color);
        Assert.Equal(CutTextBackground.None, style.Background);
        Assert.Equal(CutTextFade.None, style.Fade);
        Assert.Equal(48, style.FontPx);
        Assert.Equal(48, CutTextStyle.DefaultFontPx);
        Assert.Equal("#ffffff", style.ColorHex);
        Assert.Equal(360, style.Y);
        Assert.False(style.HasBar);
        Assert.Equal(0, style.FadeSec(2));
    }

    [Theory]
    [InlineData(CutTextPosition.Center, 360, "center")]
    [InlineData(CutTextPosition.LowerThird, 600, "lowerThird")]
    [InlineData(CutTextPosition.Top, 120, "top")]
    public void Position_maps_to_y_and_wire(CutTextPosition position, int y, string wire)
    {
        Assert.Equal(y, CutTextStyle.YOf(position));
        Assert.Equal(wire, CutTextStyle.WirePosition(position));
        Assert.Equal(position, CutTextStyle.ParsePosition(wire));
    }

    [Theory]
    [InlineData(CutTextSize.S, 32, "s")]
    [InlineData(CutTextSize.M, 48, "m")]
    [InlineData(CutTextSize.L, 72, "l")]
    public void Size_maps_to_px_and_wire(CutTextSize size, int px, string wire)
    {
        Assert.Equal(px, CutTextStyle.FontPxOf(size));
        Assert.Equal(wire, CutTextStyle.WireSize(size));
        Assert.Equal(size, CutTextStyle.ParseSize(wire));
    }

    [Theory]
    [InlineData(CutTextColor.White, "#ffffff", "white")]
    [InlineData(CutTextColor.Yellow, "#f5d76e", "yellow")]
    [InlineData(CutTextColor.Black, "#111111", "black")]
    public void Color_maps_to_hex_and_wire(CutTextColor color, string hex, string wire)
    {
        Assert.Equal(hex, CutTextStyle.ColorHexOf(color));
        Assert.Equal(wire, CutTextStyle.WireColor(color));
        Assert.Equal(color, CutTextStyle.ParseColor(wire));
    }

    [Theory]
    [InlineData(CutTextBackground.None, false, "none")]
    [InlineData(CutTextBackground.DarkBar, true, "bar")]
    public void Background_maps_to_bar_flag(CutTextBackground background, bool bar, string wire)
    {
        var style = new CutTextStyle { Background = background };
        Assert.Equal(bar, style.HasBar);
        Assert.Equal(wire, CutTextStyle.WireBackground(background));
        Assert.Equal(background, CutTextStyle.ParseBackground(wire));
    }

    [Theory]
    [InlineData(CutTextFade.None, 2, 0)]
    [InlineData(CutTextFade.Short, 2, 0.3)]
    [InlineData(CutTextFade.Short, 0.6, 0.2)]
    public void Fade_maps_to_seconds(CutTextFade fade, double hold, double expected)
    {
        Assert.Equal(expected, CutTextStyle.FadeSeconds(fade, hold), 5);
        Assert.Equal(fade == CutTextFade.Short ? "short" : "none", CutTextStyle.WireFade(fade));
        Assert.Equal(fade, CutTextStyle.ParseFade(CutTextStyle.WireFade(fade)));
    }

    [Fact]
    public void Compose_payload_uses_mapped_style_for_title_and_card()
    {
        var clip = NewClip(1, 1, 8);
        clip.Card.Enabled = true;
        clip.Card.Text = "Chapter 1";
        clip.Card.Style.Position = CutTextPosition.Top;
        clip.Card.Style.Size = CutTextSize.S;
        clip.Card.Style.Color = CutTextColor.Yellow;
        clip.Card.Style.Background = CutTextBackground.DarkBar;
        clip.Card.Style.Fade = CutTextFade.Short;
        var title = new CutTextClip { Id = "t1", Text = "Hello", StartSec = 1, Seconds = 2 };
        title.Style.Position = CutTextPosition.LowerThird;
        title.Style.Size = CutTextSize.L;
        title.Style.Color = CutTextColor.Black;

        var payload = CutComposeService.BuildExportPayload([clip], [title]);
        var card = payload[0].Card;
        Assert.NotNull(card);
        Assert.Equal(32, card!.Style!.FontPx);
        Assert.Equal("#f5d76e", card.Style.Color);
        Assert.Equal(120, card.Style.Y);
        Assert.True(card.Style.Bar);
        Assert.Equal(0.3, card.Style.FadeSec, 5);

        var over = Assert.Single(payload[0].Texts);
        Assert.Equal(72, over.Style!.FontPx);
        Assert.Equal("#111111", over.Style.Color);
        Assert.Equal(600, over.Style.Y);
        Assert.False(over.Style.Bar);
        Assert.Equal(0, over.Style.FadeSec);
    }

    [Fact]
    public void Inspector_defaults_match_compose_wire()
    {
        var wired = CutComposeService.ToJsStyle(new CutTextStyle(), 2);
        Assert.Equal(48, wired.FontPx);
        Assert.Equal("#ffffff", wired.Color);
        Assert.Equal(360, wired.Y);
        Assert.False(wired.Bar);
        Assert.Equal(0, wired.FadeSec);
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
