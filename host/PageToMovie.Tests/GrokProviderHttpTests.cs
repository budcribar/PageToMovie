using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

[Collection("env-serial")]
public class GrokProviderHttpTests
{
    [Fact]
    public void ResolveApiKey_uses_catalog_provider_slot_not_a_hardcoded_name()
    {
        var providerId = AdapterProviderId();
        using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [providerId] = "from-catalog-slot",
        }))
        {
            Assert.Equal("from-catalog-slot", GrokProviderHttp.ResolveApiKey());
        }
    }

    [Fact]
    public void ResolveApiKey_for_model_uses_that_row_provider_slot()
    {
        var video = CatalogXaiVideo();
        var providerId = SupportedModelCatalog.NormalizeProviderId(video.ProviderId);
        using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [providerId] = "model-slot-key",
        }))
        {
            Assert.Equal("model-slot-key", GrokProviderHttp.ResolveApiKey(video.Id));
        }
    }

    [Fact]
    public void RequireApiKey_throws_when_slot_and_catalog_env_are_empty()
    {
        var envNames = GrokProviderHttp.CatalogEnvKeys(null);
        var saved = envNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in envNames)
                Environment.SetEnvironmentVariable(name, null);
            using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => GrokProviderHttp.RequireApiKey());
                Assert.Contains(AdapterProviderId(), ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void ResolveApiKey_env_only_when_name_is_on_the_catalog_row()
    {
        var video = CatalogXaiVideo();
        var envName = Assert.Single(video.RequiredEnvKeys);
        var prev = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "from-catalog-env");
            using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)))
            {
                Assert.Equal("from-catalog-env", GrokProviderHttp.ResolveApiKey(video.Id));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, prev);
        }
    }

    [Fact]
    public void CatalogProviderId_unknown_model_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GrokProviderHttp.CatalogProviderId("not-a-catalog-video-model"));
        Assert.Contains("not in the models catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveApiKeyAsync_asks_user_store_with_catalog_provider_id()
    {
        var providerId = AdapterProviderId();
        var keys = new RecordingKeys();
        keys.Set("owner", providerId, "personal-catalog-slot");
        using (UserApiCallScope.Push("owner"))
        using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)))
        {
            var key = await GrokProviderHttp.ResolveApiKeyAsync(keys);
            Assert.Equal("personal-catalog-slot", key);
            var call = Assert.Single(keys.Calls);
            Assert.Equal("owner", call.UserId);
            Assert.Equal(providerId, call.ProviderId);
        }
    }

    private static string AdapterProviderId()
    {
        var id = SupportedModelCatalog.ProviderIdForApiBase(SupportedModelCatalog.XaiApiBase);
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id;
    }

    private static SupportedModelEntry CatalogXaiVideo()
    {
        var api = SupportedModelCatalog.XaiApiBase;
        return SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .First(m => m.Enabled
                && !string.IsNullOrWhiteSpace(m.ApiBase)
                && string.Equals(m.ApiBase.TrimEnd('/'), api.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingKeys : IUserApiKeyProvider
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
