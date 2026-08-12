using System.Text.Json.Serialization;

namespace PageToMovie.Adaptation;

/// <summary>
/// Specifies the type of prompt being processed in Adaptation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdaptationPromptKind
{
    BookToFountain = 0,
    CastExtraction = 1,
    ShotPlan = 2,
    SceneBible = 3,
    CharacterDesign = 4,
    Reskin = 5,
    Embellish = 6,
    Trim = 7
}

/// <summary>
/// Specifies the type of adaptation analysis or diagnostic report.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdaptationReportType
{
    Density = 0,
    Quality = 1,
    Analysis = 2,
    Summary = 3,
    Full = 4,
    Diagnostics = 5
}

/// <summary>
/// Format or origin of the imported book text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookImportSourceType
{
    None = 0,
    Fountain = 1,
    Text = 2,
    Pdf = 3,
    Epub = 4,
    Docx = 5
}

/// <summary>
/// Engine or method used to perform OCR / text extraction on source pages.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrEngineType
{
    None = 0,
    PdfPig = 1,
    GrokVision = 2,
    Tesseract = 3,
    BuiltIn = 4
}

/// <summary>
/// Seasonal setting for character wardrobe and visual lock consistency.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterWardrobeSeason
{
    Any = 0,
    Spring = 1,
    Summer = 2,
    Autumn = 3,
    Fall = 4,
    Winter = 5,
    AllSeason = 6
}

/// <summary>
/// Extension methods for Adaptation layer enums.
/// </summary>
public static class AdaptationLayerEnumExtensions
{
    public static AdaptationPromptKind ParseAdaptationPromptKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "cast_extraction" => AdaptationPromptKind.CastExtraction,
                "shot_plan" => AdaptationPromptKind.ShotPlan,
                "scene_bible" => AdaptationPromptKind.SceneBible,
                "character_design" => AdaptationPromptKind.CharacterDesign,
                "reskin" => AdaptationPromptKind.Reskin,
                "embellish" => AdaptationPromptKind.Embellish,
                "trim" => AdaptationPromptKind.Trim,
                _ => AdaptationPromptKind.BookToFountain
            };

    public static AdaptationReportType ParseAdaptationReportType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "density" => AdaptationReportType.Density,
                "quality" => AdaptationReportType.Quality,
                "summary" => AdaptationReportType.Summary,
                "full" => AdaptationReportType.Full,
                "diagnostics" => AdaptationReportType.Diagnostics,
                _ => AdaptationReportType.Analysis
            };

    public static BookImportSourceType ParseBookImportSourceType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "fountain" or "fountain_draft" => BookImportSourceType.Fountain,
                "text" or "txt" => BookImportSourceType.Text,
                "pdf" => BookImportSourceType.Pdf,
                "epub" => BookImportSourceType.Epub,
                "docx" => BookImportSourceType.Docx,
                _ => BookImportSourceType.None
            };

    public static CharacterWardrobeSeason ParseCharacterWardrobeSeason(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "spring" => CharacterWardrobeSeason.Spring,
                "summer" => CharacterWardrobeSeason.Summer,
                "autumn" or "fall" => CharacterWardrobeSeason.Autumn,
                "winter" => CharacterWardrobeSeason.Winter,
                "all_season" or "allseason" or "all" => CharacterWardrobeSeason.AllSeason,
                _ => CharacterWardrobeSeason.Any
            };

    public static OcrEngineType ParseOcrEngineType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "pdfpig" => OcrEngineType.PdfPig,
                "grok_vision" or "grok" or "vision" => OcrEngineType.GrokVision,
                "tesseract" => OcrEngineType.Tesseract,
                "builtin" => OcrEngineType.BuiltIn,
                _ => OcrEngineType.None
            };

    public static string ToApiString(this AdaptationPromptKind kind) => kind switch
        {
            AdaptationPromptKind.BookToFountain => "book_to_fountain",
            AdaptationPromptKind.CastExtraction => "cast_extraction",
            AdaptationPromptKind.ShotPlan => "shot_plan",
            AdaptationPromptKind.SceneBible => "scene_bible",
            AdaptationPromptKind.CharacterDesign => "character_design",
            AdaptationPromptKind.Reskin => "reskin",
            AdaptationPromptKind.Embellish => "embellish",
            AdaptationPromptKind.Trim => "trim",
            _ => "book_to_fountain"
        };

    public static string ToApiString(this AdaptationReportType reportType) => reportType switch
        {
            AdaptationReportType.Density => "density",
            AdaptationReportType.Quality => "quality",
            AdaptationReportType.Analysis => "analysis",
            AdaptationReportType.Summary => "summary",
            AdaptationReportType.Full => "full",
            AdaptationReportType.Diagnostics => "diagnostics",
            _ => "analysis"
        };

    public static string ToApiString(this BookImportSourceType sourceType) => sourceType switch
        {
            BookImportSourceType.Fountain => "fountain",
            BookImportSourceType.Text => "text",
            BookImportSourceType.Pdf => "pdf",
            BookImportSourceType.Epub => "epub",
            BookImportSourceType.Docx => "docx",
            _ => "none"
        };

    public static string ToApiString(this OcrEngineType ocrType) => ocrType switch
        {
            OcrEngineType.PdfPig => "pdfpig",
            OcrEngineType.GrokVision => "grok_vision",
            OcrEngineType.Tesseract => "tesseract",
            OcrEngineType.BuiltIn => "builtin",
            _ => "none"
        };

    public static string ToApiString(this CharacterWardrobeSeason season) => season switch
        {
            CharacterWardrobeSeason.Spring => "spring",
            CharacterWardrobeSeason.Summer => "summer",
            CharacterWardrobeSeason.Autumn or CharacterWardrobeSeason.Fall => "autumn",
            CharacterWardrobeSeason.Winter => "winter",
            CharacterWardrobeSeason.AllSeason => "all_season",
            _ => "any"
        };

}
