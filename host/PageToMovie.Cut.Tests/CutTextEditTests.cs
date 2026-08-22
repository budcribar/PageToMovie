using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTextEditTests
{
    [Fact]
    public void Duplicate_copies_text_style_and_duration_later_on_the_row()
    {
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 2, hold: 4);
        source.Style.Color = CutTextColor.Yellow;
        source.Style.Position = CutTextPosition.LowerThird;
        source.Style.Background = CutTextBackground.DarkBar;

        var copy = CutTextEdit.Duplicate(titles, source);
        Assert.Equal(2, titles.Count);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Smoke", copy.Text);
        Assert.Equal(4, copy.HoldSeconds);
        Assert.Equal(3, copy.StartSec);
        Assert.Equal(CutTextColor.Yellow, copy.Style.Color);
        Assert.Equal(CutTextPosition.LowerThird, copy.Style.Position);
        Assert.Equal(CutTextBackground.DarkBar, copy.Style.Background);
        Assert.NotSame(source.Style, copy.Style);
        Assert.NotSame(source, copy);
    }

    [Fact]
    public void Copy_then_paste_at_playhead_keeps_payload_and_new_id()
    {
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 1, hold: 3);
        source.Style.Size = CutTextSize.L;
        source.Style.Fade = CutTextFade.Short;

        var payload = CutTextEdit.Copy(source);
        Assert.Equal("Smoke", payload.Text);
        Assert.Equal(3, payload.Seconds);
        Assert.Equal(CutTextSize.L, payload.Style.Size);
        Assert.Equal(CutTextFade.Short, payload.Style.Fade);

        var pasted = CutTextEdit.Paste(titles, payload, startSec: 8);
        Assert.Equal(2, titles.Count);
        Assert.NotEqual(source.Id, pasted.Id);
        Assert.Equal("Smoke", pasted.Text);
        Assert.Equal(8, pasted.StartSec);
        Assert.Equal(3, pasted.HoldSeconds);
        Assert.Equal(CutTextSize.L, pasted.Style.Size);
        Assert.Equal(CutTextFade.Short, pasted.Style.Fade);
        source.Style.Size = CutTextSize.S;
        Assert.Equal(CutTextSize.L, pasted.Style.Size);
    }

    [Fact]
    public void Paste_sits_just_after_the_selection_when_playhead_is_on_it()
    {
        var selected = NewTitle("Smoke", start: 2, hold: 4);
        Assert.Equal(6, CutTextEdit.PasteStart(3.5, selected));
        Assert.Equal(10, CutTextEdit.PasteStart(10, selected));
        Assert.Equal(5, CutTextEdit.PasteStart(5, selected: null));
        Assert.Equal(0, CutTextEdit.PasteStart(-2, selected: null));
    }

    [Fact]
    public void Delete_removes_the_title_from_the_row()
    {
        var clip = NewClip(1, 1, 10);
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 2, hold: 3);
        var blocks = CutTextTrack.Build([clip], titles, pxPerSec: 10);
        var block = Assert.Single(blocks);

        CutTextTrack.Delete(block, titles);
        Assert.Empty(titles);
        Assert.Equal(source.Id, block.Id);
    }

    [Fact]
    public void Split_at_playhead_makes_two_adjacent_titles()
    {
        var titles = new List<CutTextClip>();
        var left = Seed(titles, "Smoke", start: 2, hold: 6);
        left.Style.Color = CutTextColor.Black;

        Assert.False(CutTextEdit.CanSplit(left, 2));
        Assert.False(CutTextEdit.CanSplit(left, 2.2));
        Assert.False(CutTextEdit.CanSplit(left, 7.8));
        Assert.False(CutTextEdit.CanSplit(left, 8));
        Assert.True(CutTextEdit.CanSplit(left, 5));

        Assert.True(CutTextEdit.TrySplit(titles, left, 5, out var right));
        Assert.Equal(2, titles.Count);
        Assert.Same(left, titles[0]);
        Assert.Same(right, titles[1]);
        Assert.Equal(2, left.StartSec);
        Assert.Equal(3, left.HoldSeconds);
        Assert.Equal(5, right!.StartSec);
        Assert.Equal(3, right.HoldSeconds);
        Assert.Equal("Smoke", right.Text);
        Assert.Equal(CutTextColor.Black, right.Style.Color);
        Assert.NotEqual(left.Id, right.Id);
    }

    [Fact]
    public void Split_is_a_no_op_when_playhead_is_outside_the_title()
    {
        var titles = new List<CutTextClip>();
        var title = Seed(titles, "Smoke", start: 4, hold: 2);
        Assert.False(CutTextEdit.TrySplit(titles, title, 3.9, out var right));
        Assert.Null(right);
        Assert.Single(titles);
        Assert.Equal(2, title.HoldSeconds);
    }

    [Fact]
    public void Shortcuts_run_only_when_not_typing()
    {
        Assert.Equal(CutTextShortcut.Duplicate, CutTextEdit.ShortcutOf("d", ctrlOrMeta: true, textFieldFocused: false));
        Assert.Equal(CutTextShortcut.Copy, CutTextEdit.ShortcutOf("C", ctrlOrMeta: true, textFieldFocused: false));
        Assert.Equal(CutTextShortcut.Paste, CutTextEdit.ShortcutOf("v", ctrlOrMeta: true, textFieldFocused: false));
        Assert.Equal(CutTextShortcut.Delete, CutTextEdit.ShortcutOf("Delete", ctrlOrMeta: false, textFieldFocused: false));
        Assert.Equal(CutTextShortcut.Split, CutTextEdit.ShortcutOf("s", ctrlOrMeta: false, textFieldFocused: false));

        Assert.Equal(CutTextShortcut.None, CutTextEdit.ShortcutOf("d", ctrlOrMeta: false, textFieldFocused: false));
        Assert.Equal(CutTextShortcut.None, CutTextEdit.ShortcutOf("s", ctrlOrMeta: true, textFieldFocused: false));
        Assert.Equal(CutTextShortcut.None, CutTextEdit.ShortcutOf("d", ctrlOrMeta: true, textFieldFocused: true));
        Assert.Equal(CutTextShortcut.None, CutTextEdit.ShortcutOf("Delete", ctrlOrMeta: false, textFieldFocused: true));
        Assert.True(CutTextEdit.PreventsBrowserDefault(CutTextShortcut.Duplicate));
        Assert.False(CutTextEdit.PreventsBrowserDefault(CutTextShortcut.None));
    }

    [Fact]
    public void Duplicate_and_delete_round_trip_through_cut_project_json()
    {
        var clip = NewClip(1, 1, 12);
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 1, hold: 45);
        source.Style.Color = CutTextColor.Yellow;
        CutTextEdit.Duplicate(titles, source);
        Assert.Equal(2, titles.Count);

        var json = CutProjectFile.Serialize([clip], null, titles);
        Assert.Contains("Smoke", json, StringComparison.Ordinal);
        var reload = NewClip(1, 1, 12);
        Assert.True(CutProjectFile.TryApply([reload], json, out _, out var loaded));
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, t => Assert.Equal("Smoke", t.Text));
        Assert.All(loaded, t => Assert.Equal(45, t.HoldSeconds));
        Assert.All(loaded, t => Assert.Equal(CutTextColor.Yellow, t.Style.Color));
        Assert.Equal(1, loaded[0].StartSec);
        Assert.Equal(2, loaded[1].StartSec);

        var blocks = CutTextTrack.Build([reload], loaded, pxPerSec: 10);
        CutTextTrack.Delete(blocks[0], loaded);
        Assert.Single(loaded);
        var after = CutProjectFile.Serialize([reload], null, loaded);
        Assert.True(CutProjectFile.TryApply([NewClip(1, 1, 12)], after, out _, out var kept));
        Assert.Single(kept);
    }

    [Fact]
    public void TitleAt_returns_the_title_under_the_playhead()
    {
        var titles = new List<CutTextClip>
        {
            NewTitle("A", start: 1, hold: 2),
            NewTitle("B", start: 4, hold: 2),
        };
        Assert.Equal("A", CutTextEdit.TitleAt(titles, 1)?.Text);
        Assert.Equal("A", CutTextEdit.TitleAt(titles, 2.9)?.Text);
        Assert.Null(CutTextEdit.TitleAt(titles, 3));
        Assert.Equal("B", CutTextEdit.TitleAt(titles, 4)?.Text);
        Assert.Null(CutTextEdit.TitleAt(titles, 6));
        Assert.Equal(titles[1], CutTextEdit.Find(titles, titles[1].Id));
        Assert.Null(CutTextEdit.Find(titles, "missing"));
    }

    private static CutTextClip Seed(List<CutTextClip> titles, string text, double start, double hold)
    {
        var title = NewTitle(text, start, hold);
        titles.Add(title);
        return title;
    }

    private static CutTextClip NewTitle(string text, double start, double hold) =>
        new()
        {
            Id = CutTextClip.NewId(),
            Text = text,
            StartSec = start,
            Seconds = hold,
        };

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
