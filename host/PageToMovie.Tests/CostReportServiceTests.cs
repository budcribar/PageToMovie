using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Covers the video base-fee (flat per-video, e.g. Hunyuan/Wan) pricing and the reference-image /
/// extend-per-second pricing add-ons in <see cref="CostReportService"/>: catalog-sourced values
/// must win when a model publishes them, and the small estimated fallback constants must apply
/// (with the right "pricing_source" flags) when the catalog has no verified number — which, as of
/// 2026-08, is every enabled video model including xAI's grok-imagine-video (no separate line item
/// published on docs.x.ai/developers/pricing for reference images or video-extend).
/// </summary>
// Swaps in a reduced synthetic catalog mid-test (restored in Dispose, but the window while it's
// active must not overlap another test reading the real catalog on another thread). See
// CatalogSerialCollection in SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public sealed class CostReportServiceTests : IDisposable
{
    public CostReportServiceTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    public void Dispose()
    {
        // Undo any TryLoadFromJson swap so later test classes see the real on-disk catalog again.
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public async Task GetReportAsync_NoModelConfigured_FailsFastWithClearConfigurationMessage()
    {
        // A project with no model set (no pipeline_config.json 'model_name') must fail fast with an
        // actionable message pointing to the Configuration page — not a cryptic downstream rate-table
        // error. There is no default model; cost rates come only from the catalog for a chosen model.
        var store = TestProjects.CreateStore("cost_nomodel_", out var root);
        try
        {
            var costs = new CostReportService(store);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => costs.GetReportAsync("Demo"));
            Assert.Contains("Configuration page", ex.Message);
            Assert.DoesNotContain("video_input_image", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void BuildVideoBaseRateTable_ReturnsEmptyForPerSecondOnlyModel()
    {
        // Grok/Veo are genuinely priced per second — no flat fee, no guessed base cost.
        var grok = SupportedModelCatalog.ResolveOrDefault("grok-imagine-video", ModelCapability.Video);
        var table = CostReportService.BuildVideoBaseRateTable(grok);
        Assert.Empty(table);
    }

    [Theory]
    [InlineData("hunyuan-video", "720p", 0.40)]
    [InlineData("fal-ai/wan-2.1", "480p", 0.20)]
    [InlineData("fal-ai/wan-2.1", "720p", 0.40)]
    public void BuildVideoBaseRateTable_ReturnsRealFlatFeeForFrameCountBasedModels(
        string modelId, string resolution, double expectedBase)
    {
        var entry = SupportedModelCatalog.ResolveOrDefault(modelId, ModelCapability.Video);
        var table = CostReportService.BuildVideoBaseRateTable(entry);
        Assert.Equal(expectedBase, table[resolution]);
    }

    [Fact]
    public void RatesFromModels_PriceVideo_UsesFlatFeeNotPerSecondForHunyuan()
    {
        var rates = CostReportService.RatesFromModels("hunyuan-video", "grok-imagine-image-quality");
        // A 5s and an 8s clip must cost the SAME — Hunyuan bills per generation, not per second.
        var five = CostReportService.PriceVideo(5, "720p", rates, hasRef: false, isExtend: false, attempts: 1);
        var eight = CostReportService.PriceVideo(8, "720p", rates, hasRef: false, isExtend: false, attempts: 1);
        Assert.Equal(0.40, five.Usd);
        Assert.Equal(0.40, eight.Usd);
    }

    [Fact]
    public void RatesFromModels_PriceVideo_StillScalesWithDurationForGrok()
    {
        // Grok is genuinely per-second — a longer clip must cost more, unlike Hunyuan/Wan.
        var rates = CostReportService.RatesFromModels("grok-imagine-video", "grok-imagine-image-quality");
        var five = CostReportService.PriceVideo(5, "720p", rates, hasRef: false, isExtend: false, attempts: 1);
        var eight = CostReportService.PriceVideo(8, "720p", rates, hasRef: false, isExtend: false, attempts: 1);
        Assert.True(eight.Usd > five.Usd);
    }

    private const string SyntheticCatalogJson = """
    {
      "models": [
        {
          "id": "test-video-with-addons",
          "displayName": "Test Video With Addons",
          "capability": "Video",
          "provider": "Xai",
          "apiBase": "https://api.x.ai/v1",
          "endpointPath": "videos/generations",
          "requiredEnvKeys": ["XAI_API_KEY"],
          "enabled": true,
          "supportsVideoContinue": true,
          "videoCostPerSecondByResolution": {"480p": 0.05, "720p": 0.07, "1080p": 0.25},
          "videoReferenceImageCost": 0.003,
          "videoExtendCostPerSecond": 0.015
        },
        {
          "id": "test-video-no-addons",
          "displayName": "Test Video No Addons",
          "capability": "Video",
          "provider": "Xai",
          "apiBase": "https://api.x.ai/v1",
          "endpointPath": "videos/generations",
          "requiredEnvKeys": ["XAI_API_KEY"],
          "enabled": true,
          "supportsVideoContinue": true,
          "videoCostPerSecondByResolution": {"480p": 0.05, "720p": 0.07, "1080p": 0.25},
          "videoReferenceImageCost": 0,
          "videoExtendCostPerSecond": 0.07
        },
        {
          "id": "test-image",
          "displayName": "Test Image",
          "capability": "Image",
          "provider": "Xai",
          "apiBase": "https://api.x.ai/v1",
          "endpointPath": "images/generations",
          "requiredEnvKeys": ["XAI_API_KEY"],
          "enabled": true,
          "imageCostPerImage": 0.05
        }
      ]
    }
    """;

    [Fact]
    public void RatesFromModels_prefers_catalog_ref_image_and_extend_cost_when_published()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(SyntheticCatalogJson));

        var rates = CostReportService.RatesFromModels("test-video-with-addons", "test-image");

        Assert.Equal(0.003, Assert.IsType<double>(rates["video_input_image"]));
        Assert.Equal("model_catalog", rates["video_input_image_source"]);
        Assert.Equal(0.015, Assert.IsType<double>(rates["video_input_per_sec"]));
        Assert.Equal("model_catalog", rates["video_input_per_sec_source"]);
        Assert.Equal("model_catalog", rates["video_pricing_source"]);
    }

    [Fact]
    public void RatesFromModels_throws_when_continue_model_missing_extend_cost()
    {
        const string incomplete = """
        {
          "models": [
            {
              "id": "bad-video",
              "displayName": "Bad",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "supportsVideoContinue": true,
              "videoCostPerSecondByResolution": {"720p": 0.07},
              "videoReferenceImageCost": 0
            },
            {
              "id": "test-image",
              "displayName": "Img",
              "capability": "Image",
              "provider": "Xai",
              "enabled": true,
              "imageCostPerImage": 0.02
            }
          ]
        }
        """;
        Assert.True(SupportedModelCatalog.TryLoadFromJson(incomplete));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CostReportService.RatesFromModels("bad-video", "test-image"));
        Assert.Contains("videoExtendCostPerSecond", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PriceVideo_uses_catalog_ref_image_and_extend_cost_in_the_math()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(SyntheticCatalogJson));
        var rates = CostReportService.RatesFromModels("test-video-with-addons", "test-image");

        var priced = CostReportService.PriceVideo(
            durationSec: 10,
            resolution: "720p",
            rates: rates,
            hasRef: true,
            isExtend: true,
            attempts: 1);

        Assert.Equal(0.003, priced.RefImg);
        Assert.Equal(0.15, priced.ExtendIn); // 10 sec * $0.015/sec catalog extend rate * 1 attempt
    }

    [Fact]
    public void Live_grok_imagine_video_addon_costs_come_from_catalog_not_engine_constants()
    {
        // xAI does not publish separate ref/extend line items; catalog stores explicit values
        // (0 ref fee; extend planning rate) with pricingNotes — never Engine Fallback* dollars.
        var video = SupportedModelCatalog.Find("grok-imagine-video", ModelCapability.Video);
        Assert.NotNull(video);
        Assert.NotNull(video!.VideoReferenceImageCost);
        Assert.NotNull(video.VideoExtendCostPerSecond);
        Assert.False(string.IsNullOrWhiteSpace(video.PricingNotes));

        var rates = CostReportService.RatesFromModels("grok-imagine-video", "grok-imagine-image-quality");
        Assert.Equal("model_catalog", rates["video_input_image_source"]);
        Assert.Equal("model_catalog", rates["video_input_per_sec_source"]);
        Assert.Equal("model_catalog", rates["video_pricing_source"]);
    }
}
