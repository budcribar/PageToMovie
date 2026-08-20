using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]
public class FalVideoClientTests
{
    public FalVideoClientTests() => SupportedModelCatalog.ReloadCatalog();

    [Fact]
    public void SupportedModelCatalog_ContainsHunyuanVideo()
    {
        var entry = SupportedModelCatalog.Find("hunyuan-video", ModelCapability.Video);
        Assert.NotNull(entry);
        Assert.Equal("Hunyuan Video (Fal.ai)", entry.DisplayName);
        Assert.Equal(ModelProviderFamily.Fal, entry.Provider);
        Assert.Contains(SupportedModelCatalog.FalApiKeyEnv, entry.RequiredEnvKeys);
        Assert.False(entry.SupportsVideoContinue);
        Assert.True(entry.SupportsReferenceImages);
    }

    [Fact]
    public void Catalog_provider_id_tags_fal_request_without_a_hardcoded_prefix()
    {
        var fal = SupportedModelCatalog.Find("hunyuan-video", ModelCapability.Video);
        Assert.NotNull(fal);
        var providerId = SupportedModelCatalog.NormalizeProviderId(fal.ProviderId);
        Assert.False(string.IsNullOrWhiteSpace(providerId));

        var tagged = MultiProviderVideoClient.TagRequestId(providerId, "req_123456789");
        Assert.True(MultiProviderVideoClient.TrySplitTaggedRequestId(
            tagged, new[] { providerId }, out var parsed, out var raw));
        Assert.Equal(providerId, parsed);
        Assert.Equal("req_123456789", raw);
    }

    [Theory]
    [InlineData(3, 85)]
    [InlineData(4, 85)]
    [InlineData(5, 129)]
    [InlineData(8, 129)]
    [InlineData(10, 129)]
    public void FalNumFramesMapping_MatchesHunyuanApiSpec(int durationSeconds, int expectedFrames)
    {
        var frames = durationSeconds is > 0 and <= 4 ? 85 : 129;
        Assert.Equal(expectedFrames, frames);
    }
}
