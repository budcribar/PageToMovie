namespace PageToMovie.Engine;

public enum AspectRatio
{
    Ratio16x9,
    Ratio9x16,
    Ratio1x1,
    Ratio4x3,
    Ratio21x9
}

public enum CameraLens
{
    Lens24mm,
    Lens35mm,
    Lens50mm,
    Lens85mm
}

public enum CameraMovementKind
{
    DollyPush,
    TripodHold,
    PanLeft,
    PanRight,
    TiltUp
}

public enum PacingMood
{
    Slow,
    Moderate,
    Fast,
    Frenetic
}

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public static class MediaEngineEnumExtensions
{
    public static AspectRatio ParseAspectRatio(string? s, AspectRatio defaultValue = AspectRatio.Ratio1x1)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        return s.Trim() switch
        {
            "16:9" => AspectRatio.Ratio16x9,
            "9:16" => AspectRatio.Ratio9x16,
            "1:1" => AspectRatio.Ratio1x1,
            "4:3" => AspectRatio.Ratio4x3,
            "21:9" => AspectRatio.Ratio21x9,
            _ => Enum.TryParse<AspectRatio>(s, true, out var r) ? r : defaultValue
        };
    }

    public static CameraLens ParseCameraLens(string? s, CameraLens defaultValue = CameraLens.Lens35mm)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        if (s.Contains("24mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens24mm;
        if (s.Contains("35mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens35mm;
        if (s.Contains("50mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens50mm;
        if (s.Contains("85mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens85mm;
        return Enum.TryParse<CameraLens>(s, true, out var l) ? l : defaultValue;
    }

    public static CameraMovementKind ParseCameraMovementKind(string? s, CameraMovementKind defaultValue = CameraMovementKind.TripodHold)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var lower = s.ToLowerInvariant();
        if (lower.Contains("dolly") || lower.Contains("push")) return CameraMovementKind.DollyPush;
        if (lower.Contains("pan left") || lower.Contains("pan_left")) return CameraMovementKind.PanLeft;
        if (lower.Contains("pan right") || lower.Contains("pan_right")) return CameraMovementKind.PanRight;
        if (lower.Contains("tilt") || lower.Contains("tilt_up")) return CameraMovementKind.TiltUp;
        if (lower.Contains("tripod") || lower.Contains("hold") || lower.Contains("static")) return CameraMovementKind.TripodHold;
        return Enum.TryParse<CameraMovementKind>(s, true, out var m) ? m : defaultValue;
    }

    public static string ToApiString(this AspectRatio ratio) => ratio switch
    {
        AspectRatio.Ratio16x9 => "16:9",
        AspectRatio.Ratio9x16 => "9:16",
        AspectRatio.Ratio1x1 => "1:1",
        AspectRatio.Ratio4x3 => "4:3",
        AspectRatio.Ratio21x9 => "21:9",
        _ => "1:1"
    };
}
