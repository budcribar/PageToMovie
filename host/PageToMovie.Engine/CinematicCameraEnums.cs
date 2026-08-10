using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine;

#region Enums

/// <summary>
/// Extended environmental weather conditions for cinematic prompting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentWeatherKind
{
    Clear,
    Sunny,
    Overcast,
    Rainy,
    Stormy,
    Snowy,
    Foggy,
    Windy,
    Hazy,
    Blizzard,
    Duststorm,
    TropicalMonsoon
}

/// <summary>
/// Extended atmospheric lighting styles for visual generation setup.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AtmosphereLightingStyle
{
    NaturalDaylight,
    GoldenHour,
    BlueHour,
    Night,
    Dramatic,
    Neon,
    Soft,
    HarshDirect,
    Backlit,
    Volumetric,
    Candlelight,
    HighKey,
    LowKey
}

/// <summary>
/// Extended color grading tone presets for cinematic post-processing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColorGradingTonePreset
{
    Warm,
    Cool,
    Neutral,
    HighContrast,
    Desaturated,
    Vintage,
    Sepia,
    TealAndOrange,
    CinematicBleachBypass,
    Cyberpunk,
    FilmNoir,
    Pastel,
    Monochromatic
}

/// <summary>
/// Extended lens focal length categories for camera framing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LensFocalLengthCategory
{
    UltraWide,
    Wide,
    Standard,
    Telephoto,
    SuperTelephoto,
    Macro,
    Anamorphic,
    Fisheye,
    PerspectiveControl
}

/// <summary>
/// Extended cinematic mood tag kinds for shot emotion tagging.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CinematicMoodTagKind
{
    Tense,
    Melancholic,
    Uplifting,
    Mysterious,
    ActionPacked,
    Romantic,
    Eerie,
    Playful,
    Suspenseful,
    Epic,
    Nostalgic,
    Somber,
    Whimsical
}

/// <summary>
/// Extended shot sequence position tags for narrative pacing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShotSequencePositionTag
{
    Establishing,
    Opening,
    Middle,
    Climax,
    Transition,
    Closing,
    Outro,
    Interlude,
    Prologue,
    Epilogue,
    Flashback,
    Montage
}

/// <summary>
/// Extended clip duration preset kinds for scene timing controls.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipDurationPresetKind
{
    Micro,
    Short,
    Standard,
    Extended,
    CinematicLong,
    Custom,
    MaxSupported
}

/// <summary>
/// Extended validation gate types for shot plan pipeline checks.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShotPlanValidationGateType
{
    Unvalidated,
    Draft,
    Passed,
    Failed,
    NeedsReview,
    AutoApproved,
    CriticalBlock,
    WarningBypass,
    ManualOverride
}

/// <summary>
/// Extended language style presets for AI visual prompt assembly.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptLanguageStylePreset
{
    NaturalLanguage,
    TaggedKeywords,
    TechnicalCinematic,
    StorybookNarrative,
    Minimalist,
    StructuredJson,
    ArtisticDescriptive
}

