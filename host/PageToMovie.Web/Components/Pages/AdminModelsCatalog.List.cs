using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using PageToMovie.Core.Localization;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: AdminModelsList → Host.*
public partial class AdminModelsCatalog
{
    internal void ResetFilters() => List.ResetFilters();

    internal IEnumerable<JsonObject> FilteredModels => List.FilteredModels;

    internal List<string> AvailableProviders => List.AvailableProviders;

    internal static bool IsEnabled(JsonObject m) => AdminModelsList.IsEnabled(m);

    internal static bool IsDeprecated(JsonObject m) => AdminModelsList.IsDeprecated(m);

    internal void ToggleModelDeprecated(JsonObject m) => List.ToggleModelDeprecated(m);

    internal Task LoadCatalogAsync() => List.LoadCatalogAsync();

    internal void ToggleModelEnabled(JsonObject m) => List.ToggleModelEnabled(m);

    internal Task RemoveModelAsync(JsonObject m) => List.RemoveModelAsync(m);

    internal Task OnCatalogSearchChanged(string? value) => List.OnCatalogSearchChanged(value);

    internal static string StatusRowClass(string status) => AdminModelsList.StatusRowClass(status);

    internal static string StatusBadgeClass(string status) => AdminModelsList.StatusBadgeClass(status);

    internal string GetCapBadgeClass(string cap) => List.GetCapBadgeClass(cap);
}
