using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Fakes;
using Xunit;

namespace PageToMovie.Tests;

// See CatalogSerialCollection in SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public class SupportsVocalsCatalogTests
{
    public SupportsVocalsCatalogTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Theory]
    [InlineData("suno-v5-5", true)]
    [InlineData("aimusicapi-suno", true)]
    [InlineData("elevenlabs-music", true)]
    [InlineData("fal-ai/stable-audio-2.0", false)]
    [InlineData("fal-ai/musicgen", false)]
    public void Catalog_SupportsVocals_matches_expectation(string modelId, bool expected)
    {
        var e = SupportedModelCatalog.Find(modelId, ModelCapability.Audio);
        Assert.NotNull(e);
        Assert.Equal(expected, e!.SupportsVocals);
    }

    [Fact]
    public void Dto_round_trips_SupportsVocals()
    {
        var e = SupportedModelCatalog.Find("suno-v5-5", ModelCapability.Audio)!;
        var dto = SupportedModelCatalog.ToDto(e);
        Assert.True(dto.SupportsVocals);
        var back = SupportedModelCatalog.FromDto(dto);
        Assert.True(back.SupportsVocals);
    }

    [Fact]
    public void Image_enabled_models_have_maxReferenceImages()
    {
        var images = SupportedModelCatalog.ForCapability(ModelCapability.Image, enabledOnly: true);
        Assert.NotEmpty(images);
        Assert.All(images, e => Assert.True(
            e.MaxReferenceImages is not null,
            $"{e.Id} missing maxReferenceImages"));
    }

    [Fact]
    public void ImageApiLimits_uses_catalog_not_provider_for_gemini()
    {
        var n = ImageApiLimits.MaxReferenceImages("grok", "gemini-2.5-pro-image");
        Assert.Equal(14, n); // catalog value, even if provider string is wrong
    }

    [Fact]
    public void ImageApiLimits_flux_from_catalog()
    {
        var n = ImageApiLimits.MaxReferenceImages(null, "fal-ai/flux/dev");
        Assert.Equal(1, n);
    }

    [Fact]
    public void Unknown_image_model_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ImageApiLimits.MaxReferenceImages(null, "not-a-real-image-model"));
        Assert.Contains("not in models_catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_image_model_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ImageApiLimits.MaxReferenceImages(null, null));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Chat_model_as_image_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ImageApiLimits.MaxReferenceImages(null, "grok-4.5"));
        Assert.Contains("not Image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
