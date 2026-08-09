using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: ConfigurationCatalog → Host.*
public partial class Configuration
{
    internal Task LoadCatalogAsync() => Catalog.LoadCatalogAsync();

    internal void ApplyCatalogDefaultsIfEmpty() => Catalog.ApplyCatalogDefaultsIfEmpty();

    internal static string DefaultForCapability(string capabilityId) => ConfigurationCatalog.DefaultForCapability(capabilityId);

    internal static string DefaultQualityModel() => ConfigurationCatalog.DefaultQualityModel();

    internal static string ModelProviderId(SupportedModelDto m) => ConfigurationCatalog.ModelProviderId(m);

    internal static string ProviderPlaceholder(string providerId) => ConfigurationCatalog.ProviderPlaceholder(providerId);

    internal static string FriendlyProviderLabel(string? providerId) => ConfigurationCatalog.FriendlyProviderLabel(providerId);

    internal string VendorLabel(string modelId, string capability) => Catalog.VendorLabel(modelId, capability);

    internal double CatalogVideoRate(string resolution) => Catalog.CatalogVideoRate(resolution);

    internal double CatalogImageRate() => Catalog.CatalogImageRate();

    internal static string UsdRate(double v) => ConfigurationCatalog.UsdRate(v);

}
