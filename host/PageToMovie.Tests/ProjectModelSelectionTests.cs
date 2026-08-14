using System.Text.Json;
using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Capability mapping: Settings config keys must resolve only models that match the
/// job capability (video slot ≠ chat model, etc.). Documents intentional Chat↔Vision overlap.
/// </summary>
// See CatalogSerialCollection in SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public class ProjectModelSelectionTests
{
    public ProjectModelSelectionTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    static IReadOnlyDictionary<string, JsonElement> Cfg(params (string key, string value)[] pairs)
    {
        var obj = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in pairs)
            obj[k] = JsonSerializer.SerializeToElement(v);
        return obj;
    }

    // ── Happy path: correct capability in each Settings slot ───────────────

    [Theory]
    [InlineData("grok-imagine-video")]
    [InlineData("fal-ai/wan-2.1")]
    [InlineData("veo-3.1")]
    public void RequireVideo_accepts_catalog_video_models(string modelId)
    {
        var id = ProjectModelSelection.RequireVideo(Cfg(("model_name", modelId)));
        Assert.Equal(modelId, id);
    }

    [Theory]
    [InlineData("grok-imagine-image")]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("fal-ai/flux/dev")]
    public void RequireImage_accepts_catalog_image_models(string modelId)
    {
        var id = ProjectModelSelection.RequireImage(Cfg(("image_model_name", modelId)));
        Assert.Equal(modelId, id);
    }

    [Theory]
    [InlineData("grok-4.5")]
    [InlineData("grok-4")]
    [InlineData("gemini-2.5-flash")]
    public void RequirePlanning_accepts_catalog_chat_models(string modelId)
    {
        var id = ProjectModelSelection.RequirePlanning(Cfg(("planning_model_name", modelId)));
        Assert.Equal(modelId, id);
    }

    [Fact]
    public void RequirePlanning_falls_back_to_chat_model_name()
    {
        var id = ProjectModelSelection.RequirePlanning(Cfg(("chat_model_name", "grok-4.5")));
        Assert.Equal("grok-4.5", id);
    }

    [Theory]
    [InlineData("grok-4.5")]
    [InlineData("gemini-2.5-flash")]
    public void RequireVision_accepts_vision_or_chat_overlap_models(string modelId)
    {
        // Catalog lists some ids under Vision and/or Chat; Find allows Chat↔Vision.
        var id = ProjectModelSelection.RequireVision(Cfg(("vision_model_name", modelId)));
        Assert.Equal(modelId, id);
    }

    [Fact]
    public void TryVision_returns_catalog_vision_id_or_null()
    {
        Assert.Equal("grok-4.5", ProjectModelSelection.TryVision(Cfg(("vision_model_name", "grok-4.5"))));
        Assert.Null(ProjectModelSelection.TryVision(null));
        Assert.Null(ProjectModelSelection.TryVision(Cfg(("vision_model_name", "none"))));
        Assert.Null(ProjectModelSelection.TryVision(Cfg(("vision_model_name", "grok-imagine-video"))));
    }

    // ── Mismatch: wrong capability in slot must throw ─────────────────────

    [Theory]
    [InlineData("grok-4.5")]                 // Chat (and Vision) — not Video
    [InlineData("grok-imagine-image")]       // Image
    [InlineData("eleven_multilingual_v2")]   // Voice
    [InlineData("fal-ai/musicgen")]          // Audio
    public void RequireVideo_rejects_non_video_models(string modelId)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequireVideo(Cfg(("model_name", modelId))));
        Assert.Contains(modelId, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("grok-imagine-video")]
    [InlineData("grok-4.5")]
    [InlineData("eleven_multilingual_v2")]
    public void RequireImage_rejects_non_image_models(string modelId)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequireImage(Cfg(("image_model_name", modelId))));
        Assert.Contains(modelId, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("grok-imagine-video")]
    [InlineData("grok-imagine-image")]
    [InlineData("fal-ai/wan-2.1")]
    public void RequirePlanning_rejects_non_chat_models(string modelId)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequirePlanning(Cfg(("planning_model_name", modelId))));
        Assert.Contains(modelId, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequireVideo_rejects_disabled_hunyuan()
    {
        // Present in catalog but Enabled=false
        var hunyuan = SupportedModelCatalog.Find("hunyuan-video", ModelCapability.Video);
        if (hunyuan is null)
            return; // catalog variant without this id
        Assert.False(hunyuan.Enabled);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequireVideo(Cfg(("model_name", "hunyuan-video"))));
        Assert.Contains("hunyuan-video", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Empty / sentinel config ───────────────────────────────────────────

    [Fact]
    public void RequireVideo_empty_config_throws_no_model_selected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequireVideo(null));
        Assert.Contains("no model selected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("disabled")]
    [InlineData("auto")]
    [InlineData("")]
    public void TryGet_ignores_sentinel_values(string sentinel)
    {
        var id = ProjectModelSelection.TryGet(Cfg(("model_name", sentinel)), "model_name");
        Assert.Null(id);
    }

    [Fact]
    public void RequireVideo_sentinel_only_throws_no_model_selected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequireVideo(Cfg(("model_name", "none"))));
        Assert.Contains("no model selected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Catalog Find / API-kind mapping ───────────────────────────────────

    [Fact]
    public void Find_video_id_not_usable_as_chat_without_overlap_rule()
    {
        Assert.Null(SupportedModelCatalog.Find("grok-imagine-video", ModelCapability.Chat));
        Assert.NotNull(SupportedModelCatalog.Find("grok-imagine-video", ModelCapability.Video));
    }

    [Fact]
    public void Find_chat_vision_overlap_allows_cross_lookup()
    {
        // Intentional: same id may serve Chat and Vision slots.
        var asChat = SupportedModelCatalog.Find("grok-4.5", ModelCapability.Chat);
        var asVision = SupportedModelCatalog.Find("grok-4.5", ModelCapability.Vision);
        Assert.True(asChat is not null || asVision is not null);
        // Cross-capability Find should still resolve via Chat↔Vision rule
        Assert.NotNull(SupportedModelCatalog.Find("grok-4.5", ModelCapability.Chat));
        Assert.NotNull(SupportedModelCatalog.Find("grok-4.5", ModelCapability.Vision));
    }

    [Theory]
    [InlineData("video", ModelCapability.Video)]
    [InlineData("video_extend", ModelCapability.Video)]
    [InlineData("image", ModelCapability.Image)]
    [InlineData("vision", ModelCapability.Vision)]
    [InlineData("chat", ModelCapability.Chat)]
    [InlineData("planning", ModelCapability.Chat)]
    [InlineData("video_review", ModelCapability.Chat)]
    [InlineData("audio", ModelCapability.Audio)]
    [InlineData("music", ModelCapability.Audio)]
    [InlineData("voice", ModelCapability.Voice)]
    [InlineData("tts", ModelCapability.Voice)]
    [InlineData("lip_sync", ModelCapability.LipSync)]
    public void CapabilityFromApiKind_maps_known_kinds(string kind, ModelCapability expected)
    {
        Assert.Equal(expected, SupportedModelCatalog.CapabilityFromApiKind(kind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown_kind")]
    [InlineData("stage2")]
    public void CapabilityFromApiKind_unknown_returns_null(string? kind)
    {
        Assert.Null(SupportedModelCatalog.CapabilityFromApiKind(kind));
    }

    // ── Prefer planning key over chat when both set ───────────────────────

    [Fact]
    public void RequirePlanning_prefers_planning_model_name_over_chat()
    {
        var id = ProjectModelSelection.RequirePlanning(Cfg(
            ("planning_model_name", "grok-4"),
            ("chat_model_name", "grok-4.5")));
        Assert.Equal("grok-4", id);
    }

    [Fact]
    public void RequireVideoReview_uses_quality_or_vision_or_planning_chat_slot()
    {
        // video_review maps to Chat capability via RequireVideoReview
        var id = ProjectModelSelection.RequireVideoReview(Cfg(("quality_model_name", "grok-4.5")));
        Assert.Equal("grok-4.5", id);
    }
}
