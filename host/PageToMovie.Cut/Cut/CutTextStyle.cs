namespace PageToMovie.Cut.Cut;

public enum CutTextPosition
{
    Center,
    LowerThird,
    Top,
}

public enum CutTextSize
{
    S,
    M,
    L,
}

public enum CutTextColor
{
    White,
    Yellow,
    Black,
}

public enum CutTextBackground
{
    None,
    DarkBar,
}

public enum CutTextFade
{
    None,
    Short,
}

/// <summary>
/// Clipchamp-like title/card look. Defaults stay centered white, no bar,
/// no fade — Mary19 cards match until an option changes.
/// </summary>
public sealed class CutTextStyle
{
    public const int DefaultFontPx = 48;
    public const int CenterY = 360;
    public const string DefaultColorHex = "#ffffff";
    public const double ShortFadeSeconds = 0.3;

    public CutTextPosition Position { get; set; } = CutTextPosition.Center;
    public CutTextSize Size { get; set; } = CutTextSize.M;
    public CutTextColor Color { get; set; } = CutTextColor.White;
    public CutTextBackground Background { get; set; } = CutTextBackground.None;
    public CutTextFade Fade { get; set; } = CutTextFade.None;

    public bool IsDefault =>
        Position == CutTextPosition.Center
        && Size == CutTextSize.M
        && Color == CutTextColor.White
        && Background == CutTextBackground.None
        && Fade == CutTextFade.None;

    public int FontPx => FontPxOf(Size);
    public string ColorHex => ColorHexOf(Color);
    public int Y => YOf(Position);
    public bool HasBar => Background == CutTextBackground.DarkBar;

    public static int FontPxOf(CutTextSize size) => size switch
    {
        CutTextSize.S => 32,
        CutTextSize.L => 72,
        _ => DefaultFontPx,
    };

    public static string ColorHexOf(CutTextColor color) => color switch
    {
        CutTextColor.Yellow => "#f5d76e",
        CutTextColor.Black => "#111111",
        _ => DefaultColorHex,
    };

    public static int YOf(CutTextPosition position) => position switch
    {
        CutTextPosition.Top => 120,
        CutTextPosition.LowerThird => 600,
        _ => CenterY,
    };

    public static double FadeSeconds(CutTextFade fade, double holdSeconds)
    {
        if (fade != CutTextFade.Short)
            return 0;
        var hold = holdSeconds > 0 && !double.IsNaN(holdSeconds) && !double.IsInfinity(holdSeconds)
            ? holdSeconds
            : CutCard.DefaultHoldSeconds;
        return Math.Min(ShortFadeSeconds, Math.Max(0.1, hold / 3));
    }

    public double FadeSec(double holdSeconds) => FadeSeconds(Fade, holdSeconds);

    public static string WirePosition(CutTextPosition value) => value switch
    {
        CutTextPosition.LowerThird => "lowerThird",
        CutTextPosition.Top => "top",
        _ => "center",
    };

    public static string WireSize(CutTextSize value) => value switch
    {
        CutTextSize.S => "s",
        CutTextSize.L => "l",
        _ => "m",
    };

    public static string WireColor(CutTextColor value) => value switch
    {
        CutTextColor.Yellow => "yellow",
        CutTextColor.Black => "black",
        _ => "white",
    };

    public static string WireBackground(CutTextBackground value) =>
        value == CutTextBackground.DarkBar ? "bar" : "none";

    public static string WireFade(CutTextFade value) =>
        value == CutTextFade.Short ? "short" : "none";

    public static CutTextPosition ParsePosition(string? wire) =>
        (wire ?? "").Trim().ToLowerInvariant() switch
        {
            "lower" or "lowerthird" or "lower-third" => CutTextPosition.LowerThird,
            "top" => CutTextPosition.Top,
            _ => CutTextPosition.Center,
        };

    public static CutTextSize ParseSize(string? wire) =>
        (wire ?? "").Trim().ToLowerInvariant() switch
        {
            "s" or "small" => CutTextSize.S,
            "l" or "large" => CutTextSize.L,
            _ => CutTextSize.M,
        };

    public static CutTextColor ParseColor(string? wire) =>
        (wire ?? "").Trim().ToLowerInvariant() switch
        {
            "yellow" => CutTextColor.Yellow,
            "black" => CutTextColor.Black,
            _ => CutTextColor.White,
        };

    public static CutTextBackground ParseBackground(string? wire) =>
        (wire ?? "").Trim().ToLowerInvariant() switch
        {
            "bar" or "dark" or "darkbar" => CutTextBackground.DarkBar,
            _ => CutTextBackground.None,
        };

    public static CutTextFade ParseFade(string? wire) =>
        (wire ?? "").Trim().ToLowerInvariant() switch
        {
            "short" or "fade" or "in-out" or "inout" => CutTextFade.Short,
            _ => CutTextFade.None,
        };

    public void CopyFrom(CutTextStyle? other)
    {
        if (other is null)
            return;
        Position = other.Position;
        Size = other.Size;
        Color = other.Color;
        Background = other.Background;
        Fade = other.Fade;
    }

    public static readonly (CutTextPosition Value, string Label)[] PositionChoices =
    [
        (CutTextPosition.Center, "Center"),
        (CutTextPosition.LowerThird, "Lower"),
        (CutTextPosition.Top, "Top"),
    ];

    public static readonly (CutTextSize Value, string Label)[] SizeChoices =
    [
        (CutTextSize.S, "S"),
        (CutTextSize.M, "M"),
        (CutTextSize.L, "L"),
    ];

    public static readonly (CutTextColor Value, string Label)[] ColorChoices =
    [
        (CutTextColor.White, "White"),
        (CutTextColor.Yellow, "Yellow"),
        (CutTextColor.Black, "Black"),
    ];

    public static readonly (CutTextBackground Value, string Label)[] BackgroundChoices =
    [
        (CutTextBackground.None, "None"),
        (CutTextBackground.DarkBar, "Bar"),
    ];

    public static readonly (CutTextFade Value, string Label)[] FadeChoices =
    [
        (CutTextFade.None, "None"),
        (CutTextFade.Short, "Fade"),
    ];
}
