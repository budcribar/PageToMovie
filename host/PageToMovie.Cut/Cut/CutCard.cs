namespace PageToMovie.Cut.Cut;

public sealed class CutCard
{
    public const double DefaultHoldSeconds = 2;

    public bool Enabled { get; set; }
    public string Text { get; set; } = "";
    public double Seconds { get; set; } = DefaultHoldSeconds;

    public double HoldSeconds =>
        Seconds > 0.2 && !double.IsNaN(Seconds) && !double.IsInfinity(Seconds)
            ? Seconds
            : DefaultHoldSeconds;
}
