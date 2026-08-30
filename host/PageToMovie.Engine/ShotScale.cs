namespace PageToMovie.Engine;

public enum ShotScale
{
    Wide,
    Medium,
    CloseUp,
    ExtremeCloseUp
}

public static class ShotScaleExtensions
{
    public static string ToSnakeCase(this ShotScale scale) => scale switch
    {
        ShotScale.Wide => "wide",
        ShotScale.Medium => "medium",
        ShotScale.CloseUp => "close_up",
        ShotScale.ExtremeCloseUp => "extreme_close_up",
        _ => "medium"
    };

    /// <summary>Prompt wording for the scale, used when a Camera tag is composed from fields.</summary>
    public static string ToFramingPhrase(this ShotScale scale) => scale switch
    {
        ShotScale.Wide => "Wide shot",
        ShotScale.CloseUp => "Close-up",
        ShotScale.ExtremeCloseUp => "Extreme close-up",
        _ => "Medium shot"
    };

    public static ShotScale ParseShotScale(string? raw, ShotScale fallback = ShotScale.Medium)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var s = raw.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return s switch
        {
            "wide" or "wide_shot" or "ws" => ShotScale.Wide,
            "medium" or "medium_shot" or "ms" or "medium_close" or "mcu" => ShotScale.Medium,
            "close_up" or "closeup" or "close" or "cu" => ShotScale.CloseUp,
            "extreme_close_up" or "extreme_closeup" or "ecu" => ShotScale.ExtremeCloseUp,
            _ => fallback
        };
    }
}
