namespace PageToMovie.Cut.Cut;

/// <summary>
/// Title/music context-menu chrome and title commands.
/// The backdrop is a full-viewport dismiss layer; a pointer on the
/// menu panel is an item gesture and must not be treated as dismiss —
/// otherwise Duplicate/Copy/Delete/Split/Edit duration are no-ops.
/// </summary>
public static class CutTextMenu
{
    public const string HostClass = "cut-text-menu-host";
    public const string BackClass = "cut-text-menu-back";
    public const string PanelClass = "cut-text-menu";
    public const string OpenTimelineClass = "is-menu-open";
    public const int HostZIndex = 4000;

    public static string TimelineClass(bool menuOpen) =>
        menuOpen ? $"cut-tl {OpenTimelineClass}" : "cut-tl";

    /// <summary>
    /// Backdrop click (outside the panel) dismisses. A click that
    /// landed on the menu is an item, not a dismiss.
    /// </summary>
    public static bool ShouldDismiss(bool pointerInsideMenu) =>
        !pointerInsideMenu;

    public static bool ItemOwnsPointer(bool pointerInsideMenu) =>
        pointerInsideMenu;

    public static CutTextClip? TargetOf(
        IEnumerable<CutTextClip> titles,
        string? menuTitleId,
        string? selectedTextId) =>
        CutTextEdit.Find(titles, menuTitleId) ?? CutTextEdit.Find(titles, selectedTextId);

    public static bool CanRun(bool busy, CutTextClip? target) =>
        !busy && target is not null;

    public static bool TryDuplicate(
        bool busy,
        IList<CutTextClip> titles,
        CutTextClip? target,
        out CutTextClip? copy) =>
        TryDuplicate(busy, titles, target, out copy, occupied: null, movieEnd: double.PositiveInfinity);

    public static bool TryDuplicate(
        bool busy,
        IList<CutTextClip> titles,
        CutTextClip? target,
        out CutTextClip? copy,
        IReadOnlyList<CutTextPlace.Span>? occupied,
        double movieEnd)
    {
        copy = null;
        if (!CanRun(busy, target) || target is null)
            return false;
        copy = CutTextEdit.Duplicate(titles, target, occupied, movieEnd);
        return true;
    }

    public static bool TryCopy(CutTextClip? target, out CutTextPayload? payload)
    {
        payload = null;
        if (target is null)
            return false;
        payload = CutTextEdit.Copy(target);
        return true;
    }

    public static bool TryDelete(bool busy, IList<CutTextClip> titles, string? targetId)
    {
        if (busy)
            return false;
        var title = CutTextEdit.Find(titles, targetId);
        if (title is null)
            return false;
        for (var i = titles.Count - 1; i >= 0; i--)
        {
            if (titles[i].Id == title.Id)
                titles.RemoveAt(i);
        }

        return true;
    }

    public static bool TrySplit(
        bool busy,
        IList<CutTextClip> titles,
        CutTextClip? target,
        double playheadSec) =>
        CanRun(busy, target)
        && target is not null
        && CutTextEdit.TrySplit(titles, target, playheadSec, out _);

    /// <summary>
    /// Same-turn backdrop + item: the item runs. Backdrop-only closes.
    /// </summary>
    public static bool TryRunItem(
        bool pointerInsideMenu,
        bool busy,
        IList<CutTextClip> titles,
        CutTextClip? target,
        out CutTextClip? duplicated)
    {
        duplicated = null;
        if (ShouldDismiss(pointerInsideMenu))
            return false;
        return TryDuplicate(busy, titles, target, out duplicated);
    }
}
