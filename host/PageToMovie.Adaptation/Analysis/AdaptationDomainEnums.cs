using System.Text.Json.Serialization;

namespace PageToMovie.Adaptation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenplayFormatStandard
{
    Fountain,
    FinalDraft,
    Celtx,
    Plaintext,
    PdfImport
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TitlePageMetadataKey
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
public enum SceneHeadingPrefix
{
    Int,
    Ext,
    IntExt,
    Est,
    Ie
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterNameCasing
{
    Uppercase,
    TitleCase,
    Original
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DialogueParentheticalType
{
    DeliveryTone,
    ActionInstruction,
    Pause,
    TargetSpeaker,
    Generic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualDialoguePosition
{
    None,
    Left,
    Right
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PageBreakKind
{
    Explicit,
    Forced,
    Natural
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SectionHeaderLevel
{
    Level1,
    Level2,
    Level3,
    Level4,
    Level5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FountainSyntaxTokenKind
{
    TitlePage,
    SceneHeading,
    Action,
    Character,
    Dialogue,
    Parenthetical,
    Transition,
    CenteredText,
    PageBreak,
    SectionHeader,
    Synopses,
    BoneyardComment,
    Note
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenplayAnalysisMetric
{
    WordCount,
    SceneCount,
    DialogueRatio,
    PacingScore,
    CastMemberCount,
    ActionDensity
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookGenreCategory
{
    ChildrensPictureBook,
    YoungAdult,
    Fantasy,
    SciFi,
    Mystery,
    Drama,
    NonFiction,
    GeneralFiction
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NarrativePacingType
{
    Slow,
    Balanced,
    Fast,
    Frantic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtagonistArchetype
{
    Hero,
    Everyman,
    AntiHero,
    Innocent,
    Explorer,
    Companion
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdaptationConfidenceLevel
{
    Low,
    Medium,
    High,
    Verified
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextSanitizerRule
{
    StripBoneyards,
    NormalizeWhitespace,
    FixBrokenDialogue,
    CleanSmartQuotes,
    SanitizeCharacterNames
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LineEndingStyle
{
    Lf,
    Crlf,
    Cr
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterRoleImportance
{
    Lead,
    Supporting,
    Background,
    Narrator
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DialogueEmotionTag
{
    Neutral,
    Joyful,
    Angry,
    Sad,
    Fearful,
    Surprised,
    Whispering,
    Shouting
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SceneTransitionEffect
{
    CutTo,
    FadeIn,
    FadeOut,
    DissolveTo,
    SmashCutTo,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Stage1PipelineStep
{
    BookImport,
    TextSanitization,
    FountainConversion,
    CastExtraction,
    SceneSegmentation,
    PacingAnalysis,
    Validation
}

public static class AdaptationDomainEnumExtensions
{
    public static string ToApiString(this ScreenplayFormatStandard standard) => standard switch
    {
        ScreenplayFormatStandard.FinalDraft => "final_draft",
        ScreenplayFormatStandard.Celtx => "celtx",
        ScreenplayFormatStandard.Plaintext => "plaintext",
        ScreenplayFormatStandard.PdfImport => "pdf_import",
        _ => "fountain"
    };

    public static ScreenplayFormatStandard ParseScreenplayFormatStandard(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "final_draft" or "finaldraft" or "fdx" => ScreenplayFormatStandard.FinalDraft,
            "celtx" => ScreenplayFormatStandard.Celtx,
            "plaintext" or "txt" => ScreenplayFormatStandard.Plaintext,
            "pdf_import" or "pdf" => ScreenplayFormatStandard.PdfImport,
            _ => ScreenplayFormatStandard.Fountain
        };

    public static string ToApiString(this TitlePageMetadataKey key) => key switch
    {
        TitlePageMetadataKey.Title => "title",
        TitlePageMetadataKey.Credit => "credit",
        TitlePageMetadataKey.Author => "author",
        TitlePageMetadataKey.Source => "source",
        TitlePageMetadataKey.Notes => "notes",
        TitlePageMetadataKey.DraftDate => "draft_date",
        TitlePageMetadataKey.Contact => "contact",
        TitlePageMetadataKey.Copyright => "copyright",
        _ => "title"
    };

    public static TitlePageMetadataKey ParseTitlePageMetadataKey(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "credit" => TitlePageMetadataKey.Credit,
            "author" => TitlePageMetadataKey.Author,
            "source" => TitlePageMetadataKey.Source,
            "notes" => TitlePageMetadataKey.Notes,
            "draft_date" or "draftdate" or "date" => TitlePageMetadataKey.DraftDate,
            "contact" => TitlePageMetadataKey.Contact,
            "copyright" => TitlePageMetadataKey.Copyright,
            _ => TitlePageMetadataKey.Title
        };

    public static string ToApiString(this SceneHeadingPrefix prefix) => prefix switch
    {
        SceneHeadingPrefix.Int => "int",
        SceneHeadingPrefix.Ext => "ext",
        SceneHeadingPrefix.IntExt => "int_ext",
        SceneHeadingPrefix.Est => "est",
        SceneHeadingPrefix.Ie => "ie",
        _ => "int"
    };

    public static SceneHeadingPrefix ParseSceneHeadingPrefix(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "ext" or "ext." => SceneHeadingPrefix.Ext,
            "int_ext" or "int/ext" or "int./ext." or "int. / ext." => SceneHeadingPrefix.IntExt,
            "est" or "est." => SceneHeadingPrefix.Est,
            "ie" or "i/e" => SceneHeadingPrefix.Ie,
            _ => SceneHeadingPrefix.Int
        };

    public static string ToApiString(this CharacterNameCasing casing) => casing switch
    {
        CharacterNameCasing.TitleCase => "titlecase",
        CharacterNameCasing.Original => "original",
        _ => "uppercase"
    };

    public static CharacterNameCasing ParseCharacterNameCasing(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "titlecase" or "title_case" => CharacterNameCasing.TitleCase,
            "original" => CharacterNameCasing.Original,
            _ => CharacterNameCasing.Uppercase
        };

    public static string ToApiString(this DialogueParentheticalType type) => type switch
    {
        DialogueParentheticalType.DeliveryTone => "delivery_tone",
        DialogueParentheticalType.ActionInstruction => "action_instruction",
        DialogueParentheticalType.Pause => "pause",
        DialogueParentheticalType.TargetSpeaker => "target_speaker",
        DialogueParentheticalType.Generic => "generic",
        _ => "delivery_tone"
    };

    public static DialogueParentheticalType ParseDialogueParentheticalType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "action_instruction" or "action" => DialogueParentheticalType.ActionInstruction,
            "pause" => DialogueParentheticalType.Pause,
            "target_speaker" or "target" => DialogueParentheticalType.TargetSpeaker,
            "generic" => DialogueParentheticalType.Generic,
            _ => DialogueParentheticalType.DeliveryTone
        };

    public static string ToApiString(this DualDialoguePosition pos) => pos switch
    {
        DualDialoguePosition.Left => "left",
        DualDialoguePosition.Right => "right",
        _ => "none"
    };

    public static DualDialoguePosition ParseDualDialoguePosition(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "left" or "^" => DualDialoguePosition.Left,
            "right" => DualDialoguePosition.Right,
            _ => DualDialoguePosition.None
        };

    public static string ToApiString(this PageBreakKind kind) => kind switch
    {
        PageBreakKind.Explicit => "explicit",
        PageBreakKind.Forced => "forced",
        PageBreakKind.Natural => "natural",
        _ => "explicit"
    };

    public static PageBreakKind ParsePageBreakKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "forced" => PageBreakKind.Forced,
            "natural" => PageBreakKind.Natural,
            _ => PageBreakKind.Explicit
        };

    public static string ToApiString(this SectionHeaderLevel level) => level switch
    {
        SectionHeaderLevel.Level1 => "level1",
        SectionHeaderLevel.Level2 => "level2",
        SectionHeaderLevel.Level3 => "level3",
        SectionHeaderLevel.Level4 => "level4",
        SectionHeaderLevel.Level5 => "level5",
        _ => "level1"
    };

    public static SectionHeaderLevel ParseSectionHeaderLevel(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "level2" or "##" or "2" => SectionHeaderLevel.Level2,
            "level3" or "###" or "3" => SectionHeaderLevel.Level3,
            "level4" or "####" or "4" => SectionHeaderLevel.Level4,
            "level5" or "#####" or "5" => SectionHeaderLevel.Level5,
            _ => SectionHeaderLevel.Level1
        };

    public static string ToApiString(this FountainSyntaxTokenKind kind) => kind switch
    {
        FountainSyntaxTokenKind.TitlePage => "title_page",
        FountainSyntaxTokenKind.SceneHeading => "scene_heading",
        FountainSyntaxTokenKind.Action => "action",
        FountainSyntaxTokenKind.Character => "character",
        FountainSyntaxTokenKind.Dialogue => "dialogue",
        FountainSyntaxTokenKind.Parenthetical => "parenthetical",
        FountainSyntaxTokenKind.Transition => "transition",
        FountainSyntaxTokenKind.CenteredText => "centered_text",
        FountainSyntaxTokenKind.PageBreak => "page_break",
        FountainSyntaxTokenKind.SectionHeader => "section_header",
        FountainSyntaxTokenKind.Synopses => "synopses",
        FountainSyntaxTokenKind.BoneyardComment => "boneyard_comment",
        FountainSyntaxTokenKind.Note => "note",
        _ => "action"
    };

    public static FountainSyntaxTokenKind ParseFountainSyntaxTokenKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "title_page" or "titlepage" => FountainSyntaxTokenKind.TitlePage,
            "scene_heading" or "sceneheading" or "heading" => FountainSyntaxTokenKind.SceneHeading,
            "character" => FountainSyntaxTokenKind.Character,
            "dialogue" => FountainSyntaxTokenKind.Dialogue,
            "parenthetical" => FountainSyntaxTokenKind.Parenthetical,
            "transition" => FountainSyntaxTokenKind.Transition,
            "centered_text" or "centered" => FountainSyntaxTokenKind.CenteredText,
            "page_break" or "pagebreak" => FountainSyntaxTokenKind.PageBreak,
            "section_header" or "sectionheader" or "section" => FountainSyntaxTokenKind.SectionHeader,
            "synopses" or "synopsis" => FountainSyntaxTokenKind.Synopses,
            "boneyard_comment" or "boneyard" => FountainSyntaxTokenKind.BoneyardComment,
            "note" => FountainSyntaxTokenKind.Note,
            _ => FountainSyntaxTokenKind.Action
        };

    public static string ToApiString(this ScreenplayAnalysisMetric metric) => metric switch
    {
        ScreenplayAnalysisMetric.WordCount => "word_count",
        ScreenplayAnalysisMetric.SceneCount => "scene_count",
        ScreenplayAnalysisMetric.DialogueRatio => "dialogue_ratio",
        ScreenplayAnalysisMetric.PacingScore => "pacing_score",
        ScreenplayAnalysisMetric.CastMemberCount => "cast_member_count",
        ScreenplayAnalysisMetric.ActionDensity => "action_density",
        _ => "word_count"
    };

    public static ScreenplayAnalysisMetric ParseScreenplayAnalysisMetric(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "scene_count" or "scenecount" => ScreenplayAnalysisMetric.SceneCount,
            "dialogue_ratio" or "dialogueratio" => ScreenplayAnalysisMetric.DialogueRatio,
            "pacing_score" or "pacingscore" => ScreenplayAnalysisMetric.PacingScore,
            "cast_member_count" or "castmembercount" => ScreenplayAnalysisMetric.CastMemberCount,
            "action_density" or "actiondensity" => ScreenplayAnalysisMetric.ActionDensity,
            _ => ScreenplayAnalysisMetric.WordCount
        };

    public static string ToApiString(this BookGenreCategory genre) => genre switch
    {
        BookGenreCategory.ChildrensPictureBook => "childrens_picture_book",
        BookGenreCategory.YoungAdult => "young_adult",
        BookGenreCategory.Fantasy => "fantasy",
        BookGenreCategory.SciFi => "sci_fi",
        BookGenreCategory.Mystery => "mystery",
        BookGenreCategory.Drama => "drama",
        BookGenreCategory.NonFiction => "non_fiction",
        BookGenreCategory.GeneralFiction => "general_fiction",
        _ => "general_fiction"
    };

    public static BookGenreCategory ParseBookGenreCategory(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "childrens_picture_book" or "childrens" or "picturebook" => BookGenreCategory.ChildrensPictureBook,
            "young_adult" or "ya" => BookGenreCategory.YoungAdult,
            "fantasy" => BookGenreCategory.Fantasy,
            "sci_fi" or "scifi" or "science_fiction" => BookGenreCategory.SciFi,
            "mystery" => BookGenreCategory.Mystery,
            "drama" => BookGenreCategory.Drama,
            "non_fiction" or "nonfiction" => BookGenreCategory.NonFiction,
            _ => BookGenreCategory.GeneralFiction
        };

    public static string ToApiString(this NarrativePacingType pacing) => pacing switch
    {
        NarrativePacingType.Slow => "slow",
        NarrativePacingType.Balanced => "balanced",
        NarrativePacingType.Fast => "fast",
        NarrativePacingType.Frantic => "frantic",
        _ => "balanced"
    };

    public static NarrativePacingType ParseNarrativePacingType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "slow" => NarrativePacingType.Slow,
            "fast" => NarrativePacingType.Fast,
            "frantic" => NarrativePacingType.Frantic,
            _ => NarrativePacingType.Balanced
        };

    public static string ToApiString(this ProtagonistArchetype archetype) => archetype switch
    {
        ProtagonistArchetype.Hero => "hero",
        ProtagonistArchetype.Everyman => "everyman",
        ProtagonistArchetype.AntiHero => "anti_hero",
        ProtagonistArchetype.Innocent => "innocent",
        ProtagonistArchetype.Explorer => "explorer",
        ProtagonistArchetype.Companion => "companion",
        _ => "hero"
    };

    public static ProtagonistArchetype ParseProtagonistArchetype(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "everyman" => ProtagonistArchetype.Everyman,
            "anti_hero" or "antihero" => ProtagonistArchetype.AntiHero,
            "innocent" => ProtagonistArchetype.Innocent,
            "explorer" => ProtagonistArchetype.Explorer,
            "companion" => ProtagonistArchetype.Companion,
            _ => ProtagonistArchetype.Hero
        };

    public static string ToApiString(this AdaptationConfidenceLevel level) => level switch
    {
        AdaptationConfidenceLevel.Low => "low",
        AdaptationConfidenceLevel.Medium => "medium",
        AdaptationConfidenceLevel.High => "high",
        AdaptationConfidenceLevel.Verified => "verified",
        _ => "medium"
    };

    public static AdaptationConfidenceLevel ParseAdaptationConfidenceLevel(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "low" => AdaptationConfidenceLevel.Low,
            "high" => AdaptationConfidenceLevel.High,
            "verified" => AdaptationConfidenceLevel.Verified,
            _ => AdaptationConfidenceLevel.Medium
        };

    public static string ToApiString(this TextSanitizerRule rule) => rule switch
    {
        TextSanitizerRule.StripBoneyards => "strip_boneyards",
        TextSanitizerRule.NormalizeWhitespace => "normalize_whitespace",
        TextSanitizerRule.FixBrokenDialogue => "fix_broken_dialogue",
        TextSanitizerRule.CleanSmartQuotes => "clean_smart_quotes",
        TextSanitizerRule.SanitizeCharacterNames => "sanitize_character_names",
        _ => "normalize_whitespace"
    };

    public static TextSanitizerRule ParseTextSanitizerRule(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "strip_boneyards" or "stripboneyards" => TextSanitizerRule.StripBoneyards,
            "fix_broken_dialogue" or "fixbrokendialogue" => TextSanitizerRule.FixBrokenDialogue,
            "clean_smart_quotes" or "cleansmartquotes" => TextSanitizerRule.CleanSmartQuotes,
            "sanitize_character_names" or "sanitizecharacternames" => TextSanitizerRule.SanitizeCharacterNames,
            _ => TextSanitizerRule.NormalizeWhitespace
        };

    public static string ToApiString(this LineEndingStyle style) => style switch
    {
        LineEndingStyle.Crlf => "crlf",
        LineEndingStyle.Cr => "cr",
        _ => "lf"
    };

    public static LineEndingStyle ParseLineEndingStyle(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "crlf" or "\r\n" => LineEndingStyle.Crlf,
            "cr" or "\r" => LineEndingStyle.Cr,
            _ => LineEndingStyle.Lf
        };

    public static string ToApiString(this CharacterRoleImportance role) => role switch
    {
        CharacterRoleImportance.Lead => "lead",
        CharacterRoleImportance.Supporting => "supporting",
        CharacterRoleImportance.Background => "background",
        CharacterRoleImportance.Narrator => "narrator",
        _ => "supporting"
    };

    public static CharacterRoleImportance ParseCharacterRoleImportance(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "lead" or "hero" or "main" => CharacterRoleImportance.Lead,
            "background" or "extras" => CharacterRoleImportance.Background,
            "narrator" => CharacterRoleImportance.Narrator,
            _ => CharacterRoleImportance.Supporting
        };

    public static string ToApiString(this DialogueEmotionTag emotion) => emotion switch
    {
        DialogueEmotionTag.Joyful => "joyful",
        DialogueEmotionTag.Angry => "angry",
        DialogueEmotionTag.Sad => "sad",
        DialogueEmotionTag.Fearful => "fearful",
        DialogueEmotionTag.Surprised => "surprised",
        DialogueEmotionTag.Whispering => "whispering",
        DialogueEmotionTag.Shouting => "shouting",
        _ => "neutral"
    };

    public static DialogueEmotionTag ParseDialogueEmotionTag(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "joyful" or "happy" => DialogueEmotionTag.Joyful,
            "angry" or "mad" => DialogueEmotionTag.Angry,
            "sad" => DialogueEmotionTag.Sad,
            "fearful" or "scared" => DialogueEmotionTag.Fearful,
            "surprised" => DialogueEmotionTag.Surprised,
            "whispering" or "whisper" => DialogueEmotionTag.Whispering,
            "shouting" or "shout" or "yelling" => DialogueEmotionTag.Shouting,
            _ => DialogueEmotionTag.Neutral
        };

    public static string ToApiString(this SceneTransitionEffect effect) => effect switch
    {
        SceneTransitionEffect.CutTo => "cut_to",
        SceneTransitionEffect.FadeIn => "fade_in",
        SceneTransitionEffect.FadeOut => "fade_out",
        SceneTransitionEffect.DissolveTo => "dissolve_to",
        SceneTransitionEffect.SmashCutTo => "smash_cut_to",
        _ => "none"
    };

    public static SceneTransitionEffect ParseSceneTransitionEffect(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "cut_to" or "cut" => SceneTransitionEffect.CutTo,
            "fade_in" or "fadein" => SceneTransitionEffect.FadeIn,
            "fade_out" or "fadeout" => SceneTransitionEffect.FadeOut,
            "dissolve_to" or "dissolve" => SceneTransitionEffect.DissolveTo,
            "smash_cut_to" or "smashcut" => SceneTransitionEffect.SmashCutTo,
            _ => SceneTransitionEffect.None
        };

    public static string ToApiString(this Stage1PipelineStep step) => step switch
    {
        Stage1PipelineStep.BookImport => "book_import",
        Stage1PipelineStep.TextSanitization => "text_sanitization",
        Stage1PipelineStep.FountainConversion => "fountain_conversion",
        Stage1PipelineStep.CastExtraction => "cast_extraction",
        Stage1PipelineStep.SceneSegmentation => "scene_segmentation",
        Stage1PipelineStep.PacingAnalysis => "pacing_analysis",
        Stage1PipelineStep.Validation => "validation",
        _ => "book_import"
    };

    public static Stage1PipelineStep ParseStage1PipelineStep(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "text_sanitization" or "sanitization" => Stage1PipelineStep.TextSanitization,
            "fountain_conversion" or "fountain" => Stage1PipelineStep.FountainConversion,
            "cast_extraction" or "cast" => Stage1PipelineStep.CastExtraction,
            "scene_segmentation" or "segmentation" => Stage1PipelineStep.SceneSegmentation,
            "pacing_analysis" or "pacing" => Stage1PipelineStep.PacingAnalysis,
            "validation" => Stage1PipelineStep.Validation,
            _ => Stage1PipelineStep.BookImport
        };
}
