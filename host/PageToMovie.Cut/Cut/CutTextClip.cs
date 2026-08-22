namespace PageToMovie.Cut.Cut;

/// <summary>A free title on the single text row (not a scene-boundary card).</summary>
public sealed class CutTextClip
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public double StartSec { get; set; }
    public double Seconds { get; set; } = CutCard.DefaultHoldSeconds;

    public double HoldSeconds => CutCard.ResolveHold(Seconds);

    public string DisplayText
    {
        get
        {
            var t = (Text ?? "").Trim();
            return t.Length > 0 ? t : "Title";
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
