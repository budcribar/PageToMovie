namespace PageToMovie.Cut.Cut;

/// <summary>A free title on the single text row (not a scene-boundary card).</summary>
public sealed class CutTextClip
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public double StartSec { get; set; }
    public double Seconds { get; set; } = CutCard.DefaultHoldSeconds;
    public CutTextStyle Style { get; } = new();

    public double HoldSeconds => CutCard.ResolveHold(Seconds);

    public string DisplayText
    {
        get
        {
            var t = (Text ?? "").Trim();
            return t.Length > 0 ? t : "Title";
        }
    }

    public void Move(double startSec)
    {
        if (double.IsNaN(startSec) || double.IsInfinity(startSec) || startSec < 0)
            startSec = 0;
        StartSec = startSec;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
