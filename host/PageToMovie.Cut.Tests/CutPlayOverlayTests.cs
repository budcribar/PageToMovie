using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutPlayOverlayTests
{
    [Fact]
    public void Live_overlay_is_for_native_preview_not_composed_movie()
    {
        Assert.True(CutPlayOverlay.UseLiveOverlay(showingComposedMovie: false));
        Assert.False(CutPlayOverlay.UseLiveOverlay(showingComposedMovie: true));
    }

    [Fact]
    public void Cues_include_titles_and_cards_on_the_playhead()
    {
        var a = NewClip(1, 1, 6);
        a.Card.Enabled = true;
        a.Card.Text = "Chapter 1";
        a.Card.Seconds = 2;
        var titles = new List<CutTextClip>
        {
            new() { Id = "t1", Text = "Hello", StartSec = 3, Seconds = 2 },
        };
        titles[0].Style.Position = CutTextPosition.LowerThird;
        titles[0].Style.Color = CutTextColor.Yellow;
        titles[0].Style.Background = CutTextBackground.DarkBar;
        titles[0].Style.Fade = CutTextFade.Short;
        titles[0].Style.Font = CutTextFont.Georgia;
        titles[0].Style.Align = CutTextAlign.Left;

        var cues = CutPlayOverlay.Cues([a], titles);
        Assert.Equal(2, cues.Count);

        var card = Assert.Single(cues, c => c.Text == "Chapter 1");
        Assert.Equal(0, card.StartSec);
        Assert.Equal(2, card.EndSec);
        Assert.Equal(CutTextStyle.DefaultFontPx, card.FontPx);
        Assert.Equal(CutTextStyle.DefaultColorHex, card.ColorHex);
        Assert.False(card.Bar);

        var title = Assert.Single(cues, c => c.Text == "Hello");
        Assert.Equal(3, title.StartSec);
        Assert.Equal(5, title.EndSec);
        Assert.Equal(CutTextStyle.YOf(CutTextPosition.LowerThird), title.Y);
        Assert.Equal(CutTextStyle.ColorHexOf(CutTextColor.Yellow), title.ColorHex);
        Assert.True(title.Bar);
        Assert.True(title.FadeSec > 0);
        Assert.Equal("georgia", title.Font);
        Assert.Equal("left", title.Align);
        Assert.Contains("Georgia", title.CssFont, StringComparison.Ordinal);
        Assert.Equal(CutTextStyle.LeftX, title.X);

        Assert.Equal(card, CutPlayOverlay.ActiveAt(cues, 0.2));
        Assert.Null(CutPlayOverlay.ActiveAt(cues, 2.1));
        Assert.Equal(title, CutPlayOverlay.ActiveAt(cues, 3.5));
        Assert.Null(CutPlayOverlay.ActiveAt(cues, 5));
    }

    [Fact]
    public void Cut_to_black_does_not_invent_a_scene_card_cue()
    {
        var a = NewClip(1, 1, 6);
        var b = NewClip(2, 1, 6);
        a.JoinOverride = CutJoinKind.CutToBlack;

        Assert.Empty(CutPlayOverlay.Cues([a, b], []));
        Assert.Null(CutPlayOverlay.ActiveAt([], 6));
        Assert.False(b.Card.Enabled);
        Assert.False(CutComposeContract.JoinIsSceneCard(CutJoinKind.CutToBlack));
    }

    [Fact]
    public void Fade_opacity_eases_in_and_out()
    {
        var cue = new CutPlayOverlayCue
        {
            StartSec = 2,
            EndSec = 5,
            Text = "Hello",
            FadeSec = 0.3,
        };
        Assert.Equal(0, CutPlayOverlay.Opacity(cue, 1.9));
        Assert.Equal(0, CutPlayOverlay.Opacity(cue, 2), 5);
        Assert.True(CutPlayOverlay.Opacity(cue, 2.15) is > 0 and < 1);
        Assert.Equal(1, CutPlayOverlay.Opacity(cue, 3.5));
        Assert.True(CutPlayOverlay.Opacity(cue, 4.9) is > 0 and < 1);
        Assert.Equal(0, CutPlayOverlay.Opacity(cue, 5));
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
