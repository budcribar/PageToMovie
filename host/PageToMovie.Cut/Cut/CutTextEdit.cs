namespace PageToMovie.Cut.Cut;

/// <summary>
/// Title clipboard + edit ops for the text row and live overlay.
/// Duplicate / copy / paste / split stay on <see cref="CutTextClip"/> —
/// scene cards keep their own delete/hold path.
/// </summary>
public static class CutTextEdit
{
    public const double DuplicateOffsetSeconds = 1;
    public const string DuplicateKeys = "Ctrl+D";
    public const string CopyKeys = "Ctrl+C";
    public const string PasteKeys = "Ctrl+V";
    public const string DeleteKeys = "Del";
    public const string SplitKeys = "S";

    public static CutTextClip CloneAt(CutTextClip source, double startSec)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new CutTextClip
        {
            Id = CutTextClip.NewId(),
            Text = source.Text,
            StartSec = Math.Max(0, startSec),
            Seconds = source.HoldSeconds,
        };
        copy.Style.CopyFrom(source.Style);
        return copy;
    }

    public static CutTextClip Duplicate(IList<CutTextClip> titles, CutTextClip source) =>
        Duplicate(titles, source, occupied: null, movieEnd: double.PositiveInfinity);

    public static CutTextClip Duplicate(
        IList<CutTextClip> titles,
        CutTextClip source,
        IReadOnlyList<CutTextPlace.Span>? occupied,
        double movieEnd)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var blocked = occupied ?? CutTextPlace.FromTitles(titles);
        var preferred = source.StartSec + DuplicateOffsetSeconds;
        var copy = CloneAt(source, CutTextPlace.Place(preferred, source.HoldSeconds, blocked, movieEnd));
        titles.Add(copy);
        return copy;
    }

    public static CutTextPayload Copy(CutTextClip source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var payload = new CutTextPayload
        {
            Text = source.Text,
            Seconds = source.HoldSeconds,
        };
        payload.Style.CopyFrom(source.Style);
        return payload;
    }

    public static CutTextClip Paste(IList<CutTextClip> titles, CutTextPayload payload, double startSec) =>
        Paste(titles, payload, startSec, occupied: null, movieEnd: double.PositiveInfinity);

    public static CutTextClip Paste(
        IList<CutTextClip> titles,
        CutTextPayload payload,
        double startSec,
        IReadOnlyList<CutTextPlace.Span>? occupied,
        double movieEnd)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(payload);
        var hold = CutCard.ResolveHold(payload.Seconds);
        var blocked = occupied ?? CutTextPlace.FromTitles(titles);
        var copy = new CutTextClip
        {
            Id = CutTextClip.NewId(),
            Text = string.IsNullOrWhiteSpace(payload.Text) ? "Title" : payload.Text,
            StartSec = CutTextPlace.Place(startSec, hold, blocked, movieEnd),
            Seconds = hold,
        };
        copy.Style.CopyFrom(payload.Style);
        titles.Add(copy);
        return copy;
    }

    /// <summary>
    /// Paste at the playhead. If that would land on the selected title,
    /// place the copy just after it.
    /// </summary>
    public static double PasteStart(double playheadSec, CutTextClip? selected)
    {
        var at = Math.Max(0, playheadSec);
        if (selected is not null && Contains(selected, at))
            return selected.StartSec + selected.HoldSeconds;
        return at;
    }

    public static bool Contains(CutTextClip title, double timelineSec)
    {
        var start = Math.Max(0, title.StartSec);
        var end = start + title.HoldSeconds;
        return timelineSec >= start && timelineSec < end;
    }

    public static bool CanSplit(CutTextClip title, double playheadSec)
    {
        var start = Math.Max(0, title.StartSec);
        var end = start + title.HoldSeconds;
        return playheadSec >= start + CutCard.MinHoldSeconds
            && playheadSec <= end - CutCard.MinHoldSeconds;
    }

    public static bool TrySplit(
        IList<CutTextClip> titles,
        CutTextClip title,
        double playheadSec,
        out CutTextClip? right)
    {
        ArgumentNullException.ThrowIfNull(titles);
        right = null;
        if (!CanSplit(title, playheadSec))
            return false;

        var start = Math.Max(0, title.StartSec);
        var end = start + title.HoldSeconds;
        title.Seconds = playheadSec - start;
        right = CloneAt(title, playheadSec);
        right.Seconds = end - playheadSec;

        var at = titles.ToList().FindIndex(t => ReferenceEquals(t, title) || t.Id == title.Id);
        if (at < 0)
            titles.Add(right);
        else
            titles.Insert(at + 1, right);
        return true;
    }

    public static CutTextClip? Find(IEnumerable<CutTextClip> titles, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        foreach (var title in titles)
        {
            if (title.Id == id)
                return title;
        }

        return null;
    }

    public static CutTextClip? TitleAt(IEnumerable<CutTextClip> titles, double timelineSec) =>
        titles.LastOrDefault(title => Contains(title, timelineSec));

    public static CutTextShortcut ShortcutOf(string? key, bool ctrlOrMeta, bool textFieldFocused)
    {
        if (textFieldFocused || string.IsNullOrEmpty(key))
            return CutTextShortcut.None;
        return ShortcutFromKey(key, ctrlOrMeta);
    }

    private static CutTextShortcut ShortcutFromKey(string key, bool ctrlOrMeta)
    {
        if (ctrlOrMeta && key is "d" or "D")
            return CutTextShortcut.Duplicate;
        if (ctrlOrMeta && key is "c" or "C")
            return CutTextShortcut.Copy;
        if (ctrlOrMeta && key is "v" or "V")
            return CutTextShortcut.Paste;
        if (key is "Delete" or "Backspace")
            return CutTextShortcut.Delete;
        if (!ctrlOrMeta && key is "s" or "S")
            return CutTextShortcut.Split;
        return CutTextShortcut.None;
    }

    public static bool PreventsBrowserDefault(CutTextShortcut shortcut) =>
        shortcut is not CutTextShortcut.None;
}

public enum CutTextShortcut
{
    None,
    Duplicate,
    Copy,
    Paste,
    Delete,
    Split,
}

/// <summary>In-project title clipboard: text, style, and duration.</summary>
public sealed class CutTextPayload
{
    public string Text { get; init; } = "";
    public double Seconds { get; init; } = CutCard.DefaultHoldSeconds;
    public CutTextStyle Style { get; } = new();
}
