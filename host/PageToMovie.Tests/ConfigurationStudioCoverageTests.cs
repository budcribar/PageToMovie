using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Studio coverage key-panel state (Add / Replace / Add provider) without a browser.
/// Provider ids come from the catalog — never a hardcoded model/provider string.
/// </summary>
[Collection("catalog-serial")]
public class ConfigurationStudioCoverageTests
{
    public ConfigurationStudioCoverageTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void Studio_coverage_starts_open()
    {
        var host = new Configuration();
        host.EnsureDomains();
        Assert.True(host.Coverage.StudioCoverageOpen);
    }

    [Fact]
    public void BeginAddKeyForProvider_opens_key_panel_for_catalog_provider()
    {
        var host = new Configuration();
        host.EnsureDomains();
        var providerId = FirstCatalogProviderId(ModelCapability.Video);

        host.Keys.BeginAddKeyForProvider("video", providerId);

        Assert.True(host.Coverage.StudioCoverageOpen);
        Assert.Equal("video", host.Coverage._coverageEditId);
        Assert.Equal("add-key", host.Coverage._coverageKeyMode);
        Assert.Equal(
            SupportedModelCatalog.NormalizeProviderId(providerId),
            host.Coverage._coverageKeyProviderId);
    }

    [Fact]
    public void BeginReplaceKey_opens_replace_panel_for_catalog_provider()
    {
        var host = new Configuration();
        host.EnsureDomains();
        var providerId = FirstCatalogProviderId(ModelCapability.Video);

        host.Keys.BeginReplaceKey("video", providerId);

        Assert.True(host.Coverage.StudioCoverageOpen);
        Assert.Equal("video", host.Coverage._coverageEditId);
        Assert.Equal("replace", host.Coverage._coverageKeyMode);
        Assert.Equal(
            SupportedModelCatalog.NormalizeProviderId(providerId),
            host.Coverage._coverageKeyProviderId);
    }

    [Fact]
    public void BeginAddProvider_opens_key_panel()
    {
        var host = new Configuration();
        host.EnsureDomains();

        host.Keys.BeginAddProvider("video");

        Assert.True(host.Coverage.StudioCoverageOpen);
        Assert.Equal("video", host.Coverage._coverageEditId);
        Assert.Equal("add-provider", host.Coverage._coverageKeyMode);
    }

    private static string FirstCatalogProviderId(ModelCapability capability)
    {
        var entry = SupportedModelCatalog.ForCapability(capability)
            .First(e => e.Enabled && !string.IsNullOrWhiteSpace(e.ProviderId));
        return SupportedModelCatalog.NormalizeProviderId(entry.ProviderId);
    }
}
