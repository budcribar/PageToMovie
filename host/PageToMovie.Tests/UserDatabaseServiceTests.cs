using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class UserDatabaseServiceTests
{
    [Fact]
    public void ResolveDataDirectory_uses_data_under_an_isolated_test_workspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "ptm-user-db-resolve-" + Guid.NewGuid().ToString("N"));

        var resolved = UserDatabaseService.ResolveDataDirectory(workspace);

        Assert.Equal(Path.Combine(workspace, "data"), resolved);
    }

    [Fact]
    public async Task SaveXaiApiKeyAsync_encrypts_key_and_decrypts_per_user()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-user-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var service = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

        var testUserId = "user_alpha_123";
        var originalKey = "xai-test-key-998877665544332211";

        await service.SaveXaiApiKeyAsync(testUserId, originalKey);

        var decrypted = await service.GetDecryptedXaiApiKeyAsync(testUserId);
        Assert.Equal(originalKey, decrypted);

        var settings = await service.GetUserSettingsDtoAsync(testUserId);
        var grokStatus = Assert.Single(settings.Providers, p => p.ProviderId == "grok");
        Assert.True(grokStatus.HasPersonalKey);
        Assert.NotNull(grokStatus.MaskedPersonalKey);
        Assert.Contains("...", grokStatus.MaskedPersonalKey);
        Assert.DoesNotContain("998877665544332211", grokStatus.MaskedPersonalKey);

        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public async Task UpdateUserSettingsAsync_saves_multiple_provider_keys_independently()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-user-db-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var service = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);
        var userId = "user_multi_1";

        await service.UpdateUserSettingsAsync(userId, new UpdateUserSettingsRequest
        {
            XaiApiKey = "xai-key-aaaa1111bbbb",
            GeminiApiKey = "AIza-gemini-key-2222",
        });

        Assert.Equal("xai-key-aaaa1111bbbb", await service.GetDecryptedProviderApiKeyAsync(userId, "grok"));
        Assert.Equal("AIza-gemini-key-2222", await service.GetDecryptedProviderApiKeyAsync(userId, "gemini"));
        Assert.Null(await service.GetDecryptedProviderApiKeyAsync(userId, "anthropic"));

        // Null fields leave existing keys alone.
        await service.UpdateUserSettingsAsync(userId, new UpdateUserSettingsRequest
        {
            AnthropicApiKey = "sk-ant-claude-3333",
        });
        Assert.Equal("xai-key-aaaa1111bbbb", await service.GetDecryptedProviderApiKeyAsync(userId, "grok"));
        Assert.Equal("sk-ant-claude-3333", await service.GetDecryptedProviderApiKeyAsync(userId, "anthropic"));

        // Empty string clears that provider only.
        await service.UpdateUserSettingsAsync(userId, new UpdateUserSettingsRequest
        {
            GeminiApiKey = "",
        });
        Assert.Null(await service.GetDecryptedProviderApiKeyAsync(userId, "gemini"));
        Assert.Equal("xai-key-aaaa1111bbbb", await service.GetDecryptedProviderApiKeyAsync(userId, "grok"));

        var settings = await service.GetUserSettingsDtoAsync(userId);
        Assert.Contains(settings.Providers, p => p.ProviderId == "grok" && p.HasPersonalKey);
        Assert.Contains(settings.Providers, p => p.ProviderId == "gemini" && !p.HasPersonalKey);
        Assert.Contains(settings.Providers, p => p.ProviderId == "anthropic" && p.HasPersonalKey);
        Assert.True(settings.Providers.Count >= 4);

        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public async Task DbUserApiKeyProvider_resolves_keys_per_provider()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-key-prov-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var userDb = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

        var testUserId = "user_beta_456";
        await userDb.UpdateUserSettingsAsync(testUserId, new UpdateUserSettingsRequest
        {
            XaiApiKey = "xai-beta-api-key-1234567890",
            GeminiApiKey = "gemini-beta-key-zzzz",
        });

        var provider = new DbUserApiKeyProvider(userDb, opts);
        Assert.Equal("xai-beta-api-key-1234567890", await provider.GetKeyAsync(testUserId));
        Assert.Equal("xai-beta-api-key-1234567890", await provider.GetKeyAsync(testUserId, "grok"));
        Assert.Equal("gemini-beta-key-zzzz", await provider.GetKeyAsync(testUserId, "gemini"));
        Assert.True(await provider.HasKeyAsync(testUserId, "gemini"));
        // Anthropic has no personal key for this user; HasKey may still be true if server env is set.
        Assert.Null(await userDb.GetDecryptedProviderApiKeyAsync(testUserId, "anthropic"));

        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public async Task AcceptTermsAsync_records_timestamp_and_version_in_user_database()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-terms-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var userDb = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

        var userId = "user_terms_789";
        Assert.False(await userDb.HasAcceptedTermsAsync(userId));

        // Create user record via settings update
        await userDb.UpdateUserSettingsAsync(userId, new UpdateUserSettingsRequest());

        Assert.False(await userDb.HasAcceptedTermsAsync(userId));

        bool accepted = await userDb.AcceptTermsAsync(userId, "1.0");
        Assert.True(accepted);

        Assert.True(await userDb.HasAcceptedTermsAsync(userId));

        try { Directory.Delete(tmp, true); } catch { }
    }
}
