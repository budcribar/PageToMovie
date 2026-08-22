namespace PageToMovie.Cut.Cut;

public sealed class CutTake
{
    public required int Take { get; init; }
    public required string FileName { get; init; }
    public required string RelativePath { get; init; }
    public long SizeBytes { get; init; }
    public string? PreviewUrl { get; set; }
    public bool Missing { get; set; }
    public string? MissingReason { get; set; }
    public double DurationSec { get; private set; }
    public double MarkIn { get; private set; }
    public double MarkOut { get; private set; }

    public bool HasDuration => DurationSec > 0;

    public string Label => $"Take {Take}";

    public void SetDuration(double seconds)
    {
        DurationSec = seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)
            ? seconds
            : 0;
        if (DurationSec <= 0)
        {
            MarkIn = 0;
            MarkOut = 0;
            return;
        }

        if (MarkOut <= 0)
            ApplyInOut(0, DurationSec);
        else
            ApplyInOut(MarkIn, MarkOut);
    }

    public void ApplyInOut(double markIn, double markOut)
    {
        var (a, b) = ClipInOut.Clamp(markIn, markOut, DurationSec);
        MarkIn = a;
        MarkOut = b;
    }
}
