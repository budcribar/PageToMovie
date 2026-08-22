namespace PageToMovie.Cut.Cut;

public sealed class CutClip
{
    public required int Scene { get; init; }
    public required int Clip { get; init; }
    public List<CutTake> Takes { get; } = [];
    public string PointerRelativePath { get; set; } = "";

    /// <summary>Take number from <c>.current.json</c> (0 if none).</summary>
    public int ActiveTakeNumber { get; set; }

    /// <summary>In-memory current take for preview / in-out / export.</summary>
    public int SelectedTakeNumber { get; private set; }

    public CutTake? SelectedTake =>
        SelectedTakeNumber > 0
            ? Takes.FirstOrDefault(t => t.Take == SelectedTakeNumber)
            : null;

    public string Label => $"S{Scene:D2} C{Clip:D2}";
    public string FileName => SelectedTake?.FileName ?? "";
    public string RelativePath => SelectedTake?.RelativePath ?? "";
    public string? PreviewUrl => SelectedTake?.PreviewUrl;
    public bool Missing =>
        SelectedTakeNumber <= 0
        || SelectedTake is null
        || SelectedTake.Missing
        || string.IsNullOrWhiteSpace(SelectedTake.PreviewUrl);

    public string? MissingReason
    {
        get
        {
            if (SelectedTakeNumber <= 0)
                return $"{Label}: no current take. Add {CutClipNaming.CurrentTakePointerFileName(Scene, Clip)} or pick a take.";
            if (SelectedTake is null)
                return $"{Label}: current take {SelectedTakeNumber} is missing.";
            return SelectedTake.MissingReason;
        }
    }

    public double DurationSec => SelectedTake?.DurationSec ?? 0;
    public double MarkIn => SelectedTake?.MarkIn ?? 0;
    public double MarkOut => SelectedTake?.MarkOut ?? 0;
    public bool HasDuration => SelectedTake?.HasDuration ?? false;

    public bool NeedsTrim =>
        SelectedTake is { HasDuration: true } t
        && (t.MarkIn > 0.05 || t.MarkOut < t.DurationSec - 0.05);

    public void SeedSelection()
    {
        // Current is .current.json only — do not fall back to another take.
        SelectedTakeNumber = ActiveTakeNumber > 0 ? ActiveTakeNumber : 0;
    }

    public void SelectTake(int take)
    {
        if (!Takes.Any(t => t.Take == take))
            return;
        SelectedTakeNumber = take;
        ActiveTakeNumber = take;
    }

    public void SetDuration(double seconds) => SelectedTake?.SetDuration(seconds);

    public void ApplyInOut(double markIn, double markOut) => SelectedTake?.ApplyInOut(markIn, markOut);
}
