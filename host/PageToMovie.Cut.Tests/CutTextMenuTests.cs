using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTextMenuTests
{
    [Fact]
    public void Backdrop_dismiss_does_not_swallow_an_item_click()
    {
        Assert.False(CutTextMenu.ShouldDismiss(pointerInsideMenu: true));
        Assert.True(CutTextMenu.ItemOwnsPointer(true));
        Assert.True(CutTextMenu.ShouldDismiss(pointerInsideMenu: false));
        Assert.False(CutTextMenu.ItemOwnsPointer(false));
    }

    [Fact]
    public void Duplicate_runs_with_an_open_menu_even_if_selection_was_cleared()
    {
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 2, hold: 4);

        var target = CutTextMenu.TargetOf(titles, menuTitleId: source.Id, selectedTextId: null);
        Assert.Same(source, target);
        Assert.True(CutTextMenu.CanRun(busy: false, target));

        Assert.True(CutTextMenu.TryRunItem(
            pointerInsideMenu: true,
            busy: false,
            titles,
            target,
            out var copy));
        Assert.Equal(2, titles.Count);
        Assert.NotNull(copy);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Smoke", copy.Text);
        Assert.Equal(3, copy.StartSec);
    }

    [Fact]
    public void Backdrop_pointer_closes_without_running_duplicate()
    {
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 1, hold: 3);

        Assert.False(CutTextMenu.TryRunItem(
            pointerInsideMenu: false,
            busy: false,
            titles,
            source,
            out var copy));
        Assert.Null(copy);
        Assert.Single(titles);
    }

    [Fact]
    public void Copy_and_delete_use_the_menu_target_not_live_selection()
    {
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 1, hold: 3);
        Seed(titles, "Other", start: 8, hold: 2);

        var target = CutTextMenu.TargetOf(titles, menuTitleId: source.Id, selectedTextId: titles[1].Id);
        Assert.Same(source, target);
        Assert.True(CutTextMenu.TryCopy(target, out var payload));
        Assert.Equal("Smoke", payload!.Text);
        Assert.Equal(3, payload.Seconds);

        Assert.True(CutTextMenu.TryDelete(busy: false, titles, source.Id));
        Assert.Single(titles);
        Assert.Equal("Other", titles[0].Text);
    }

    [Fact]
    public void Busy_or_missing_target_leaves_titles_unchanged()
    {
        var titles = new List<CutTextClip>();
        var source = Seed(titles, "Smoke", start: 2, hold: 6);

        Assert.False(CutTextMenu.TryDuplicate(busy: true, titles, source, out _));
        Assert.False(CutTextMenu.TryDuplicate(busy: false, titles, target: null, out _));
        Assert.False(CutTextMenu.TryDelete(busy: true, titles, source.Id));
        Assert.False(CutTextMenu.TryDelete(busy: false, titles, "missing"));
        Assert.False(CutTextMenu.TrySplit(busy: false, titles, source, playheadSec: 1));
        Assert.Single(titles);
        Assert.True(CutTextMenu.TrySplit(busy: false, titles, source, playheadSec: 5));
        Assert.Equal(2, titles.Count);
    }

    [Fact]
    public void Host_stacks_above_the_preview_overlay_and_old_sibling_backdrop()
    {
        Assert.Equal("cut-text-menu-host", CutTextMenu.HostClass);
        Assert.Equal("cut-text-menu-back", CutTextMenu.BackClass);
        Assert.Equal(CutTextMenu.PanelClass, CutTransport.TextMenuClass);
        Assert.Equal("cut-tl is-menu-open", CutTextMenu.TimelineClass(true));
        Assert.Equal("cut-tl", CutTextMenu.TimelineClass(false));
        Assert.True(CutTextMenu.HostZIndex > 31);
        Assert.True(CutTextMenu.HostZIndex > 2);
    }

    private static CutTextClip Seed(List<CutTextClip> titles, string text, double start, double hold)
    {
        var title = new CutTextClip
        {
            Id = CutTextClip.NewId(),
            Text = text,
            StartSec = start,
            Seconds = hold,
        };
        titles.Add(title);
        return title;
    }
}
