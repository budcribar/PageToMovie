using System.ComponentModel;
using System.Text.Json.Serialization;

namespace PageToMovie.Fountain;

/// <summary>
/// Screenplay element / beat type parsed from Fountain formatting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainBeatType
{
    Action = 0,
    Dialogue = 1,
    Parenthetical = 2,
    Transition = 3,
    Note = 4,
    Centered = 5,
    Sound = 6
}

/// <summary>
/// Environment indicator on a scene heading (INT. / EXT. / INT./EXT.).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainHeadingEnvironment
{
    [Description("INT.")]
    INT = 0,
    [Description("EXT.")]
    EXT = 1,
    [Description("INT./EXT.")]
    INT_EXT = 2
}

/// <summary>
/// Time of day indicator on a scene heading.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainTimeOfDay
{
    [Description("DAY")]
    DAY = 0,
    [Description("NIGHT")]
    NIGHT = 1,
    [Description("CONTINUOUS")]
    CONTINUOUS = 2,
    [Description("MOMENTS LATER")]
    MOMENTS_LATER = 3,
    [Description("DAWN")]
    DAWN = 4,
    [Description("DUSK")]
    DUSK = 5
}

/// <summary>
/// Character speaker extension tag on dialogue headings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainSpeakerExtension
{
    [Description("")]
    None = 0,
    [Description("V.O.")]
    VO = 1,
    [Description("O.S.")]
    OS = 2,
    [Description("CONT'D")]
    CONTD = 3
}

/// <summary>
/// Standard Fountain transition presets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainTransitionPreset
{
    [Description("CUT TO:")]
    CutTo = 0,
    [Description("FADE IN:")]
    FadeIn = 1,
    [Description("FADE OUT.")]
    FadeOut = 2,
    [Description("DISSOLVE TO:")]
    DissolveTo = 3,
    [Description("SMASH CUT TO:")]
    SmashCutTo = 4,
    [Description("BLACKOUT")]
    Blackout = 5
}

/// <summary>
/// Page format used by the Fountain exporter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainExporterPageFormat
{
    Letter = 0,
    A4 = 1
}

/// <summary>
/// Font used by the Fountain exporter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainExporterFont
{
    CourierPrime = 0,
    CourierFinalDraft = 1,
    Courier = 2
}

/// <summary>
/// Extension methods for Fountain layer enums.
/// </summary>
public static class FountainLayerEnumExtensions
{
    public static string ToHeadingPrefix(this FountainHeadingEnvironment env) => env switch
    {
        FountainHeadingEnvironment.INT => "INT.",
        FountainHeadingEnvironment.EXT => "EXT.",
        FountainHeadingEnvironment.INT_EXT => "INT./EXT.",
        _ => "INT."
    };

    public static FountainHeadingEnvironment ParseEnvironment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FountainHeadingEnvironment.INT;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.StartsWith("INT./EXT") || upper.StartsWith("I/E") || upper.StartsWith("INT/EXT")) return FountainHeadingEnvironment.INT_EXT;
        if (upper.StartsWith("EXT")) return FountainHeadingEnvironment.EXT;
        return FountainHeadingEnvironment.INT;
    }

    public static string ToDisplayString(this FountainTimeOfDay time) => time switch
    {
        FountainTimeOfDay.DAY => "DAY",
        FountainTimeOfDay.NIGHT => "NIGHT",
        FountainTimeOfDay.CONTINUOUS => "CONTINUOUS",
        FountainTimeOfDay.MOMENTS_LATER => "MOMENTS LATER",
        FountainTimeOfDay.DAWN => "DAWN",
        FountainTimeOfDay.DUSK => "DUSK",
        _ => "DAY"
    };

    public static FountainTimeOfDay ParseTimeOfDay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FountainTimeOfDay.DAY;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Contains("NIGHT")) return FountainTimeOfDay.NIGHT;
        if (upper.Contains("MOMENT")) return FountainTimeOfDay.MOMENTS_LATER;
        if (upper.Contains("CONTINUOUS")) return FountainTimeOfDay.CONTINUOUS;
        if (upper.Contains("DAWN")) return FountainTimeOfDay.DAWN;
        if (upper.Contains("DUSK")) return FountainTimeOfDay.DUSK;
        return FountainTimeOfDay.DAY;
    }

    public static string ToDisplayString(this FountainSpeakerExtension ext) => ext switch
    {
        FountainSpeakerExtension.VO => "V.O.",
        FountainSpeakerExtension.OS => "O.S.",
        FountainSpeakerExtension.CONTD => "CONT'D",
        _ => ""
    };

    public static FountainSpeakerExtension ParseSpeakerExtension(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FountainSpeakerExtension.None;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Contains("V.O")) return FountainSpeakerExtension.VO;
        if (upper.Contains("O.S")) return FountainSpeakerExtension.OS;
        if (upper.Contains("CONT")) return FountainSpeakerExtension.CONTD;
        return FountainSpeakerExtension.None;
    }

    public static string ToDisplayString(this FountainTransitionPreset preset) => preset switch
    {
        FountainTransitionPreset.CutTo => "CUT TO:",
        FountainTransitionPreset.FadeIn => "FADE IN:",
        FountainTransitionPreset.FadeOut => "FADE OUT.",
        FountainTransitionPreset.DissolveTo => "DISSOLVE TO:",
        FountainTransitionPreset.SmashCutTo => "SMASH CUT TO:",
        FountainTransitionPreset.Blackout => "BLACKOUT",
        _ => "CUT TO:"
    };
}
