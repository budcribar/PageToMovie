using System;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The admin one-off video model override (StartBatchGenRequest.VideoModel, resolved by
/// FilmJobService.ResolveExplicitVideoModel) must accept only catalog video models. RequireExplicit
/// alone falls back to an any-capability match, so this guards the batch spend path against an
/// override that names a chat/image/audio/voice model.
/// </summary>
// See CatalogSerialCollection in SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public class VideoModelOverrideTests
{
    public VideoModelOverrideTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Theory]
    [InlineData("grok-imagine-video")]
    [InlineData("fal-ai/wan-i2v")]
    [InlineData("veo-3.1-generate-preview")]
    public void Accepts_catalog_video_models(string modelId)
    {
        var id = FilmJobService.ResolveExplicitVideoModel(modelId);
        Assert.Equal(modelId, id);
    }

    [Theory]
    [InlineData("grok-4.5")]                 // Chat (and Vision) — not Video
    [InlineData("grok-imagine-image")]       // Image
    [InlineData("eleven_multilingual_v2")]   // Voice
    [InlineData("fal-ai/musicgen")]          // Audio
    public void Rejects_non_video_models(string modelId)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FilmJobService.ResolveExplicitVideoModel(modelId));
        Assert.Contains(modelId, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("auto")]
    [InlineData("not-a-real-model-id")]
    public void Rejects_empty_sentinel_or_unknown(string modelId)
    {
        Assert.Throws<InvalidOperationException>(
            () => FilmJobService.ResolveExplicitVideoModel(modelId));
    }
}
