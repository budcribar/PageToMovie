using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Plate sort chooses vision vs heuristic from catalog Vision capability + client config,
/// not a vendor <c>useGrok</c> flag.
/// </summary>
[Collection("catalog-serial")]
public class CharacterBookPlateVisionGateTests
{
    public CharacterBookPlateVisionGateTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void VisionSortIsUsable_requires_catalog_vision_row_and_configured_client()
    {
        var visionId = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision);
        Assert.False(string.IsNullOrWhiteSpace(visionId));

        var ready = new StubVision { Configured = true };
        Assert.True(CharacterBookPlateService.VisionSortIsUsable(visionId, ready));

        var missingKey = new StubVision { Configured = false };
        Assert.False(CharacterBookPlateService.VisionSortIsUsable(visionId, missingKey));
    }

    [Fact]
    public void VisionSortIsUsable_rejects_non_vision_catalog_models()
    {
        var ready = new StubVision { Configured = true };
        Assert.False(CharacterBookPlateService.VisionSortIsUsable("grok-imagine-video", ready));
        Assert.False(CharacterBookPlateService.VisionSortIsUsable("not-a-real-model", ready));
        Assert.False(CharacterBookPlateService.VisionSortIsUsable("", ready));
        Assert.False(CharacterBookPlateService.VisionSortIsUsable(null, ready));
    }

    [Fact]
    public void VisionSortIsUsable_asks_client_for_that_model_not_any_provider()
    {
        var visionId = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision);
        var client = new StubVision
        {
            Configured = true,
            ConfiguredForOverride = model =>
                string.Equals(model, visionId, StringComparison.OrdinalIgnoreCase),
        };

        Assert.True(CharacterBookPlateService.VisionSortIsUsable(visionId, client));
        Assert.Equal(visionId, client.LastConfiguredForModel);
    }

    [Fact]
    public void VisionFallbackMessage_uses_catalog_env_keys_not_hardcoded_vendor()
    {
        var visionId = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision);
        var entry = SupportedModelCatalog.Find(visionId, ModelCapability.Vision);
        Assert.NotNull(entry);
        Assert.NotEmpty(entry.RequiredEnvKeys);

        var msg = CharacterBookPlateService.VisionFallbackMessage(visionId);
        foreach (var key in entry.RequiredEnvKeys)
            Assert.Contains(key, msg, StringComparison.Ordinal);
        Assert.DoesNotContain("Grok", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("heuristic", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisionFallbackMessage_when_no_model_selected()
    {
        var msg = CharacterBookPlateService.VisionFallbackMessage(null);
        Assert.Contains("No vision model selected", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XAI_API_KEY", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void VisionFallbackMessage_when_model_is_not_vision()
    {
        var msg = CharacterBookPlateService.VisionFallbackMessage("grok-imagine-video");
        Assert.Contains("not a vision model", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePlateSortVisionModel_uses_project_vision_slot()
    {
        var visionId = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision);
        var cfg = Cfg((ProjectModelSelection.VisionConfigKey, visionId!));
        Assert.Equal(visionId, CharacterBookPlateService.ResolvePlateSortVisionModel(cfg, explicitModel: null));
    }

    [Fact]
    public void ResolvePlateSortVisionModel_explicit_overrides_config()
    {
        var configured = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision);
        var explicitId = "grok-4.5";
        Assert.NotNull(SupportedModelCatalog.Find(explicitId, ModelCapability.Vision));

        var cfg = Cfg((ProjectModelSelection.VisionConfigKey, configured!));
        Assert.Equal(explicitId, CharacterBookPlateService.ResolvePlateSortVisionModel(cfg, explicitId));
    }

    [Fact]
    public void ResolvePlateSortVisionModel_wrong_capability_or_empty_is_null()
    {
        Assert.Null(CharacterBookPlateService.ResolvePlateSortVisionModel(
            Cfg((ProjectModelSelection.VisionConfigKey, "grok-imagine-video")), explicitModel: null));
        Assert.Null(CharacterBookPlateService.ResolvePlateSortVisionModel(null, explicitModel: null));
        Assert.Null(CharacterBookPlateService.ResolvePlateSortVisionModel(null, "grok-imagine-video"));
        Assert.Null(CharacterBookPlateService.ResolvePlateSortVisionModel(
            Cfg((ProjectModelSelection.VisionConfigKey, "none")), explicitModel: null));
    }

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Cfg(
        params (string key, string value)[] pairs)
    {
        var obj = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var (k, v) in pairs)
            obj[k] = System.Text.Json.JsonSerializer.SerializeToElement(v);
        return obj;
    }

    private sealed class StubVision : IVisionClient
    {
        public bool Configured { get; set; }
        public Func<string?, bool>? ConfiguredForOverride { get; set; }
        public string? LastConfiguredForModel { get; private set; }

        public bool IsConfigured => Configured;

        public bool IsConfiguredFor(string? model)
        {
            LastConfiguredForModel = model;
            return ConfiguredForOverride?.Invoke(model) ?? Configured;
        }

        public Task<string> TranscribePageAsync(
            string imagePath, int page, string model = "", CancellationToken ct = default) =>
            Task.FromResult("");

        public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
            string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
            string model = "", CancellationToken ct = default) =>
            Task.FromResult(new CharacterPageClassification());

        public Task<string> CompleteWithImagesAsync(
            string prompt, IReadOnlyList<string> imagePaths, string model = "",
            string detail = "low", CancellationToken ct = default) =>
            Task.FromResult("");
    }
}
