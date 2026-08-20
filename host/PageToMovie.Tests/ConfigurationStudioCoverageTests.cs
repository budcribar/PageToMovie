using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Studio coverage open policy + key-panel state without a browser.
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
    public void ShouldShowStudioBody_starts_open_from_local_flag()
    {
        Assert.True(ConfigurationCoverageCard.ShouldShowStudioBody(localOpen: true, coverageOpen: false, editId: null));
    }

    [Fact]
    public void ShouldShowStudioBody_closed_when_all_flags_false()
    {
        Assert.False(ConfigurationCoverageCard.ShouldShowStudioBody(localOpen: false, coverageOpen: false, editId: null));
    }

    [Fact]
    public void ShouldShowStudioBody_stays_open_for_add_key_rerender()
    {
        // Uncontrolled details would collapse on re-render; C# @if must stay open
        // when BeginAddKey* sets StudioCoverageOpen and/or _coverageEditId.
        Assert.True(ConfigurationCoverageCard.ShouldShowStudioBody(localOpen: false, coverageOpen: true, editId: null));
        Assert.True(ConfigurationCoverageCard.ShouldShowStudioBody(localOpen: false, coverageOpen: false, editId: "video"));
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
        Assert.True(ConfigurationCoverageCard.ShouldShowStudioBody(
            localOpen: false,
            coverageOpen: host.Coverage.StudioCoverageOpen,
            editId: host.Coverage._coverageEditId));
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
        Assert.True(ConfigurationCoverageCard.ShouldShowStudioBody(
            localOpen: false,
            coverageOpen: host.Coverage.StudioCoverageOpen,
            editId: host.Coverage._coverageEditId));
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
        Assert.True(ConfigurationCoverageCard.ShouldShowStudioBody(
            localOpen: false,
            coverageOpen: host.Coverage.StudioCoverageOpen,
            editId: host.Coverage._coverageEditId));
    }

    private static string FirstCatalogProviderId(ModelCapability capability)
    {
        var entry = SupportedModelCatalog.ForCapability(capability)
            .First(e => e.Enabled && !string.IsNullOrWhiteSpace(e.ProviderId));
        return SupportedModelCatalog.NormalizeProviderId(entry.ProviderId);
    }
}