/// <summary>
/// Extended export target formats for shot plans and NLE timelines.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShotPlanExportTargetKind
{
    Json,
    Pdf,
    FinalCutProXml,
    PremiereProXml,
    DaVinciResolve,
    Markdown,
    AvidEdl,
    OtioTimeline,
    CsvSheet
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods and string parsers for extended cinematic camera enums.
/// </summary>
public static class CinematicCameraEnumExtensions
{
    public static string ToApiString(this EnvironmentWeatherKind val) => val switch
    {
        EnvironmentWeatherKind.TropicalMonsoon => "tropical_monsoon",
        _ => val.ToString().ToLowerInvariant()
    };
    public static EnvironmentWeatherKind ParseEnvironmentWeatherKind(string? s, EnvironmentWeatherKind defaultValue = EnvironmentWeatherKind.Clear) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<EnvironmentWeatherKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static EnvironmentWeatherKind ToEnvironmentWeatherKind(this string? s, EnvironmentWeatherKind defaultValue = EnvironmentWeatherKind.Clear) => ParseEnvironmentWeatherKind(s, defaultValue);

    public static string ToApiString(this AtmosphereLightingStyle val) => val switch
    {
        AtmosphereLightingStyle.NaturalDaylight => "natural_daylight",
        AtmosphereLightingStyle.GoldenHour => "golden_hour",
        AtmosphereLightingStyle.BlueHour => "blue_hour",
        AtmosphereLightingStyle.HarshDirect => "harsh_direct",
        AtmosphereLightingStyle.HighKey => "high_key",
        AtmosphereLightingStyle.LowKey => "low_key",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AtmosphereLightingStyle ParseAtmosphereLightingStyle(string? s, AtmosphereLightingStyle defaultValue = AtmosphereLightingStyle.NaturalDaylight) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AtmosphereLightingStyle>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AtmosphereLightingStyle ToAtmosphereLightingStyle(this string? s, AtmosphereLightingStyle defaultValue = AtmosphereLightingStyle.NaturalDaylight) => ParseAtmosphereLightingStyle(s, defaultValue);

    public static string ToApiString(this ColorGradingTonePreset val) => val switch
    {
        ColorGradingTonePreset.HighContrast => "high_contrast",
        ColorGradingTonePreset.TealAndOrange => "teal_and_orange",
        ColorGradingTonePreset.CinematicBleachBypass => "cinematic_bleach_bypass",
        ColorGradingTonePreset.FilmNoir => "film_noir",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ColorGradingTonePreset ParseColorGradingTonePreset(string? s, ColorGradingTonePreset defaultValue = ColorGradingTonePreset.Neutral) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ColorGradingTonePreset>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ColorGradingTonePreset ToColorGradingTonePreset(this string? s, ColorGradingTonePreset defaultValue = ColorGradingTonePreset.Neutral) => ParseColorGradingTonePreset(s, defaultValue);

    public static string ToApiString(this LensFocalLengthCategory val) => val switch
    {
        LensFocalLengthCategory.UltraWide => "ultra_wide",
        LensFocalLengthCategory.SuperTelephoto => "super_telephoto",
        LensFocalLengthCategory.PerspectiveControl => "perspective_control",
        _ => val.ToString().ToLowerInvariant()
    };
    public static LensFocalLengthCategory ParseLensFocalLengthCategory(string? s, LensFocalLengthCategory defaultValue = LensFocalLengthCategory.Standard) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<LensFocalLengthCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static LensFocalLengthCategory ToLensFocalLengthCategory(this string? s, LensFocalLengthCategory defaultValue = LensFocalLengthCategory.Standard) => ParseLensFocalLengthCategory(s, defaultValue);

    public static string ToApiString(this CinematicMoodTagKind val) => val switch
    {
        CinematicMoodTagKind.ActionPacked => "action_packed",
        _ => val.ToString().ToLowerInvariant()
    };
    public static CinematicMoodTagKind ParseCinematicMoodTagKind(string? s, CinematicMoodTagKind defaultValue = CinematicMoodTagKind.Uplifting) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CinematicMoodTagKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static CinematicMoodTagKind ToCinematicMoodTagKind(this string? s, CinematicMoodTagKind defaultValue = CinematicMoodTagKind.Uplifting) => ParseCinematicMoodTagKind(s, defaultValue);

    public static string ToApiString(this ShotSequencePositionTag val) => val.ToString().ToLowerInvariant();
    public static ShotSequencePositionTag ParseShotSequencePositionTag(string? s, ShotSequencePositionTag defaultValue = ShotSequencePositionTag.Middle) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ShotSequencePositionTag>(s, true, out var r) ? r : defaultValue;
    public static ShotSequencePositionTag ToShotSequencePositionTag(this string? s, ShotSequencePositionTag defaultValue = ShotSequencePositionTag.Middle) => ParseShotSequencePositionTag(s, defaultValue);

    public static string ToApiString(this ClipDurationPresetKind val) => val switch
    {
        ClipDurationPresetKind.CinematicLong => "cinematic_long",
        ClipDurationPresetKind.MaxSupported => "max_supported",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ClipDurationPresetKind ParseClipDurationPresetKind(string? s, ClipDurationPresetKind defaultValue = ClipDurationPresetKind.Standard) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ClipDurationPresetKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ClipDurationPresetKind ToClipDurationPresetKind(this string? s, ClipDurationPresetKind defaultValue = ClipDurationPresetKind.Standard) => ParseClipDurationPresetKind(s, defaultValue);

    public static string ToApiString(this ShotPlanValidationGateType val) => val switch
    {
        ShotPlanValidationGateType.NeedsReview => "needs_review",
        ShotPlanValidationGateType.AutoApproved => "auto_approved",
        ShotPlanValidationGateType.CriticalBlock => "critical_block",
        ShotPlanValidationGateType.WarningBypass => "warning_bypass",
        ShotPlanValidationGateType.ManualOverride => "manual_override",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ShotPlanValidationGateType ParseShotPlanValidationGateType(string? s, ShotPlanValidationGateType defaultValue = ShotPlanValidationGateType.Unvalidated) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ShotPlanValidationGateType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ShotPlanValidationGateType ToShotPlanValidationGateType(this string? s, ShotPlanValidationGateType defaultValue = ShotPlanValidationGateType.Unvalidated) => ParseShotPlanValidationGateType(s, defaultValue);

    public static string ToApiString(this PromptLanguageStylePreset val) => val switch
    {
        PromptLanguageStylePreset.NaturalLanguage => "natural_language",
        PromptLanguageStylePreset.TaggedKeywords => "tagged_keywords",
        PromptLanguageStylePreset.TechnicalCinematic => "technical_cinematic",
        PromptLanguageStylePreset.StorybookNarrative => "storybook_narrative",
        PromptLanguageStylePreset.StructuredJson => "structured_json",
        PromptLanguageStylePreset.ArtisticDescriptive => "artistic_descriptive",
        _ => val.ToString().ToLowerInvariant()
    };
    public static PromptLanguageStylePreset ParsePromptLanguageStylePreset(string? s, PromptLanguageStylePreset defaultValue = PromptLanguageStylePreset.NaturalLanguage) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<PromptLanguageStylePreset>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static PromptLanguageStylePreset ToPromptLanguageStylePreset(this string? s, PromptLanguageStylePreset defaultValue = PromptLanguageStylePreset.NaturalLanguage) => ParsePromptLanguageStylePreset(s, defaultValue);

    public static string ToApiString(this ShotPlanExportTargetKind val) => val switch
    {
        ShotPlanExportTargetKind.FinalCutProXml => "final_cut_pro_xml",
        ShotPlanExportTargetKind.PremiereProXml => "premiere_pro_xml",
        ShotPlanExportTargetKind.DaVinciResolve => "davinci_resolve",
        ShotPlanExportTargetKind.AvidEdl => "avid_edl",
        ShotPlanExportTargetKind.OtioTimeline => "otio_timeline",
        ShotPlanExportTargetKind.CsvSheet => "csv_sheet",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ShotPlanExportTargetKind ParseShotPlanExportTargetKind(string? s, ShotPlanExportTargetKind defaultValue = ShotPlanExportTargetKind.Json) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ShotPlanExportTargetKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ShotPlanExportTargetKind ToShotPlanExportTargetKind(this string? s, ShotPlanExportTargetKind defaultValue = ShotPlanExportTargetKind.Json) => ParseShotPlanExportTargetKind(s, defaultValue);
}

#endregion
