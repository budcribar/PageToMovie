using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]
public class CatalogApiKeyTests
{
    [Fact]
    public void ProviderIdForVideo_uses_catalog_row_not_a_hardcoded_name()
    {
        var xaiVideo = SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .First(m => string.Equals(
                m.ApiBase.TrimEnd('/'),
                SupportedModelCatalog.XaiApiBase.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase));
        var other = SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .First(m => !string.Equals(
                SupportedModelCatalog.NormalizeProviderId(m.ProviderId),
                SupportedModelCatalog.NormalizeProviderId(xaiVideo.ProviderId),
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            SupportedModelCatalog.NormalizeProviderId(xaiVideo.ProviderId),
            CatalogApiKey.ProviderIdForVideo(xaiVideo.Id));
        Assert.Equal(
            SupportedModelCatalog.NormalizeProviderId(other.ProviderId),
            CatalogApiKey.ProviderIdForVideo(other.Id));
        Assert.NotEqual(
            CatalogApiKey.ProviderIdForVideo(xaiVideo.Id),
            CatalogApiKey.ProviderIdForVideo(other.Id));
    }

    [Fact]
    public void ResolveVideoModel_prefers_sidecar_then_project()
    {
        var xaiVideo = SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .First(m => string.Equals(
                m.ApiBase.TrimEnd('/'),
                SupportedModelCatalog.XaiApiBase.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase));
        var other = SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .First(m => !string.Equals(
                SupportedModelCatalog.NormalizeProviderId(m.ProviderId),
                SupportedModelCatalog.NormalizeProviderId(xaiVideo.ProviderId),
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(xaiVideo.Id, CatalogApiKey.ResolveVideoModel(xaiVideo.Id, other.Id));
        Assert.Equal(other.Id, CatalogApiKey.ResolveVideoModel(null, other.Id));
    }

    [Fact]
    public async Task GetKeyAsync_never_calls_provider_with_null_user()
    {
        var providerId = SupportedModelCatalog.ProviderIdForApiBase(SupportedModelCatalog.XaiApiBase);
        Assert.False(string.IsNullOrWhiteSpace(providerId));
        var keys = new RecordingKeys();

        var none = await CatalogApiKey.GetKeyAsync(keys, null, providerId);
        Assert.Null(none);
        Assert.Empty(keys.Calls);

        keys.Set("owner", providerId, "personal");
        var got = await CatalogApiKey.GetKeyAsync(keys, "owner", providerId);
        Assert.Equal("personal", got);
        Assert.All(keys.Calls, c => Assert.False(string.IsNullOrWhiteSpace(c.UserId)));
    }

    private sealed class RecordingKeys : PageToMovie.Engine.Abstractions.IUserApiKeyProvider
    {
        private readonly Dictionary<(string User, string Provider), string> _keys = new();
        public List<(string? UserId, string ProviderId)> Calls { get; } = new();

        public void Set(string userId, string providerId, string key) =>
            _keys[(userId, SupportedModelCatalog.NormalizeProviderId(providerId))] = key;

        public Task<string?> GetKeyAsync(string? userId, string providerId, CancellationToken ct = default)
        {
            Calls.Add((userId, providerId));
            if (string.IsNullOrWhiteSpace(userId)) return Task.FromResult<string?>(null);
            var norm = SupportedModelCatalog.NormalizeProviderId(providerId);
            return Task.FromResult(_keys.TryGetValue((userId, norm), out var k) ? k : null);
        }
    }
}
