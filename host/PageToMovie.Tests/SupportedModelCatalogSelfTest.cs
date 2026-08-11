using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Catalog self-test: every enabled model must carry required capability + cost fields.
/// Runs against the real models_catalog.json so deploy/CI fails before movie generation.
/// </summary>
// Swaps in reduced/synthetic/lab catalogs mid-test. See CatalogSerialCollection in
// SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public class SupportedModelCatalogSelfTest
{
    public SupportedModelCatalogSelfTest()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void All_enabled_models_pass_catalog_self_test()
    {
        var errors = SupportedModelCatalog.ValidateEnabledModels();
        Assert.True(
            errors.Count == 0,
            "models_catalog.json incomplete:\n- " + string.Join("\n- ", errors));
    }

    [Fact]
    public void EnsureEnabledModelsComplete_does_not_throw_on_real_catalog()
    {
        var ex = Record.Exception(SupportedModelCatalog.EnsureEnabledModelsComplete);
        Assert.Null(ex);
        Assert.True(SupportedModelCatalog.Entries.Count > 0);
    }

    [Fact]
    public void Enabled_models_have_lastVerifiedAt_and_pricing_dates_when_priced()
    {
        foreach (var e in SupportedModelCatalog.Entries.Where(x => x.Enabled))
        {
            Assert.False(string.IsNullOrWhiteSpace(e.LastVerifiedAt), e.Id + " lastVerifiedAt");
            var hasCost = e.InputCostPerMillionTokens is not null
                || e.OutputCostPerMillionTokens is not null
                || e.ImageCostPerImage is not null
                || e.VideoCostPerSecondByResolution is { Count: > 0 }
                || e.VideoBaseCostByResolution is { Count: > 0 }
                || e.VideoReferenceImageCost is not null;
            if (hasCost)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.PricingLastReviewedAt), e.Id + " pricingLastReviewedAt");
                Assert.False(string.IsNullOrWhiteSpace(e.PricingNotes), e.Id + " pricingNotes");
            }
        }
    }

    [Fact]
    public void Self_test_detects_missing_video_cost_on_synthetic_model()
    {
        const string incomplete = """
        {
          "models": [
            {
              "id": "broken-video",
              "displayName": "Broken",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "supportsVideoContinue": false,
              "minClipDurationSeconds": 1,
              "maxClipDurationSeconds": 5,
              "absMaxClipDurationSeconds": 5,
              "maxReferenceImages": 1,
              "maxPromptLength": 800,
              "lastVerifiedAt": "2026-08-05"
            }
          ]
        }
        """;
        Assert.True(SupportedModelCatalog.TryLoadFromJson(incomplete));
        var errors = SupportedModelCatalog.ValidateEnabledModels();
        Assert.Contains(errors, e => e.Contains("videoCost", StringComparison.OrdinalIgnoreCase)
            || e.Contains("videoReferenceImageCost", StringComparison.OrdinalIgnoreCase));
        // Restore real catalog for other tests in this class collection
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void Lab_mode_model_skips_strict_field_requirements()
    {
        const string lab = """
        {
          "models": [
            {
              "id": "lab-video-wip",
              "displayName": "Lab Video WIP",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "labMode": true,
              "labNotes": "Experiment only — durations not filled yet"
            }
          ]
        }
        """;
        Assert.True(SupportedModelCatalog.TryLoadFromJson(lab));
        var errors = SupportedModelCatalog.ValidateEnabledModels();
        Assert.Empty(errors);
        var e = SupportedModelCatalog.Find("lab-video-wip", ModelCapability.Video);
        Assert.NotNull(e);
        Assert.True(e!.LabMode);
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void Lab_mode_without_labNotes_fails_self_test()
    {
        const string lab = """
        {
          "models": [
            {
              "id": "lab-bad",
              "displayName": "Lab Bad",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "labMode": true
            }
          ]
        }
        """;
        Assert.True(SupportedModelCatalog.TryLoadFromJson(lab));
        var errors = SupportedModelCatalog.ValidateEnabledModels();
        Assert.Contains(errors, x => x.Contains("labNotes", StringComparison.OrdinalIgnoreCase));
        SupportedModelCatalog.ReloadCatalog();
    }

}
