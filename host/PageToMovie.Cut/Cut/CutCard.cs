namespace PageToMovie.Cut.Cut;

public sealed class CutCard
{
    public const double DefaultHoldSeconds = 2;
    public const double MinHoldSeconds = 0.3;
    public const double MaxHoldSeconds = 30;

    public bool Enabled { get; set; }
    public string Text { get; set; } = "";
    public double Seconds { get; set; } = DefaultHoldSeconds;
    public CutTextStyle Style { get; } = new();

    public double HoldSeconds => ResolveHold(Seconds);

    public static double ResolveHold(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < MinHoldSeconds)
            return DefaultHoldSeconds;
        return Math.Clamp(seconds, MinHoldSeconds, MaxHoldSeconds);
    }

    public static string DisplayText(string? text, int scene)
    {
        var t = (text ?? "").Trim();
        return t.Length > 0 ? t : $"Scene {scene}";
    }
}
