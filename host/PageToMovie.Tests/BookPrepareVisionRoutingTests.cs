using PageToMovie.Adaptation;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Book-prepare / OCR engine identity is catalog Vision — not a compile-time Grok route.
/// </summary>
[Collection("catalog-serial")]
public class BookPrepareVisionRoutingTests : IDisposable
{
    public BookPrepareVisionRoutingTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    public void Dispose()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    const string LabVisionCatalogJson = """
        {
          "capabilities": [
            { "id": "vision", "displayName": "Image Vision & OCR", "defaultModelId": "lab-vision-ocr" }
          ],
          "models": [
            {
              "id": "lab-vision-ocr",
              "displayName": "Lab Vision OCR",
              "capability": "Vision",
              "provider": "Fake",
              "enabled": true
            }
          ]
        }
        """;

    const string NoVisionCatalogJson = """
        {
          "capabilities": [
            { "id": "chat", "displayName": "Script", "defaultModelId": "lab-chat" }
          ],
          "models": [
            {
              "id": "lab-chat",
              "displayName": "Lab Chat",
              "capability": "Chat",
              "provider": "Fake",
              "enabled": true
            }
          ]
        }
        """;

    [Theory]
    [InlineData("vision")]
    [InlineData("grok_vision")]
    [InlineData("grok")]
    [InlineData("GrokVision")]
    public void ParseOcrEngineType_maps_capability_and_legacy_aliases_to_Vision(string raw)
    {
        Assert.Equal(OcrEngineType.Vision, AdaptationLayerEnumExtensions.ParseOcrEngineType(raw));
    }

    [Fact]
    public void ToApiString_Vision_is_capability_label_not_vendor()
    {
        Assert.Equal("vision", OcrEngineType.Vision.ToApiString());
        Assert.Equal(OcrEngineIdentity.VisionEngine, OcrEngineType.Vision.ToApiString());
        Assert.NotEqual("grok_vision", OcrEngineType.Vision.ToApiString());
    }

    [Theory]
    [InlineData("vision_transcribe", true)]
    [InlineData("grok_vision_transcribe", true)]
    [InlineData("use_embedded_text", false)]
    [InlineData("need_xai_for_vision", false)]
    public void IsVisionTranscribeAction_accepts_legacy_alias(string action, bool expected)
    {
        Assert.Equal(expected, OcrEngineIdentity.IsVisionTranscribeAction(action));
    }

    [Theory]
    [InlineData("vision", true)]
    [InlineData("grok_vision", true)]
    [InlineData("pdfpig", false)]
    [InlineData("existing_book_full", false)]
    public void IsVisionEngine_accepts_legacy_alias(string engine, bool expected)
    {
        Assert.Equal(expected, OcrEngineIdentity.IsVisionEngine(engine));
    }

    [Theory]
    [InlineData("vision", TextEngineKind.Vision)]
    [InlineData("grok_vision", TextEngineKind.Vision)]
    [InlineData("PdfPigGrok", TextEngineKind.Vision)]
    [InlineData("pdfpig", TextEngineKind.PdfPig)]
    [InlineData("text", TextEngineKind.Text)]
    public void TextEngineKind_TryParse_maps_legacy_vision_aliases(string raw, TextEngineKind expected)
    {
        Assert.Equal(expected, TextEngineKindExtensions.TryParse(raw));
    }

    [Fact]
    public void Catalog_only_non_grok_vision_default_selects_vision_transcribe()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(LabVisionCatalogJson));
        Assert.Equal("lab-vision-ocr", OcrEngineIdentity.RequireCatalogVisionModelId());
        Assert.True(OcrEngineIdentity.CatalogOffersVision());
        Assert.True(BookPrepareService.VisionOcrAvailable(visionClientConfigured: true));

        var strategy = BookPrepareService.DecidePrepareStrategy(
            PoorPictureBook(),
            hasImages: true,
            visionAvailable: BookPrepareService.VisionOcrAvailable(true),
            forceVision: false,
            autoVision: true);

        Assert.Equal(OcrEngineIdentity.VisionTranscribeAction, strategy.Action);
        Assert.DoesNotContain("grok", strategy.Action, StringComparison.OrdinalIgnoreCase);
        Assert.True(OcrEngineIdentity.IsVisionTranscribeAction(strategy.Action));
    }

    [Fact]
    public void Catalog_without_vision_does_not_select_transcribe_even_if_client_configured()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(NoVisionCatalogJson));
        Assert.False(OcrEngineIdentity.CatalogOffersVision());
        Assert.False(BookPrepareService.VisionOcrAvailable(visionClientConfigured: true));

        var strategy = BookPrepareService.DecidePrepareStrategy(
            PoorPictureBook(),
            hasImages: true,
            visionAvailable: BookPrepareService.VisionOcrAvailable(true),
            forceVision: false,
            autoVision: true);

        Assert.False(OcrEngineIdentity.IsVisionTranscribeAction(strategy.Action));
        Assert.Equal("need_xai_for_vision", strategy.Action);
    }

    [Fact]
    public void Force_vision_uses_capability_action_not_vendor_literal()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(LabVisionCatalogJson));
        var clean = new BookTextAnalysis
        {
            TextQuality = TextQuality.Good,
            TextDensity = TextDensity.Normal,
            BookKind = BookKind.Short,
            TextWords = 200,
            GarbageScore = 0.05,
        };

        var strategy = BookPrepareService.DecidePrepareStrategy(
            clean,
            hasImages: true,
            visionAvailable: true,
            forceVision: true,
            autoVision: true);

        Assert.Equal(OcrEngineIdentity.VisionTranscribeAction, strategy.Action);
    }

    [Fact]
    public void Product_catalog_vision_default_is_resolved_not_hardcoded()
    {
        SupportedModelCatalog.ReloadCatalog();
        var fromCatalog = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision);
        Assert.False(string.IsNullOrWhiteSpace(fromCatalog));
        Assert.Equal(fromCatalog, OcrEngineIdentity.RequireCatalogVisionModelId());
        Assert.True(OcrEngineIdentity.CatalogOffersVision());
    }

    static BookTextAnalysis PoorPictureBook() => new()
    {
        TextQuality = TextQuality.Poor,
        TextDensity = TextDensity.Sparse,
        BookKind = BookKind.PictureBook,
        TextWords = 12,
        GarbageScore = 0.6,
    };
}
