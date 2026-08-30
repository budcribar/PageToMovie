using System.Text.Json.Serialization;

namespace PageToMovie.Adaptation;

/// <summary>
/// Engine or method used to perform OCR / text extraction on source pages.
/// Values are capability-based (Vision vs extract/heuristic). Persisted
/// <c>grok_vision</c> is a legacy alias for <see cref="Vision"/>, not a vendor route.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrEngineType
{
    None = 0,
    PdfPig = 1,
    /// <summary>Catalog Vision capability (OCR / transcribe). Numeric 2 matches legacy GrokVision.</summary>
    Vision = 2,
    Tesseract = 3,
    BuiltIn = 4
}

/// <summary>
/// Extension methods for Adaptation layer enums.
/// </summary>
public static class AdaptationLayerEnumExtensions
{
    public static OcrEngineType ParseOcrEngineType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "pdfpig" => OcrEngineType.PdfPig,
            // Capability label plus legacy vendor aliases from extract_meta / older jobs.
            "vision" or "grok_vision" or "grok" or "grokvision" => OcrEngineType.Vision,
            "tesseract" => OcrEngineType.Tesseract,
            "builtin" => OcrEngineType.BuiltIn,
            _ => OcrEngineType.None
        };

    public static string ToApiString(this OcrEngineType ocrType) => ocrType switch
    {
        OcrEngineType.PdfPig => "pdfpig",
        OcrEngineType.Vision => OcrEngineIdentity.VisionEngine,
        OcrEngineType.Tesseract => "tesseract",
        OcrEngineType.BuiltIn => "builtin",
        _ => "none"
    };
}
