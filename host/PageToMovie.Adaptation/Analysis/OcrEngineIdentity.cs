using PageToMovie.Core.Models;

namespace PageToMovie.Adaptation;

/// <summary>
/// Capability-based OCR / book-prepare engine identity. Labels come from the
/// catalog Vision capability — never a compile-time vendor (Grok) route.
/// </summary>
public static class OcrEngineIdentity
{
    /// <summary>Persisted strategy action when book-prepare runs catalog Vision OCR.</summary>
    public const string VisionTranscribeAction = "vision_transcribe";

    /// <summary>Legacy extract_meta / script action. Read as Vision; do not write.</summary>
    public const string LegacyVisionTranscribeAction = "grok_vision_transcribe";

    /// <summary>Persisted text_engine / method label for catalog Vision OCR.</summary>
    public const string VisionEngine = "vision";

    /// <summary>Legacy text_engine / method label. Read as Vision; do not write.</summary>
    public const string LegacyVisionEngine = "grok_vision";

    public static bool IsVisionTranscribeAction(string? action)
    {
        var v = (action ?? "").Trim();
        return v.Equals(VisionTranscribeAction, StringComparison.OrdinalIgnoreCase)
               || v.Equals(LegacyVisionTranscribeAction, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVisionEngine(string? engine) =>
        AdaptationLayerEnumExtensions.ParseOcrEngineType(engine) == OcrEngineType.Vision;

    /// <summary>
    /// True when the loaded catalog publishes a Vision default (or an enabled Vision model).
    /// Product code must not invent a vendor fallback when this is false.
    /// </summary>
    public static bool CatalogOffersVision() =>
        !string.IsNullOrWhiteSpace(
            SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision));

    /// <summary>Catalog Vision default model id, or throw. Never invents a Grok/other id.</summary>
    public static string RequireCatalogVisionModelId() =>
        SupportedModelCatalog.RequireDefaultModelIdForCapability(ModelCapability.Vision);
}
