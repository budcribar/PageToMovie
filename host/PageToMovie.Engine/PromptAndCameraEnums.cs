using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine;

#region Enums

/// <summary>
/// Operational modes for the Stage 2 shot planner engine.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Stage2PlannerMode
{
    Standard,
    Fast,
    FineGrained,
    Cinematic,
    Experimental,
    Auto
}

/// <summary>
/// Template prompt style families for shot generation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptTemplateFamily
{
    Default,
    Cinematic,
    Photorealistic,
    Anime,
    Fantasy,
    SciFi,
    Vintage,
    Noir
}

/// <summary>
/// Visual medium rendering styles for visual generation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisualMediumStyle
{
    LiveAction,
    Animation3D,
    Animation2D,
    Claymation,
    StopMotion,
    ComicBook,
    Watercolor
}

/// <summary>
/// Character appearance lock modes across generated scene clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterVisualLockMode
{
    Strict,
    Flexible,
    ReferenceImageOnly,
    DescriptionOnly,
    Hybrid
}

/// <summary>
/// Wardrobe continuity rules applied during visual prompting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WardrobeConsistencyRule
{
    LockPerScene,
    LockPerStory,
    DynamicByEnvironment,
    StrictMatch
}

/// <summary>
/// Filters used to scrub or sanitize generated prompts before AI model submission.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptScrubbingFilter
{
    None,
    Basic,
    Strict,
    NsfwFilter,
    ContentSafety,
    BrandFilter
}

/// <summary>
/// Camera focal distance categories for shot framing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraFocusDistance
{
    Macro,
    CloseUp,
    Medium,
    Deep,
    Infinity,
    Auto
}

/// <summary>
/// Depth of field presets for cinematic shot direction.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepthOfFieldPreset
{
    Shallow,
    UltraShallow,
    Medium,
    Deep,
    Hyperfocal
}

/// <summary>
/// Artistic composition framing guidelines for shot setup.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FramingComposition
{
    RuleOfThirds,
    CenterWeighted,
    GoldenRatio,
    Symmetric,
    LeadingLines,
    Headroom
}

/// <summary>
/// Subject movement speed classifications in a video clip.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubjectMovementSpeed
{
    Static,
    Slow,
    Moderate,
    Fast,
    Rapid,
    Explosive
}

/// <summary>
/// Environmental weather settings for prompt generation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentWeather
{
    Clear,
    Sunny,
    Overcast,
    Rainy,
    Stormy,
    Snowy,
    Foggy,
    Windy
}

/// <summary>
/// Atmospheric lighting conditions for scene visuals.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AtmosphereLighting
{
    NaturalDaylight,
    GoldenHour,
    BlueHour,
    Night,
    Dramatic,
    Neon,
    Soft
}

/// <summary>
/// Color grading tones for cinematic visual style.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColorGradingTone
{
    Warm,
    Cool,
    Neutral,
    HighContrast,
    Desaturated,
    Vintage,
    Sepia,
    TealAndOrange
}

/// <summary>
/// Lens focal length classification groups.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LensFocalLengthGroup
{
    UltraWide,
    Wide,
    Standard,
    Telephoto,
    SuperTelephoto,
    Macro
}

/// <summary>
/// Cinematic mood tags assigned to shot sequences.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CinematicMoodTag
{
    Tense,
    Melancholic,
    Uplifting,
    Mysterious,
    ActionPacked,
    Romantic,
    Eerie,
    Playful
}

/// <summary>
/// Position of a shot in narrative sequence.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShotSequencePosition
{
    Establishing,
    Opening,
    Middle,
    Climax,
    Transition,
    Closing,
    Outro
}

/// <summary>
/// Standardized clip duration presets in seconds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipDurationPreset
{
    Short,
    Standard,
    Extended,
    Custom,
    MaxSupported
}

/// <summary>
/// Validation gates for checking shot plan readiness.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShotPlanValidationGate
{
    Unvalidated,
    Draft,
    Passed,
    Failed,
    NeedsReview,
    AutoApproved
}

/// <summary>
/// Language formatting styles for AI visual prompts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptLanguageStyle
{
    NaturalLanguage,
    TaggedKeywords,
    TechnicalCinematic,
    StorybookNarrative,
    Minimalist
}

