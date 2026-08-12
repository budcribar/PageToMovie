using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Adaptation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenplayStandardType
{
    Fountain,
    FinalDraft,
    Celtx,
    Plaintext,
    PdfImport
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TitlePageMetadataField
{
    Title,
    Credit,
    Author,
    Source,
    Notes,
    DraftDate,
    Contact,
    Copyright
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SceneHeadingPrefixType
{
    Int,
    Ext,
    IntExt,
    Est,
    Ie
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterCasingRule
{
    Uppercase,
    TitleCase,
    Original
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParentheticalCategory
{
    DeliveryTone,
    ActionInstruction,
    Pause,
    TargetSpeaker,
    Generic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualDialogueLayout
{
    None,
    Left,
    Right,
    Balanced
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PageBreakKindType
{
    Explicit,
    Forced,
    Automatic,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SectionHeaderDepth
{
    Level1,
    Level2,
    Level3,
    Level4,
    Level5,
    DepthNone
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainTokenType
{
    SceneHeading,
    Action,
    Character,
    Dialogue,
    Parenthetical,
    Transition,
    Centered,
    PageBreak,
    SectionHeader,
    Synopsis,
    Comment,
    Boneyard,
    TitlePage,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenplayAnalysisKind
{
    Structure,
    CharacterArcs,
    Pacing,
    DialogueRatio,
    SceneCount,
    WordCount,
    Full
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookGenreType
{
    Fiction,
    NonFiction,
    ChildrensPictureBook,
    YoungAdult,
    SciFiFantasy,
    MysteryThriller,
    Romance,
    Historical,
    Memoir,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NarrativePacingStyle
{
    FastPaced,
    Balanced,
    SlowBurn,
    Episodic,
    ActionHeavy
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtagonistArchetypeKind
{
    Hero,
    AntiHero,
    Everyman,
    Explorer,
    Innocent,
    Outlaw,
    Sage,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdaptationConfidenceScore
{
    Low,
    Medium,
    High,
    VeryHigh,
    Uncertain
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextSanitizationMode
{
    Strict,
    Standard,
    Minimal,
    Raw
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LineEndingKind
{
    Lf,
    Crlf,
    Cr,
    Mixed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterRoleTier
{
    Lead,
    Supporting,
    Minor,
    Background,
    Narrator
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DialogueEmotionTagKind
{
    Neutral,
    Joy,
    Sadness,
    Anger,
    Fear,
    Surprise,
    Disgust,
    Whispering,
    Shouting
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SceneTransitionEffectKind
{
    Cut,
    Dissolve,
    FadeIn,
    FadeOut,
    Wipe,
    JumpCut,
    MatchCut,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Stage1WorkflowPhase
{
    Uninitialized,
    ImportingBook,
    AnalyzingText,
    ExtractingCast,
    GeneratingFountain,
    ValidatingScreenplay,
    Approved
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Stage2PlannerModeType
{
    Standard,
    Cinematic,
    FastDraft,
    DetailedBreakdown,
    AutoRegenerate
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptTemplateFamilyType
{
    ScreenplayAdaptation,
    CharacterExtraction,
    ShotPlanning,
    PortraitGen,
    VideoGen,
    MusicGen,
    VoiceGen
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisualMediumStylePreset
{
    Cinematic3d,
    [JsonPropertyName("2dAnimation")]
    Animation2d,
    Photorealistic,
    Watercolor,
    Anime,
    OilPainting,
    ComicBook,
    Default
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterVisualLockStrategy
{
    StrictReferenceImage,
    PromptDescriptionOnly,
    Hybrid,
    DynamicAdaptive,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WardrobeConsistencyMode
{
    FixedPerCharacter,
    SceneBased,
    TimeBased,
    Dynamic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptScrubbingRule
{
    RemoveNames,
    RemoveAntiPatterns,
    SanitizeBrackets,
    EnforceBudget,
    StripJargon,
    All
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraFocusDistanceSpec
{
    Macro,
    CloseUp,
    Medium,
    Deep,
    Infinity,
    RackFocus
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepthOfFieldPresetKind
{
    Shallow,
    Deep,
    TiltShift,
    BokehRich,
    Standard
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FramingCompositionStyle
{
    RuleOfThirds,
    Centered,
    Symmetrical,
    GoldenRatio,
    ExtremeWide,
    CloseUpDetail,
    OverTheShoulder,
    Dynamic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubjectMovementSpeedBand
{
    Static,
    SlowMotion,
    NormalPacing,
    FastAction,
    Hyperlapse
}

public static class AdaptationSyntaxEnumExtensions
{
    public static AdaptationConfidenceScore ParseAdaptationConfidenceScore(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "low" => AdaptationConfidenceScore.Low,
                "high" => AdaptationConfidenceScore.High,
                "very_high" or "veryhigh" => AdaptationConfidenceScore.VeryHigh,
                "uncertain" => AdaptationConfidenceScore.Uncertain,
                _ => AdaptationConfidenceScore.Medium
            };

    public static BookGenreType ParseBookGenreType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "non_fiction" or "nonfiction" => BookGenreType.NonFiction,
                "childrens_picture_book" or "picture_book" => BookGenreType.ChildrensPictureBook,
                "young_adult" or "ya" => BookGenreType.YoungAdult,
                "scifi_fantasy" or "scifi" or "fantasy" => BookGenreType.SciFiFantasy,
                "mystery_thriller" or "mystery" or "thriller" => BookGenreType.MysteryThriller,
                "romance" => BookGenreType.Romance,
                "historical" => BookGenreType.Historical,
                "memoir" => BookGenreType.Memoir,
                "other" => BookGenreType.Other,
                _ => BookGenreType.Fiction
            };

    public static CameraFocusDistanceSpec ParseCameraFocusDistanceSpec(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "macro" => CameraFocusDistanceSpec.Macro,
                "close_up" or "closeup" => CameraFocusDistanceSpec.CloseUp,
                "deep" => CameraFocusDistanceSpec.Deep,
                "infinity" => CameraFocusDistanceSpec.Infinity,
                "rack_focus" or "rackfocus" => CameraFocusDistanceSpec.RackFocus,
                _ => CameraFocusDistanceSpec.Medium
            };

    public static CharacterCasingRule ParseCharacterCasingRule(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "title_case" or "titlecase" or "title" => CharacterCasingRule.TitleCase,
                "original" => CharacterCasingRule.Original,
                _ => CharacterCasingRule.Uppercase
            };

    public static CharacterRoleTier ParseCharacterRoleTier(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "lead" => CharacterRoleTier.Lead,
                "minor" => CharacterRoleTier.Minor,
                "background" => CharacterRoleTier.Background,
                "narrator" => CharacterRoleTier.Narrator,
                _ => CharacterRoleTier.Supporting
            };

    public static CharacterVisualLockStrategy ParseCharacterVisualLockStrategy(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "prompt_description_only" or "promptdescriptiononly" => CharacterVisualLockStrategy.PromptDescriptionOnly,
                "hybrid" => CharacterVisualLockStrategy.Hybrid,
                "dynamic_adaptive" or "dynamicadaptive" => CharacterVisualLockStrategy.DynamicAdaptive,
                "none" => CharacterVisualLockStrategy.None,
                _ => CharacterVisualLockStrategy.StrictReferenceImage
            };

    public static DepthOfFieldPresetKind ParseDepthOfFieldPresetKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "shallow" => DepthOfFieldPresetKind.Shallow,
                "deep" => DepthOfFieldPresetKind.Deep,
                "tilt_shift" or "tiltshift" => DepthOfFieldPresetKind.TiltShift,
                "bokeh_rich" or "bokehrich" => DepthOfFieldPresetKind.BokehRich,
                _ => DepthOfFieldPresetKind.Standard
            };

    public static DialogueEmotionTagKind ParseDialogueEmotionTagKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "joy" => DialogueEmotionTagKind.Joy,
                "sadness" => DialogueEmotionTagKind.Sadness,
                "anger" => DialogueEmotionTagKind.Anger,
                "fear" => DialogueEmotionTagKind.Fear,
                "surprise" => DialogueEmotionTagKind.Surprise,
                "disgust" => DialogueEmotionTagKind.Disgust,
                "whispering" => DialogueEmotionTagKind.Whispering,
                "shouting" => DialogueEmotionTagKind.Shouting,
                _ => DialogueEmotionTagKind.Neutral
            };

    public static DualDialogueLayout ParseDualDialogueLayout(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "left" => DualDialogueLayout.Left,
                "right" => DualDialogueLayout.Right,
                "balanced" => DualDialogueLayout.Balanced,
                _ => DualDialogueLayout.None
            };

    public static FountainTokenType ParseFountainTokenType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "scene_heading" or "sceneheading" => FountainTokenType.SceneHeading,
                "action" => FountainTokenType.Action,
                "character" => FountainTokenType.Character,
                "dialogue" => FountainTokenType.Dialogue,
                "parenthetical" => FountainTokenType.Parenthetical,
                "transition" => FountainTokenType.Transition,
                "centered" => FountainTokenType.Centered,
                "page_break" or "pagebreak" => FountainTokenType.PageBreak,
                "section_header" or "sectionheader" => FountainTokenType.SectionHeader,
                "synopsis" => FountainTokenType.Synopsis,
                "comment" => FountainTokenType.Comment,
                "boneyard" => FountainTokenType.Boneyard,
                "title_page" or "titlepage" => FountainTokenType.TitlePage,
                _ => FountainTokenType.Unknown
            };

    public static FramingCompositionStyle ParseFramingCompositionStyle(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "rule_of_thirds" or "ruleofthirds" => FramingCompositionStyle.RuleOfThirds,
                "centered" => FramingCompositionStyle.Centered,
                "symmetrical" => FramingCompositionStyle.Symmetrical,
                "golden_ratio" or "goldenratio" => FramingCompositionStyle.GoldenRatio,
                "extreme_wide" or "extremewide" => FramingCompositionStyle.ExtremeWide,
                "close_up_detail" or "closeupdetail" => FramingCompositionStyle.CloseUpDetail,
                "over_the_shoulder" or "overtheshoulder" => FramingCompositionStyle.OverTheShoulder,
                _ => FramingCompositionStyle.Dynamic
            };

    public static LineEndingKind ParseLineEndingKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "crlf" => LineEndingKind.Crlf,
                "cr" => LineEndingKind.Cr,
                "mixed" => LineEndingKind.Mixed,
                _ => LineEndingKind.Lf
            };

    public static NarrativePacingStyle ParseNarrativePacingStyle(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "fast_paced" or "fastpaced" or "fast" => NarrativePacingStyle.FastPaced,
                "slow_burn" or "slowburn" or "slow" => NarrativePacingStyle.SlowBurn,
                "episodic" => NarrativePacingStyle.Episodic,
                "action_heavy" or "actionheavy" => NarrativePacingStyle.ActionHeavy,
                _ => NarrativePacingStyle.Balanced
            };

    public static PageBreakKindType ParsePageBreakKindType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "explicit" => PageBreakKindType.Explicit,
                "forced" => PageBreakKindType.Forced,
                "automatic" => PageBreakKindType.Automatic,
                _ => PageBreakKindType.None
            };

    public static ParentheticalCategory ParseParentheticalCategory(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "delivery_tone" or "deliverytone" => ParentheticalCategory.DeliveryTone,
                "action_instruction" or "actioninstruction" => ParentheticalCategory.ActionInstruction,
                "pause" => ParentheticalCategory.Pause,
                "target_speaker" or "targetspeaker" => ParentheticalCategory.TargetSpeaker,
                _ => ParentheticalCategory.Generic
            };

    public static PromptScrubbingRule ParsePromptScrubbingRule(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "remove_names" or "removenames" => PromptScrubbingRule.RemoveNames,
                "remove_anti_patterns" or "removeantipatterns" => PromptScrubbingRule.RemoveAntiPatterns,
                "sanitize_brackets" or "sanitizebrackets" => PromptScrubbingRule.SanitizeBrackets,
                "enforce_budget" or "enforcebudget" => PromptScrubbingRule.EnforceBudget,
                "strip_jargon" or "stripjargon" => PromptScrubbingRule.StripJargon,
                _ => PromptScrubbingRule.All
            };

    public static PromptTemplateFamilyType ParsePromptTemplateFamilyType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "character_extraction" or "characterextraction" => PromptTemplateFamilyType.CharacterExtraction,
                "shot_planning" or "shotplanning" => PromptTemplateFamilyType.ShotPlanning,
                "portrait_gen" or "portraitgen" => PromptTemplateFamilyType.PortraitGen,
                "video_gen" or "videogen" => PromptTemplateFamilyType.VideoGen,
                "music_gen" or "musicgen" => PromptTemplateFamilyType.MusicGen,
                "voice_gen" or "voicegen" => PromptTemplateFamilyType.VoiceGen,
                _ => PromptTemplateFamilyType.ScreenplayAdaptation
            };

    public static ProtagonistArchetypeKind ParseProtagonistArchetypeKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "anti_hero" or "antihero" => ProtagonistArchetypeKind.AntiHero,
                "everyman" => ProtagonistArchetypeKind.Everyman,
                "explorer" => ProtagonistArchetypeKind.Explorer,
                "innocent" => ProtagonistArchetypeKind.Innocent,
                "outlaw" => ProtagonistArchetypeKind.Outlaw,
                "sage" => ProtagonistArchetypeKind.Sage,
                "other" => ProtagonistArchetypeKind.Other,
                _ => ProtagonistArchetypeKind.Hero
            };

    public static SceneHeadingPrefixType ParseSceneHeadingPrefixType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "ext" or "ext." => SceneHeadingPrefixType.Ext,
                "int_ext" or "intext" or "int/ext" or "int./ext." => SceneHeadingPrefixType.IntExt,
                "est" or "est." => SceneHeadingPrefixType.Est,
                "ie" or "i./e." or "i/e" => SceneHeadingPrefixType.Ie,
                _ => SceneHeadingPrefixType.Int
            };

    public static SceneTransitionEffectKind ParseSceneTransitionEffectKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "dissolve" => SceneTransitionEffectKind.Dissolve,
                "fade_in" or "fadein" => SceneTransitionEffectKind.FadeIn,
                "fade_out" or "fadeout" => SceneTransitionEffectKind.FadeOut,
                "wipe" => SceneTransitionEffectKind.Wipe,
                "jump_cut" or "jumpcut" => SceneTransitionEffectKind.JumpCut,
                "match_cut" or "matchcut" => SceneTransitionEffectKind.MatchCut,
                "none" => SceneTransitionEffectKind.None,
                _ => SceneTransitionEffectKind.Cut
            };

    public static ScreenplayAnalysisKind ParseScreenplayAnalysisKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "structure" => ScreenplayAnalysisKind.Structure,
                "character_arcs" or "characterarcs" => ScreenplayAnalysisKind.CharacterArcs,
                "pacing" => ScreenplayAnalysisKind.Pacing,
                "dialogue_ratio" or "dialogueratio" => ScreenplayAnalysisKind.DialogueRatio,
                "scene_count" or "scenecount" => ScreenplayAnalysisKind.SceneCount,
                "word_count" or "wordcount" => ScreenplayAnalysisKind.WordCount,
                _ => ScreenplayAnalysisKind.Full
            };

    public static ScreenplayStandardType ParseScreenplayStandardType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "final_draft" or "finaldraft" => ScreenplayStandardType.FinalDraft,
                "celtx" => ScreenplayStandardType.Celtx,
                "plaintext" => ScreenplayStandardType.Plaintext,
                "pdf_import" or "pdf" => ScreenplayStandardType.PdfImport,
                _ => ScreenplayStandardType.Fountain
            };

    public static SectionHeaderDepth ParseSectionHeaderDepth(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "level_1" or "level1" or "1" => SectionHeaderDepth.Level1,
                "level_2" or "level2" or "2" => SectionHeaderDepth.Level2,
                "level_3" or "level3" or "3" => SectionHeaderDepth.Level3,
                "level_4" or "level4" or "4" => SectionHeaderDepth.Level4,
                "level_5" or "level5" or "5" => SectionHeaderDepth.Level5,
                _ => SectionHeaderDepth.DepthNone
            };

    public static Stage1WorkflowPhase ParseStage1WorkflowPhase(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "importing_book" or "importingbook" => Stage1WorkflowPhase.ImportingBook,
                "analyzing_text" or "analyzingtext" => Stage1WorkflowPhase.AnalyzingText,
                "extracting_cast" or "extractingcast" => Stage1WorkflowPhase.ExtractingCast,
                "generating_fountain" or "generatingfountain" => Stage1WorkflowPhase.GeneratingFountain,
                "validating_screenplay" or "validatingscreenplay" => Stage1WorkflowPhase.ValidatingScreenplay,
                "approved" => Stage1WorkflowPhase.Approved,
                _ => Stage1WorkflowPhase.Uninitialized
            };

    public static Stage2PlannerModeType ParseStage2PlannerModeType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "cinematic" => Stage2PlannerModeType.Cinematic,
                "fast_draft" or "fastdraft" => Stage2PlannerModeType.FastDraft,
                "detailed_breakdown" or "detailedbreakdown" => Stage2PlannerModeType.DetailedBreakdown,
                "auto_regenerate" or "autoregenerate" => Stage2PlannerModeType.AutoRegenerate,
                _ => Stage2PlannerModeType.Standard
            };

    public static SubjectMovementSpeedBand ParseSubjectMovementSpeedBand(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "static" => SubjectMovementSpeedBand.Static,
                "slow_motion" or "slowmotion" => SubjectMovementSpeedBand.SlowMotion,
                "fast_action" or "fastaction" => SubjectMovementSpeedBand.FastAction,
                "hyperlapse" => SubjectMovementSpeedBand.Hyperlapse,
                _ => SubjectMovementSpeedBand.NormalPacing
            };

    public static TextSanitizationMode ParseTextSanitizationMode(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "strict" => TextSanitizationMode.Strict,
                "minimal" => TextSanitizationMode.Minimal,
                "raw" => TextSanitizationMode.Raw,
                _ => TextSanitizationMode.Standard
            };

    public static TitlePageMetadataField ParseTitlePageMetadataField(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "credit" => TitlePageMetadataField.Credit,
                "author" => TitlePageMetadataField.Author,
                "source" => TitlePageMetadataField.Source,
                "notes" => TitlePageMetadataField.Notes,
                "draft_date" or "draftdate" => TitlePageMetadataField.DraftDate,
                "contact" => TitlePageMetadataField.Contact,
                "copyright" => TitlePageMetadataField.Copyright,
                _ => TitlePageMetadataField.Title
            };

    public static VisualMediumStylePreset ParseVisualMediumStylePreset(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "cinematic_3d" or "cinematic3d" => VisualMediumStylePreset.Cinematic3d,
                "2d_animation" or "2danimation" => VisualMediumStylePreset.Animation2d,
                "photorealistic" => VisualMediumStylePreset.Photorealistic,
                "watercolor" => VisualMediumStylePreset.Watercolor,
                "anime" => VisualMediumStylePreset.Anime,
                "oil_painting" or "oilpainting" => VisualMediumStylePreset.OilPainting,
                "comic_book" or "comicbook" => VisualMediumStylePreset.ComicBook,
                _ => VisualMediumStylePreset.Default
            };

    public static WardrobeConsistencyMode ParseWardrobeConsistencyMode(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "scene_based" or "scenebased" => WardrobeConsistencyMode.SceneBased,
                "time_based" or "timebased" => WardrobeConsistencyMode.TimeBased,
                "dynamic" => WardrobeConsistencyMode.Dynamic,
                _ => WardrobeConsistencyMode.FixedPerCharacter
            };

    public static string ToApiString(this ScreenplayStandardType standard) => standard switch
        {
            ScreenplayStandardType.Fountain => "fountain",
            ScreenplayStandardType.FinalDraft => "final_draft",
            ScreenplayStandardType.Celtx => "celtx",
            ScreenplayStandardType.Plaintext => "plaintext",
            ScreenplayStandardType.PdfImport => "pdf_import",
            _ => "fountain"
        };

    public static string ToApiString(this TitlePageMetadataField field) => field switch
        {
            TitlePageMetadataField.Title => "title",
            TitlePageMetadataField.Credit => "credit",
            TitlePageMetadataField.Author => "author",
            TitlePageMetadataField.Source => "source",
            TitlePageMetadataField.Notes => "notes",
            TitlePageMetadataField.DraftDate => "draft_date",
            TitlePageMetadataField.Contact => "contact",
            TitlePageMetadataField.Copyright => "copyright",
            _ => "title"
        };

    public static string ToApiString(this SceneHeadingPrefixType prefix) => prefix switch
        {
            SceneHeadingPrefixType.Int => "int",
            SceneHeadingPrefixType.Ext => "ext",
            SceneHeadingPrefixType.IntExt => "int_ext",
            SceneHeadingPrefixType.Est => "est",
            SceneHeadingPrefixType.Ie => "ie",
            _ => "int"
        };

    public static string ToApiString(this CharacterCasingRule rule) => rule switch
        {
            CharacterCasingRule.Uppercase => "uppercase",
            CharacterCasingRule.TitleCase => "title_case",
            CharacterCasingRule.Original => "original",
            _ => "uppercase"
        };

    public static string ToApiString(this ParentheticalCategory category) => category switch
        {
            ParentheticalCategory.DeliveryTone => "delivery_tone",
            ParentheticalCategory.ActionInstruction => "action_instruction",
            ParentheticalCategory.Pause => "pause",
            ParentheticalCategory.TargetSpeaker => "target_speaker",
            ParentheticalCategory.Generic => "generic",
            _ => "generic"
        };

    public static string ToApiString(this DualDialogueLayout layout) => layout switch
        {
            DualDialogueLayout.None => "none",
            DualDialogueLayout.Left => "left",
            DualDialogueLayout.Right => "right",
            DualDialogueLayout.Balanced => "balanced",
            _ => "none"
        };

    public static string ToApiString(this PageBreakKindType kind) => kind switch
        {
            PageBreakKindType.Explicit => "explicit",
            PageBreakKindType.Forced => "forced",
            PageBreakKindType.Automatic => "automatic",
            PageBreakKindType.None => "none",
            _ => "none"
        };

    public static string ToApiString(this SectionHeaderDepth depth) => depth switch
        {
            SectionHeaderDepth.Level1 => "level_1",
            SectionHeaderDepth.Level2 => "level_2",
            SectionHeaderDepth.Level3 => "level_3",
            SectionHeaderDepth.Level4 => "level_4",
            SectionHeaderDepth.Level5 => "level_5",
            SectionHeaderDepth.DepthNone => "none",
            _ => "none"
        };

    public static string ToApiString(this FountainTokenType tokenType) => tokenType switch
        {
            FountainTokenType.SceneHeading => "scene_heading",
            FountainTokenType.Action => "action",
            FountainTokenType.Character => "character",
            FountainTokenType.Dialogue => "dialogue",
            FountainTokenType.Parenthetical => "parenthetical",
            FountainTokenType.Transition => "transition",
            FountainTokenType.Centered => "centered",
            FountainTokenType.PageBreak => "page_break",
            FountainTokenType.SectionHeader => "section_header",
            FountainTokenType.Synopsis => "synopsis",
            FountainTokenType.Comment => "comment",
            FountainTokenType.Boneyard => "boneyard",
            FountainTokenType.TitlePage => "title_page",
            _ => "unknown"
        };

    public static string ToApiString(this ScreenplayAnalysisKind kind) => kind switch
        {
            ScreenplayAnalysisKind.Structure => "structure",
            ScreenplayAnalysisKind.CharacterArcs => "character_arcs",
            ScreenplayAnalysisKind.Pacing => "pacing",
            ScreenplayAnalysisKind.DialogueRatio => "dialogue_ratio",
            ScreenplayAnalysisKind.SceneCount => "scene_count",
            ScreenplayAnalysisKind.WordCount => "word_count",
            ScreenplayAnalysisKind.Full => "full",
            _ => "full"
        };

    public static string ToApiString(this BookGenreType genre) => genre switch
        {
            BookGenreType.Fiction => "fiction",
            BookGenreType.NonFiction => "non_fiction",
            BookGenreType.ChildrensPictureBook => "childrens_picture_book",
            BookGenreType.YoungAdult => "young_adult",
            BookGenreType.SciFiFantasy => "scifi_fantasy",
            BookGenreType.MysteryThriller => "mystery_thriller",
            BookGenreType.Romance => "romance",
            BookGenreType.Historical => "historical",
            BookGenreType.Memoir => "memoir",
            BookGenreType.Other => "other",
            _ => "fiction"
        };

    public static string ToApiString(this NarrativePacingStyle pacing) => pacing switch
        {
            NarrativePacingStyle.FastPaced => "fast_paced",
            NarrativePacingStyle.Balanced => "balanced",
            NarrativePacingStyle.SlowBurn => "slow_burn",
            NarrativePacingStyle.Episodic => "episodic",
            NarrativePacingStyle.ActionHeavy => "action_heavy",
            _ => "balanced"
        };

    public static string ToApiString(this ProtagonistArchetypeKind archetype) => archetype switch
        {
            ProtagonistArchetypeKind.Hero => "hero",
            ProtagonistArchetypeKind.AntiHero => "anti_hero",
            ProtagonistArchetypeKind.Everyman => "everyman",
            ProtagonistArchetypeKind.Explorer => "explorer",
            ProtagonistArchetypeKind.Innocent => "innocent",
            ProtagonistArchetypeKind.Outlaw => "outlaw",
            ProtagonistArchetypeKind.Sage => "sage",
            ProtagonistArchetypeKind.Other => "other",
            _ => "hero"
        };

    public static string ToApiString(this AdaptationConfidenceScore score) => score switch
        {
            AdaptationConfidenceScore.Low => "low",
            AdaptationConfidenceScore.Medium => "medium",
            AdaptationConfidenceScore.High => "high",
            AdaptationConfidenceScore.VeryHigh => "very_high",
            AdaptationConfidenceScore.Uncertain => "uncertain",
            _ => "medium"
        };

    public static string ToApiString(this TextSanitizationMode mode) => mode switch
        {
            TextSanitizationMode.Strict => "strict",
            TextSanitizationMode.Standard => "standard",
            TextSanitizationMode.Minimal => "minimal",
            TextSanitizationMode.Raw => "raw",
            _ => "standard"
        };

    public static string ToApiString(this LineEndingKind kind) => kind switch
        {
            LineEndingKind.Lf => "lf",
            LineEndingKind.Crlf => "crlf",
            LineEndingKind.Cr => "cr",
            LineEndingKind.Mixed => "mixed",
            _ => "lf"
        };

    public static string ToApiString(this CharacterRoleTier tier) => tier switch
        {
            CharacterRoleTier.Lead => "lead",
            CharacterRoleTier.Supporting => "supporting",
            CharacterRoleTier.Minor => "minor",
            CharacterRoleTier.Background => "background",
            CharacterRoleTier.Narrator => "narrator",
            _ => "supporting"
        };

    public static string ToApiString(this DialogueEmotionTagKind kind) => kind switch
        {
            DialogueEmotionTagKind.Neutral => "neutral",
            DialogueEmotionTagKind.Joy => "joy",
            DialogueEmotionTagKind.Sadness => "sadness",
            DialogueEmotionTagKind.Anger => "anger",
            DialogueEmotionTagKind.Fear => "fear",
            DialogueEmotionTagKind.Surprise => "surprise",
            DialogueEmotionTagKind.Disgust => "disgust",
            DialogueEmotionTagKind.Whispering => "whispering",
            DialogueEmotionTagKind.Shouting => "shouting",
            _ => "neutral"
        };

    public static string ToApiString(this SceneTransitionEffectKind effect) => effect switch
        {
            SceneTransitionEffectKind.Cut => "cut",
            SceneTransitionEffectKind.Dissolve => "dissolve",
            SceneTransitionEffectKind.FadeIn => "fade_in",
            SceneTransitionEffectKind.FadeOut => "fade_out",
            SceneTransitionEffectKind.Wipe => "wipe",
            SceneTransitionEffectKind.JumpCut => "jump_cut",
            SceneTransitionEffectKind.MatchCut => "match_cut",
            SceneTransitionEffectKind.None => "none",
            _ => "cut"
        };

    public static string ToApiString(this Stage1WorkflowPhase phase) => phase switch
        {
            Stage1WorkflowPhase.Uninitialized => "uninitialized",
            Stage1WorkflowPhase.ImportingBook => "importing_book",
            Stage1WorkflowPhase.AnalyzingText => "analyzing_text",
            Stage1WorkflowPhase.ExtractingCast => "extracting_cast",
            Stage1WorkflowPhase.GeneratingFountain => "generating_fountain",
            Stage1WorkflowPhase.ValidatingScreenplay => "validating_screenplay",
            Stage1WorkflowPhase.Approved => "approved",
            _ => "uninitialized"
        };

    public static string ToApiString(this Stage2PlannerModeType mode) => mode switch
        {
            Stage2PlannerModeType.Standard => "standard",
            Stage2PlannerModeType.Cinematic => "cinematic",
            Stage2PlannerModeType.FastDraft => "fast_draft",
            Stage2PlannerModeType.DetailedBreakdown => "detailed_breakdown",
            Stage2PlannerModeType.AutoRegenerate => "auto_regenerate",
            _ => "standard"
        };

    public static string ToApiString(this PromptTemplateFamilyType family) => family switch
        {
            PromptTemplateFamilyType.ScreenplayAdaptation => "screenplay_adaptation",
            PromptTemplateFamilyType.CharacterExtraction => "character_extraction",
            PromptTemplateFamilyType.ShotPlanning => "shot_planning",
            PromptTemplateFamilyType.PortraitGen => "portrait_gen",
            PromptTemplateFamilyType.VideoGen => "video_gen",
            PromptTemplateFamilyType.MusicGen => "music_gen",
            PromptTemplateFamilyType.VoiceGen => "voice_gen",
            _ => "screenplay_adaptation"
        };

    public static string ToApiString(this VisualMediumStylePreset style) => style switch
        {
            VisualMediumStylePreset.Cinematic3d => "cinematic_3d",
            VisualMediumStylePreset.Animation2d => "2d_animation",
            VisualMediumStylePreset.Photorealistic => "photorealistic",
            VisualMediumStylePreset.Watercolor => "watercolor",
            VisualMediumStylePreset.Anime => "anime",
            VisualMediumStylePreset.OilPainting => "oil_painting",
            VisualMediumStylePreset.ComicBook => "comic_book",
            VisualMediumStylePreset.Default => "default",
            _ => "default"
        };

    public static string ToApiString(this CharacterVisualLockStrategy strategy) => strategy switch
        {
            CharacterVisualLockStrategy.StrictReferenceImage => "strict_reference_image",
            CharacterVisualLockStrategy.PromptDescriptionOnly => "prompt_description_only",
            CharacterVisualLockStrategy.Hybrid => "hybrid",
            CharacterVisualLockStrategy.DynamicAdaptive => "dynamic_adaptive",
            CharacterVisualLockStrategy.None => "none",
            _ => "strict_reference_image"
        };

    public static string ToApiString(this WardrobeConsistencyMode mode) => mode switch
        {
            WardrobeConsistencyMode.FixedPerCharacter => "fixed_per_character",
            WardrobeConsistencyMode.SceneBased => "scene_based",
            WardrobeConsistencyMode.TimeBased => "time_based",
            WardrobeConsistencyMode.Dynamic => "dynamic",
            _ => "fixed_per_character"
        };

    public static string ToApiString(this PromptScrubbingRule rule) => rule switch
        {
            PromptScrubbingRule.RemoveNames => "remove_names",
            PromptScrubbingRule.RemoveAntiPatterns => "remove_anti_patterns",
            PromptScrubbingRule.SanitizeBrackets => "sanitize_brackets",
            PromptScrubbingRule.EnforceBudget => "enforce_budget",
            PromptScrubbingRule.StripJargon => "strip_jargon",
            PromptScrubbingRule.All => "all",
            _ => "all"
        };

    public static string ToApiString(this CameraFocusDistanceSpec spec) => spec switch
        {
            CameraFocusDistanceSpec.Macro => "macro",
            CameraFocusDistanceSpec.CloseUp => "close_up",
            CameraFocusDistanceSpec.Medium => "medium",
            CameraFocusDistanceSpec.Deep => "deep",
            CameraFocusDistanceSpec.Infinity => "infinity",
            CameraFocusDistanceSpec.RackFocus => "rack_focus",
            _ => "medium"
        };

    public static string ToApiString(this DepthOfFieldPresetKind kind) => kind switch
        {
            DepthOfFieldPresetKind.Shallow => "shallow",
            DepthOfFieldPresetKind.Deep => "deep",
            DepthOfFieldPresetKind.TiltShift => "tilt_shift",
            DepthOfFieldPresetKind.BokehRich => "bokeh_rich",
            DepthOfFieldPresetKind.Standard => "standard",
            _ => "standard"
        };

    public static string ToApiString(this FramingCompositionStyle style) => style switch
        {
            FramingCompositionStyle.RuleOfThirds => "rule_of_thirds",
            FramingCompositionStyle.Centered => "centered",
            FramingCompositionStyle.Symmetrical => "symmetrical",
            FramingCompositionStyle.GoldenRatio => "golden_ratio",
            FramingCompositionStyle.ExtremeWide => "extreme_wide",
            FramingCompositionStyle.CloseUpDetail => "close_up_detail",
            FramingCompositionStyle.OverTheShoulder => "over_the_shoulder",
            FramingCompositionStyle.Dynamic => "dynamic",
            _ => "dynamic"
        };

    public static string ToApiString(this SubjectMovementSpeedBand band) => band switch
        {
            SubjectMovementSpeedBand.Static => "static",
            SubjectMovementSpeedBand.SlowMotion => "slow_motion",
            SubjectMovementSpeedBand.NormalPacing => "normal_pacing",
            SubjectMovementSpeedBand.FastAction => "fast_action",
            SubjectMovementSpeedBand.Hyperlapse => "hyperlapse",
            _ => "normal_pacing"
        };

    public static bool TryParseAdaptationConfidenceScore(string? value, out AdaptationConfidenceScore result)
        {
            result = ParseAdaptationConfidenceScore(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseBookGenreType(string? value, out BookGenreType result)
        {
            result = ParseBookGenreType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseCameraFocusDistanceSpec(string? value, out CameraFocusDistanceSpec result)
        {
            result = ParseCameraFocusDistanceSpec(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseCharacterCasingRule(string? value, out CharacterCasingRule result)
        {
            result = ParseCharacterCasingRule(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseCharacterRoleTier(string? value, out CharacterRoleTier result)
        {
            result = ParseCharacterRoleTier(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseCharacterVisualLockStrategy(string? value, out CharacterVisualLockStrategy result)
        {
            result = ParseCharacterVisualLockStrategy(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseDepthOfFieldPresetKind(string? value, out DepthOfFieldPresetKind result)
        {
            result = ParseDepthOfFieldPresetKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseDialogueEmotionTagKind(string? value, out DialogueEmotionTagKind result)
        {
            result = ParseDialogueEmotionTagKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseDualDialogueLayout(string? value, out DualDialogueLayout result)
        {
            result = ParseDualDialogueLayout(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseFountainTokenType(string? value, out FountainTokenType result)
        {
            result = ParseFountainTokenType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseFramingCompositionStyle(string? value, out FramingCompositionStyle result)
        {
            result = ParseFramingCompositionStyle(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseLineEndingKind(string? value, out LineEndingKind result)
        {
            result = ParseLineEndingKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseNarrativePacingStyle(string? value, out NarrativePacingStyle result)
        {
            result = ParseNarrativePacingStyle(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParsePageBreakKindType(string? value, out PageBreakKindType result)
        {
            result = ParsePageBreakKindType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseParentheticalCategory(string? value, out ParentheticalCategory result)
        {
            result = ParseParentheticalCategory(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParsePromptScrubbingRule(string? value, out PromptScrubbingRule result)
        {
            result = ParsePromptScrubbingRule(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParsePromptTemplateFamilyType(string? value, out PromptTemplateFamilyType result)
        {
            result = ParsePromptTemplateFamilyType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseProtagonistArchetypeKind(string? value, out ProtagonistArchetypeKind result)
        {
            result = ParseProtagonistArchetypeKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseSceneHeadingPrefixType(string? value, out SceneHeadingPrefixType result)
        {
            result = ParseSceneHeadingPrefixType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseSceneTransitionEffectKind(string? value, out SceneTransitionEffectKind result)
        {
            result = ParseSceneTransitionEffectKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseScreenplayAnalysisKind(string? value, out ScreenplayAnalysisKind result)
        {
            result = ParseScreenplayAnalysisKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseScreenplayStandardType(string? value, out ScreenplayStandardType result)
        {
            result = ParseScreenplayStandardType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseSectionHeaderDepth(string? value, out SectionHeaderDepth result)
        {
            result = ParseSectionHeaderDepth(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseStage1WorkflowPhase(string? value, out Stage1WorkflowPhase result)
        {
            result = ParseStage1WorkflowPhase(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseStage2PlannerModeType(string? value, out Stage2PlannerModeType result)
        {
            result = ParseStage2PlannerModeType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseSubjectMovementSpeedBand(string? value, out SubjectMovementSpeedBand result)
        {
            result = ParseSubjectMovementSpeedBand(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseTextSanitizationMode(string? value, out TextSanitizationMode result)
        {
            result = ParseTextSanitizationMode(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseTitlePageMetadataField(string? value, out TitlePageMetadataField result)
        {
            result = ParseTitlePageMetadataField(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseVisualMediumStylePreset(string? value, out VisualMediumStylePreset result)
        {
            result = ParseVisualMediumStylePreset(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseWardrobeConsistencyMode(string? value, out WardrobeConsistencyMode result)
        {
            result = ParseWardrobeConsistencyMode(value);
            return !string.IsNullOrWhiteSpace(value);
        }

}
