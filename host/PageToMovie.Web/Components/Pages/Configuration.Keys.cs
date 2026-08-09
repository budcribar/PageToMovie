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

// Forwarders: ConfigurationKeys → Host.*
public partial class Configuration
{
    internal bool IsKeyVisible(string providerId) => Keys.IsKeyVisible(providerId);

    internal void ToggleKeyVisible(string providerId) => Keys.ToggleKeyVisible(providerId);

    internal void BeginAddKey(string coverageId, string? preferredProviderId = null) => Keys.BeginAddKey(coverageId, preferredProviderId);

    internal void BeginReplaceKey(string coverageId, string providerId) => Keys.BeginReplaceKey(coverageId, providerId);

    internal void BeginAddKeyForProvider(string coverageId, string providerId) => Keys.BeginAddKeyForProvider(coverageId, providerId);

    internal void BeginAddProvider(string coverageId) => Keys.BeginAddProvider(coverageId);

    internal void CancelAddKey() => Keys.CancelAddKey();

    internal IEnumerable<ProviderKeyStatusDto> ProvidersForKeyPanel(string coverageId) => Keys.ProvidersForKeyPanel(coverageId);

    internal void SelectKeyProvider(string providerId) => Keys.SelectKeyProvider(providerId);

    internal Task SaveCoverageKeyAsync(string providerId, string coverageId) => Keys.SaveCoverageKeyAsync(providerId, coverageId);

    internal IReadOnlyList<ProviderKeyStatusDto> ProviderRows => Keys.ProviderRows;

    internal string? GetKeyInput(string providerId) => Keys.GetKeyInput(providerId);

    internal void SetKeyInput(string providerId, string? value) => Keys.SetKeyInput(providerId, value);

    internal Task SaveProviderKeyAsync(string providerId) => Keys.SaveProviderKeyAsync(providerId);

    internal Task ClearProviderKeyAsync(string providerId) => Keys.ClearProviderKeyAsync(providerId);

    internal Task PersistProviderKeyAsync(string providerId, string keyValue, bool clearing) => Keys.PersistProviderKeyAsync(providerId, keyValue, clearing);

    internal void ApplyProviderModelDefaults(string providerId) => Keys.ApplyProviderModelDefaults(providerId);

}
