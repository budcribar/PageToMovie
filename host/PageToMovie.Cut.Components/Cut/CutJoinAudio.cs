namespace PageToMovie.Cut.Cut;

/// <summary>
/// Audio offset on a join — not a visual wipe.
/// J-cut: incoming sound starts before the picture cut.
/// L-cut: outgoing sound hangs after the picture cut.
/// </summary>
public readonly record struct CutJoinAudio(CutJoinAudioKind Kind, double Seconds)
{
    public const double DefaultSeconds = 0.75;
    public const double MinSeconds = 0.25;
    public const double MaxSeconds = 4;
    public const double StepSeconds = 0.25;

    public static CutJoinAudio None { get; } = new(CutJoinAudioKind.None, 0);

    public static CutJoinAudio JCut(double seconds = DefaultSeconds) =>
        new(CutJoinAudioKind.JCut, seconds);

    public static CutJoinAudio LCut(double seconds = DefaultSeconds) =>
        new(CutJoinAudioKind.LCut, seconds);

    public bool IsActive => Kind != CutJoinAudioKind.None && Seconds > 0.05;

    public double ResolvedSeconds =>
        Kind == CutJoinAudioKind.None
            ? 0
            : (Seconds > 0.05 ? Seconds : DefaultSeconds);

    public static double ClampSeconds(double requested, double leftSec, double rightSec)
    {
        var shorter = Math.Min(Positive(leftSec), Positive(rightSec));
        var max = Math.Min(MaxSeconds, shorter);
        if (max < MinSeconds)
            return 0;
        var value = requested > 0.05 ? requested : DefaultSeconds;
        var stepped = Math.Round(value / StepSeconds) * StepSeconds;
        return Math.Clamp(stepped, MinSeconds, max);
    }

    public CutJoinAudio Clamped(double leftSec, double rightSec)
    {
        if (Kind == CutJoinAudioKind.None)
            return None;
        var sec = ClampSeconds(ResolvedSeconds, leftSec, rightSec);
        return sec < MinSeconds ? None : new CutJoinAudio(Kind, sec);
    }

    public static string WireName(CutJoinAudioKind kind) => kind switch
    {
        CutJoinAudioKind.JCut => "jcut",
        CutJoinAudioKind.LCut => "lcut",
        _ => "",
    };

    public static CutJoinAudioKind ParseKind(string? wire) =>
        (wire ?? "").Trim().ToLowerInvariant() switch
        {
            "jcut" or "j-cut" or "j" => CutJoinAudioKind.JCut,
            "lcut" or "l-cut" or "l" => CutJoinAudioKind.LCut,
            _ => CutJoinAudioKind.None,
        };

    public static string TickTag(CutJoinAudioKind kind) => kind switch
    {
        CutJoinAudioKind.JCut => "J-cut",
        CutJoinAudioKind.LCut => "L-cut",
        _ => "",
    };

    private static double Positive(double value) =>
        value > 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 0;
}

public enum CutJoinAudioKind
{
    None = 0,
    JCut = 1,
    LCut = 2,
}
