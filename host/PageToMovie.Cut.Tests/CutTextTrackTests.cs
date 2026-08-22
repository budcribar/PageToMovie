using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTextTrackTests
{
    [Fact]
    public void Scene_cards_sit_on_the_text_row_at_incoming_scene_start()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(1, 2, 4);
        var c = NewClip(2, 1, 4);
        c.Card.Enabled = true;
        c.Card.Text = "Chapter 1";
        c.Card.Seconds = 2;

        var blocks = CutTextTrack.Build([a, b, c], [], pxPerSec: 10);
        var card = Assert.Single(blocks);
        Assert.Equal(CutTextKind.SceneCard, card.Kind);
        Assert.Equal("Chapter 1", card.Text);
        Assert.Equal(8, card.StartSec);
        Assert.Equal(2, card.Seconds);
        Assert.Equal(80, card.StartPx);
        Assert.Equal(20, card.WidthPx);
        Assert.Equal(2, card.Scene);
        Assert.Same(c, card.CardClip);
    }

    [Fact]
    public void Disabled_cards_stay_off_the_row()
    {
        var clip = NewClip(1, 1, 4);
        clip.Card.Text = "Chapter 1";
        clip.Card.Enabled = false;
        Assert.Empty(CutTextTrack.Build([clip], [], pxPerSec: 10));
    }

    [Fact]
    public void Titles_appear_at_their_start_and_can_be_edited()
    {
        var clip = NewClip(1, 1, 10);
        var titles = new List<CutTextClip>();
        var added = CutTextTrack.Add(titles, 3.5);
        Assert.Equal("Title", added.Text);
        Assert.Equal(3.5, added.StartSec);
        Assert.Equal(CutCard.DefaultHoldSeconds, added.HoldSeconds);

        var blocks = CutTextTrack.Build([clip], titles, pxPerSec: 20);
        var block = Assert.Single(blocks);
        Assert.Equal(CutTextKind.Title, block.Kind);
        Assert.Equal(3.5, block.StartSec);
        Assert.Equal(70, block.StartPx);

        CutTextTrack.SetLabel(block, "  Opening  ");
        CutTextTrack.SetHold(block, 4);
        CutTextTrack.SetStart(block, 1);
        Assert.Equal("Opening", added.Text);
        Assert.Equal(4, added.Seconds);
        Assert.Equal(1, added.StartSec);
    }

    [Fact]
    public void Delete_disables_cards_and_removes_titles()
    {
        var clip = NewClip(2, 1, 4);
        clip.Card.Enabled = true;
        clip.Card.Text = "Chapter 2";
        var titles = new List<CutTextClip>();
        CutTextTrack.Add(titles, 1, "Title");
        var blocks = CutTextTrack.Build([clip], titles, pxPerSec: 10);
        Assert.Equal(2, blocks.Count);

        CutTextTrack.Delete(blocks[0].Kind == CutTextKind.SceneCard ? blocks[0] : blocks[1], titles);
        Assert.False(clip.Card.Enabled);
        CutTextTrack.Delete(blocks.First(b => b.Kind == CutTextKind.Title), titles);
        Assert.Empty(titles);
    }

    [Fact]
    public void Hold_clamp_rejects_tiny_and_huge_spans()
    {
        Assert.Equal(2, CutCard.ResolveHold(0));
        Assert.Equal(2, CutCard.ResolveHold(0.1));
        Assert.Equal(0.3, CutCard.ResolveHold(0.3));
        Assert.Equal(30, CutCard.ResolveHold(99));
        Assert.Equal(2, CutCard.ResolveHold(double.NaN));
    }

    [Fact]
    public void Compose_overlays_map_title_start_onto_the_clip()
    {
        var a = NewClip(1, 1, 5);
        var b = NewClip(1, 2, 5);
        var titles = new List<CutTextClip>
        {
            new() { Id = "t1", Text = "Hello", StartSec = 6.5, Seconds = 2 },
        };

        var overlays = CutTextTrack.OverlaysForCompose([a, b], titles);
        var one = Assert.Single(overlays);
        Assert.Equal(1, one.ClipIndex);
        Assert.Equal(1.5, one.LocalStart, 5);
        Assert.Equal("Hello", one.Text);
        Assert.Equal(2, one.Seconds);

        var payload = CutComposeService.BuildExportPayload([a, b], titles);
        Assert.Empty(payload[0].Texts);
        var wired = Assert.Single(payload[1].Texts);
        Assert.Equal("Hello", wired.Text);
        Assert.Equal(1.5, wired.Start, 5);
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