/// <summary>
/// Target formats for exporting shot plans.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShotPlanExportTarget
{
    Json,
    Pdf,
    FinalCutProXml,
    PremiereProXml,
    DaVinciResolve,
    Markdown
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods and string parsers for prompt and camera enums.
/// </summary>
public static class PromptAndCameraEnumExtensions
{
    public static string ToApiString(this Stage2PlannerMode val) => val switch
    {
        Stage2PlannerMode.FineGrained => "fine_grained",
        _ => val.ToString().ToLowerInvariant()
    };
    public static Stage2PlannerMode ParseStage2PlannerMode(string? s, Stage2PlannerMode defaultValue = Stage2PlannerMode.Standard) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<Stage2PlannerMode>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static Stage2PlannerMode ToStage2PlannerMode(this string? s, Stage2PlannerMode defaultValue = Stage2PlannerMode.Standard) => ParseStage2PlannerMode(s, defaultValue);

    public static string ToApiString(this PromptTemplateFamily val) => val.ToString().ToLowerInvariant();
    public static PromptTemplateFamily ParsePromptTemplateFamily(string? s, PromptTemplateFamily defaultValue = PromptTemplateFamily.Default) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<PromptTemplateFamily>(s, true, out var r) ? r : defaultValue;
    public static PromptTemplateFamily ToPromptTemplateFamily(this string? s, PromptTemplateFamily defaultValue = PromptTemplateFamily.Default) => ParsePromptTemplateFamily(s, defaultValue);

    public static string ToApiString(this VisualMediumStyle val) => val switch
    {
        VisualMediumStyle.LiveAction => "live_action",
        VisualMediumStyle.Animation3D => "animation_3d",
        VisualMediumStyle.Animation2D => "animation_2d",
        VisualMediumStyle.StopMotion => "stop_motion",
        VisualMediumStyle.ComicBook => "comic_book",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VisualMediumStyle ParseVisualMediumStyle(string? s, VisualMediumStyle defaultValue = VisualMediumStyle.LiveAction) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VisualMediumStyle>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VisualMediumStyle ToVisualMediumStyle(this string? s, VisualMediumStyle defaultValue = VisualMediumStyle.LiveAction) => ParseVisualMediumStyle(s, defaultValue);

    public static string ToApiString(this CharacterVisualLockMode val) => val switch
    {
        CharacterVisualLockMode.ReferenceImageOnly => "reference_image_only",
        CharacterVisualLockMode.DescriptionOnly => "description_only",
        _ => val.ToString().ToLowerInvariant()
    };
    public static CharacterVisualLockMode ParseCharacterVisualLockMode(string? s, CharacterVisualLockMode defaultValue = CharacterVisualLockMode.Strict) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CharacterVisualLockMode>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static CharacterVisualLockMode ToCharacterVisualLockMode(this string? s, CharacterVisualLockMode defaultValue = CharacterVisualLockMode.Strict) => ParseCharacterVisualLockMode(s, defaultValue);

    public static string ToApiString(this WardrobeConsistencyRule val) => val switch
    {
        WardrobeConsistencyRule.LockPerScene => "lock_per_scene",
        WardrobeConsistencyRule.LockPerStory => "lock_per_story",
        WardrobeConsistencyRule.DynamicByEnvironment => "dynamic_by_environment",
        WardrobeConsistencyRule.StrictMatch => "strict_match",
        _ => val.ToString().ToLowerInvariant()
    };
    public static WardrobeConsistencyRule ParseWardrobeConsistencyRule(string? s, WardrobeConsistencyRule defaultValue = WardrobeConsistencyRule.LockPerScene) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<WardrobeConsistencyRule>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static WardrobeConsistencyRule ToWardrobeConsistencyRule(this string? s, WardrobeConsistencyRule defaultValue = WardrobeConsistencyRule.LockPerScene) => ParseWardrobeConsistencyRule(s, defaultValue);

    public static string ToApiString(this PromptScrubbingFilter val) => val switch
    {
        PromptScrubbingFilter.NsfwFilter => "nsfw_filter",
        PromptScrubbingFilter.ContentSafety => "content_safety",
        PromptScrubbingFilter.BrandFilter => "brand_filter",
        _ => val.ToString().ToLowerInvariant()
    };
    public static PromptScrubbingFilter ParsePromptScrubbingFilter(string? s, PromptScrubbingFilter defaultValue = PromptScrubbingFilter.None) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<PromptScrubbingFilter>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static PromptScrubbingFilter ToPromptScrubbingFilter(this string? s, PromptScrubbingFilter defaultValue = PromptScrubbingFilter.None) => ParsePromptScrubbingFilter(s, defaultValue);

    public static string ToApiString(this CameraFocusDistance val) => val switch
    {
        CameraFocusDistance.CloseUp => "close_up",
        _ => val.ToString().ToLowerInvariant()
    };
    public static CameraFocusDistance ParseCameraFocusDistance(string? s, CameraFocusDistance defaultValue = CameraFocusDistance.Medium) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CameraFocusDistance>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static CameraFocusDistance ToCameraFocusDistance(this string? s, CameraFocusDistance defaultValue = CameraFocusDistance.Medium) => ParseCameraFocusDistance(s, defaultValue);

    public static string ToApiString(this DepthOfFieldPreset val) => val switch
    {
        DepthOfFieldPreset.UltraShallow => "ultra_shallow",
        _ => val.ToString().ToLowerInvariant()
    };
    public static DepthOfFieldPreset ParseDepthOfFieldPreset(string? s, DepthOfFieldPreset defaultValue = DepthOfFieldPreset.Medium) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<DepthOfFieldPreset>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static DepthOfFieldPreset ToDepthOfFieldPreset(this string? s, DepthOfFieldPreset defaultValue = DepthOfFieldPreset.Medium) => ParseDepthOfFieldPreset(s, defaultValue);

    public static string ToApiString(this FramingComposition val) => val switch
    {
        FramingComposition.RuleOfThirds => "rule_of_thirds",
        FramingComposition.CenterWeighted => "center_weighted",
        FramingComposition.GoldenRatio => "golden_ratio",
        FramingComposition.LeadingLines => "leading_lines",
        _ => val.ToString().ToLowerInvariant()
    };
    public static FramingComposition ParseFramingComposition(string? s, FramingComposition defaultValue = FramingComposition.RuleOfThirds) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FramingComposition>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FramingComposition ToFramingComposition(this string? s, FramingComposition defaultValue = FramingComposition.RuleOfThirds) => ParseFramingComposition(s, defaultValue);

    public static string ToApiString(this SubjectMovementSpeed val) => val.ToString().ToLowerInvariant();
    public static SubjectMovementSpeed ParseSubjectMovementSpeed(string? s, SubjectMovementSpeed defaultValue = SubjectMovementSpeed.Moderate) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SubjectMovementSpeed>(s, true, out var r) ? r : defaultValue;
    public static SubjectMovementSpeed ToSubjectMovementSpeed(this string? s, SubjectMovementSpeed defaultValue = SubjectMovementSpeed.Moderate) => ParseSubjectMovementSpeed(s, defaultValue);

    public static string ToApiString(this EnvironmentWeather val) => val.ToString().ToLowerInvariant();
    public static EnvironmentWeather ParseEnvironmentWeather(string? s, EnvironmentWeather defaultValue = EnvironmentWeather.Clear) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<EnvironmentWeather>(s, true, out var r) ? r : defaultValue;
    public static EnvironmentWeather ToEnvironmentWeather(this string? s, EnvironmentWeather defaultValue = EnvironmentWeather.Clear) => ParseEnvironmentWeather(s, defaultValue);

    public static string ToApiString(this AtmosphereLighting val) => val switch
    {
        AtmosphereLighting.NaturalDaylight => "natural_daylight",
        AtmosphereLighting.GoldenHour => "golden_hour",
        AtmosphereLighting.BlueHour => "blue_hour",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AtmosphereLighting ParseAtmosphereLighting(string? s, AtmosphereLighting defaultValue = AtmosphereLighting.NaturalDaylight) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AtmosphereLighting>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AtmosphereLighting ToAtmosphereLighting(this string? s, AtmosphereLighting defaultValue = AtmosphereLighting.NaturalDaylight) => ParseAtmosphereLighting(s, defaultValue);

    public static string ToApiString(this ColorGradingTone val) => val switch
    {
        ColorGradingTone.HighContrast => "high_contrast",
        ColorGradingTone.TealAndOrange => "teal_and_orange",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ColorGradingTone ParseColorGradingTone(string? s, ColorGradingTone defaultValue = ColorGradingTone.Neutral) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ColorGradingTone>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ColorGradingTone ToColorGradingTone(this string? s, ColorGradingTone defaultValue = ColorGradingTone.Neutral) => ParseColorGradingTone(s, defaultValue);

    public static string ToApiString(this LensFocalLengthGroup val) => val switch
    {
        LensFocalLengthGroup.UltraWide => "ultra_wide",
        LensFocalLengthGroup.SuperTelephoto => "super_telephoto",
        _ => val.ToString().ToLowerInvariant()
    };
    public static LensFocalLengthGroup ParseLensFocalLengthGroup(string? s, LensFocalLengthGroup defaultValue = LensFocalLengthGroup.Standard) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<LensFocalLengthGroup>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static LensFocalLengthGroup ToLensFocalLengthGroup(this string? s, LensFocalLengthGroup defaultValue = LensFocalLengthGroup.Standard) => ParseLensFocalLengthGroup(s, defaultValue);

    public static string ToApiString(this CinematicMoodTag val) => val switch
    {
        CinematicMoodTag.ActionPacked => "action_packed",
        _ => val.ToString().ToLowerInvariant()
    };
    public static CinematicMoodTag ParseCinematicMoodTag(string? s, CinematicMoodTag defaultValue = CinematicMoodTag.Uplifting) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CinematicMoodTag>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static CinematicMoodTag ToCinematicMoodTag(this string? s, CinematicMoodTag defaultValue = CinematicMoodTag.Uplifting) => ParseCinematicMoodTag(s, defaultValue);

    public static string ToApiString(this ShotSequencePosition val) => val.ToString().ToLowerInvariant();
    public static ShotSequencePosition ParseShotSequencePosition(string? s, ShotSequencePosition defaultValue = ShotSequencePosition.Middle) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ShotSequencePosition>(s, true, out var r) ? r : defaultValue;
    public static ShotSequencePosition ToShotSequencePosition(this string? s, ShotSequencePosition defaultValue = ShotSequencePosition.Middle) => ParseShotSequencePosition(s, defaultValue);

    public static string ToApiString(this ClipDurationPreset val) => val switch
    {
        ClipDurationPreset.MaxSupported => "max_supported",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ClipDurationPreset ParseClipDurationPreset(string? s, ClipDurationPreset defaultValue = ClipDurationPreset.Standard) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ClipDurationPreset>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ClipDurationPreset ToClipDurationPreset(this string? s, ClipDurationPreset defaultValue = ClipDurationPreset.Standard) => ParseClipDurationPreset(s, defaultValue);

    public static string ToApiString(this ShotPlanValidationGate val) => val switch
    {
        ShotPlanValidationGate.NeedsReview => "needs_review",
        ShotPlanValidationGate.AutoApproved => "auto_approved",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ShotPlanValidationGate ParseShotPlanValidationGate(string? s, ShotPlanValidationGate defaultValue = ShotPlanValidationGate.Unvalidated) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ShotPlanValidationGate>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ShotPlanValidationGate ToShotPlanValidationGate(this string? s, ShotPlanValidationGate defaultValue = ShotPlanValidationGate.Unvalidated) => ParseShotPlanValidationGate(s, defaultValue);

    public static string ToApiString(this PromptLanguageStyle val) => val switch
    {
        PromptLanguageStyle.NaturalLanguage => "natural_language",
        PromptLanguageStyle.TaggedKeywords => "tagged_keywords",
        PromptLanguageStyle.TechnicalCinematic => "technical_cinematic",
        PromptLanguageStyle.StorybookNarrative => "storybook_narrative",
        _ => val.ToString().ToLowerInvariant()
    };
    public static PromptLanguageStyle ParsePromptLanguageStyle(string? s, PromptLanguageStyle defaultValue = PromptLanguageStyle.NaturalLanguage) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<PromptLanguageStyle>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static PromptLanguageStyle ToPromptLanguageStyle(this string? s, PromptLanguageStyle defaultValue = PromptLanguageStyle.NaturalLanguage) => ParsePromptLanguageStyle(s, defaultValue);

    public static string ToApiString(this ShotPlanExportTarget val) => val switch
    {
        ShotPlanExportTarget.FinalCutProXml => "final_cut_pro_xml",
        ShotPlanExportTarget.PremiereProXml => "premiere_pro_xml",
        ShotPlanExportTarget.DaVinciResolve => "davinci_resolve",
        _ => val.ToString().ToLowerInvariant()
    };
    public static ShotPlanExportTarget ParseShotPlanExportTarget(string? s, ShotPlanExportTarget defaultValue = ShotPlanExportTarget.Json) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ShotPlanExportTarget>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ShotPlanExportTarget ToShotPlanExportTarget(this string? s, ShotPlanExportTarget defaultValue = ShotPlanExportTarget.Json) => ParseShotPlanExportTarget(s, defaultValue);
}

#endregion
