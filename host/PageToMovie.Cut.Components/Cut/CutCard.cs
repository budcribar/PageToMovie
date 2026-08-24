namespace PageToMovie.Cut.Cut;

public sealed class CutCard
{
    public const double DefaultHoldSeconds = 2;
    public const double MinHoldSeconds = 0.3;

    public bool Enabled { get; set; }
    public string Text { get; set; } = "";
    public double Seconds { get; set; } = DefaultHoldSeconds;
    public CutTextStyle Style { get; } = new();

    public double HoldSeconds => ResolveHold(Seconds);

    /// <summary>
    /// Titles stretch along the timeline. No 30s product cap — pass
    /// <paramref name="maxSeconds"/> (movie length from the start) when
    /// trimming; omit it to keep any finite hold.
    /// </summary>
    public static double ResolveHold(double seconds, double maxSeconds = double.PositiveInfinity)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < MinHoldSeconds)
            return DefaultHoldSeconds;
        var cap = double.IsNaN(maxSeconds) || double.IsInfinity(maxSeconds) || maxSeconds < MinHoldSeconds
            ? seconds
            : maxSeconds;
        return Math.Clamp(seconds, MinHoldSeconds, Math.Max(MinHoldSeconds, cap));
    }

    public static string DisplayText(string? text, int scene)
    {
        var t = (text ?? "").Trim();
        return t.Length > 0 ? t : $"Scene {scene}";
    }
}
