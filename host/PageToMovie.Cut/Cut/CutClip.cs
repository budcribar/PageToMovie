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

    /// <summary>
    /// No playable current-take picture. Compose must hold this slot — never
    /// reuse the previous scene's frames.
    /// </summary>
    public bool Missing =>
        SelectedTakeNumber <= 0
        || SelectedTake is null
        || SelectedTake.Missing
        || string.IsNullOrWhiteSpace(SelectedTake.PreviewUrl);

    public bool HoldsPicture =>
        SelectedTakeNumber <= 0
        || SelectedTake is null
        || SelectedTake.Missing;

    public string? MissingReason
    {
        get
        {
            if (SelectedTakeNumber <= 0)
                return $"{Label}: no current take. Add {CutClipNaming.CurrentTakePointerFileName(Scene, Clip)}.";
            if (SelectedTake is null)
                return $"{Label}: current take {SelectedTakeNumber} is missing.";
            return SelectedTake.MissingReason;
        }
    }

    private double _holdDurationSec;
    private double _holdMarkIn;
    private double _holdMarkOut;

    public double DurationSec => SelectedTake?.DurationSec ?? _holdDurationSec;
    public double MarkIn => SelectedTake?.MarkIn ?? _holdMarkIn;
    public double MarkOut => SelectedTake?.MarkOut ?? _holdMarkOut;
    public bool HasDuration => DurationSec > 0;
    public IReadOnlyList<string> Filmstrip => SelectedTake?.Filmstrip ?? [];
    public double SlicedDurationSec => CutTimelineLayout.SlicedSeconds(this);

    public List<CutRangeSpan> RangeDeletes { get; } = [];
    public CutJoinKind? JoinOverride { get; set; }
    public string? FountainTransition { get; set; }
    public CutCard Card { get; } = new();

    public IReadOnlyList<(double Start, double End)> KeepWindows() =>
        CutRangeDelete.KeepWindows(MarkIn, MarkOut, RangeDeletes.Select(r => (r.Start, r.End)));

    public CutJoinKind JoinToNext(CutClip? next) =>
        CutTransitionMap.Resolve(FountainTransition, next is not null && next.Scene != Scene, JoinOverride);

    public bool IsFirstOfScene(IReadOnlyList<CutClip> strip)
    {
        for (var i = 0; i < strip.Count; i++)
        {
            if (!ReferenceEquals(strip[i], this))
                continue;
            return i == 0 || strip[i - 1].Scene != Scene;
        }

        return true;
    }

    public bool IsLastOfScene(IReadOnlyList<CutClip> strip)
    {
        for (var i = 0; i < strip.Count; i++)
        {
            if (!ReferenceEquals(strip[i], this))
                continue;
            return i == strip.Count - 1 || strip[i + 1].Scene != Scene;
        }

        return true;
    }

    public bool NeedsTrim =>
        SelectedTake is { HasDuration: true } t
        && (t.MarkIn > 0.05 || t.MarkOut < t.DurationSec - 0.05);

    public void SeedSelection()
    {
        // Current is .current.json, or a same-slot recover — never another scene.
        SelectedTakeNumber = ActiveTakeNumber > 0 ? ActiveTakeNumber : 0;
    }

    public void SetDuration(double seconds)
    {
        var before = MarkOut <= MarkIn;
        if (SelectedTake is { } take)
        {
            take.SetDuration(seconds);
            if (before && MarkOut > MarkIn)
                MarksRepaired = true;
            return;
        }

        _holdDurationSec = seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)
            ? seconds
            : 0;
        if (_holdDurationSec <= 0)
        {
            _holdMarkIn = 0;
            _holdMarkOut = 0;
            return;
        }

        if (_holdMarkOut <= _holdMarkIn)
            ApplyHoldInOut(0, _holdDurationSec);
        else
            ApplyHoldInOut(_holdMarkIn, _holdMarkOut);
        if (before && MarkOut > MarkIn)
            MarksRepaired = true;
    }

    public void ApplyInOut(double markIn, double markOut)
    {
        if (SelectedTake is { } take)
        {
            take.ApplyInOut(markIn, markOut);
            return;
        }

        ApplyHoldInOut(markIn, markOut);
    }

    /// <summary>
    /// <c>markOut &lt;= markIn</c> must not stay at 0 when the take or sidecar
    /// has a duration. Persisted by the next <c>cut.project.json</c> save.
    /// </summary>
    public bool MarksRepaired { get; private set; }

    public bool EnsureInOutFromDuration()
    {
        if (DurationSec <= 0 || MarkOut > MarkIn)
            return false;
        ApplyInOut(MarkIn > 0 ? MarkIn : 0, DurationSec);
        if (MarkOut <= MarkIn)
            return false;
        MarksRepaired = true;
        return true;
    }

    private void ApplyHoldInOut(double markIn, double markOut)
    {
        if (_holdDurationSec <= 0)
        {
            _holdMarkIn = markIn;
            _holdMarkOut = markOut;
            return;
        }

        var (a, b) = ClipInOut.Clamp(markIn, markOut, _holdDurationSec);
        _holdMarkIn = a;
        _holdMarkOut = b;
    }
}
