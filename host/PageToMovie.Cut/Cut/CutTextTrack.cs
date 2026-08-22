namespace PageToMovie.Cut.Cut;

public enum CutTextKind
{
    SceneCard,
    Title,
}

/// <summary>
/// One text row: scene cards at the incoming scene start, plus free titles.
/// Cards stay pinned to the scene-change; titles keep their own start.
/// </summary>
public static class CutTextTrack
{
    public static IReadOnlyList<CutTextBlock> Build(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip> titles,
        double pxPerSec)
    {
        var layout = CutTimelineLayout.Build(clips, pxPerSec);
        var px = layout.PxPerSec;
        var blocks = new List<CutTextBlock>();

        foreach (var band in layout.Scenes)
        {
            if (band.FirstIndex < 0 || band.FirstIndex >= clips.Count)
                continue;
            var first = clips[band.FirstIndex];
            if (!first.Card.Enabled)
                continue;
            var hold = first.Card.HoldSeconds;
            blocks.Add(new CutTextBlock(
                Id: CardId(first.Scene),
                Kind: CutTextKind.SceneCard,
                Text: CutCard.DisplayText(first.Card.Text, first.Scene),
                StartSec: band.StartSec,
                Seconds: hold,
                StartPx: band.StartSec * px,
                WidthPx: hold * px,
                Scene: first.Scene,
                CardClip: first,
                Title: null));
        }

        foreach (var title in titles)
        {
            var start = Math.Max(0, title.StartSec);
            var hold = title.HoldSeconds;
            blocks.Add(new CutTextBlock(
                Id: title.Id,
                Kind: CutTextKind.Title,
                Text: title.DisplayText,
                StartSec: start,
                Seconds: hold,
                StartPx: start * px,
                WidthPx: hold * px,
                Scene: null,
                CardClip: null,
                Title: title));
        }

        return blocks
            .OrderBy(b => b.StartSec)
            .ThenBy(b => b.Kind == CutTextKind.SceneCard ? 0 : 1)
            .ToList();
    }

    public static string CardId(int scene) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"card:{scene}");

    public static CutTextClip Add(IList<CutTextClip> titles, double startSec, string? text = null)
    {
        var title = new CutTextClip
        {
            Id = CutTextClip.NewId(),
            Text = string.IsNullOrWhiteSpace(text) ? "Title" : text.Trim(),
            StartSec = Math.Max(0, startSec),
            Seconds = CutCard.DefaultHoldSeconds,
        };
        titles.Add(title);
        return title;
    }

    public static void SetLabel(CutTextBlock block, string? text)
    {
        var value = (text ?? "").Trim();
        if (block.CardClip is { } clip)
            clip.Card.Text = value.Length > 0 ? value : CutCard.DisplayText(null, clip.Scene);
        else if (block.Title is { } title)
            title.Text = value.Length > 0 ? value : "Title";
    }

    public static void SetHold(CutTextBlock block, double seconds)
    {
        var hold = CutCard.ResolveHold(seconds);
        if (block.CardClip is { } clip)
            clip.Card.Seconds = hold;
        else if (block.Title is { } title)
            title.Seconds = hold;
    }

    public static void SetStart(CutTextBlock block, double startSec)
    {
        if (block.Title is { } title)
            title.StartSec = Math.Max(0, startSec);
    }

    /// <summary>
    /// Backspace/Delete remove the selected text clip only when no
    /// label field is focused. While an input is focused, the key
    /// edits one character — it must not wipe the clip.
    /// </summary>
    public static bool RemovesSelectedTextOnKey(string? key, bool textFieldFocused) =>
        !textFieldFocused && key is "Delete" or "Backspace";

    public static bool TryDeleteSelectedOnKey(
        string? key,
        bool textFieldFocused,
        string? selectedId,
        IReadOnlyList<CutTextBlock> blocks,
        IList<CutTextClip> titles)
    {
        if (!RemovesSelectedTextOnKey(key, textFieldFocused) || string.IsNullOrEmpty(selectedId))
            return false;
        foreach (var block in blocks)
        {
            if (block.Id != selectedId)
                continue;
            Delete(block, titles);
            return true;
        }

        return false;
    }

    public static void Delete(CutTextBlock block, IList<CutTextClip> titles)
    {
        if (block.CardClip is { } clip)
        {
            clip.Card.Enabled = false;
            return;
        }

        if (block.Title is { } title)
        {
            for (var i = titles.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(titles[i], title) || titles[i].Id == title.Id)
                    titles.RemoveAt(i);
            }
        }
    }

    /// <summary>Map free titles onto clip-local times for compose overlay.</summary>
    public static IReadOnlyList<CutTextOverlay> OverlaysForCompose(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip> titles)
    {
        if (clips.Count == 0 || titles.Count == 0)
            return [];
        var layout = CutTimelineLayout.Build(clips, CutTimelineLayout.DefaultPxPerSec);
        var result = new List<CutTextOverlay>(titles.Count);
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title.Text) && string.IsNullOrWhiteSpace(title.DisplayText))
                continue;
            var idx = 0;
            for (var i = 0; i < layout.Lanes.Count; i++)
            {
                var lane = layout.Lanes[i];
                if (title.StartSec < lane.StartSec + lane.WidthSec - 0.0001 || i == layout.Lanes.Count - 1)
                {
                    idx = i;
                    break;
                }
            }

            var local = Math.Max(0, title.StartSec - layout.Lanes[idx].StartSec);
            result.Add(new CutTextOverlay(idx, local, title.DisplayText, title.HoldSeconds));
        }

        return result;
    }
}

public readonly record struct CutTextBlock(
    string Id,
    CutTextKind Kind,
    string Text,
    double StartSec,
    double Seconds,
    double StartPx,
    double WidthPx,
    int? Scene,
    CutClip? CardClip,
    CutTextClip? Title);

public readonly record struct CutTextOverlay(
    int ClipIndex,
    double LocalStart,
    string Text,
    double Seconds);
