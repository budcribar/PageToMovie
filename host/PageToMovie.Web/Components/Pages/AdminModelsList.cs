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

public partial class AdminModelsCatalog
{
    /// <summary>List/filter domain for the AdminModelsCatalog page. Owns related UI state and behavior.</summary>
    internal sealed class AdminModelsList
    {
        private readonly AdminModelsCatalog S;
        public AdminModelsList(AdminModelsCatalog host) => S = host;

        internal bool _loading = true;

        internal List<JsonObject> _modelList = new();

        internal string _filterQuery = "";

        internal string _filterCapability = "";

        internal string _filterProvider = "";

        internal string _filterStatus = "";


        internal void ResetFilters()
        {
            _filterQuery = "";
            _filterCapability = "";
            _filterProvider = "";
            _filterStatus = "";
        }

        internal IEnumerable<JsonObject> FilteredModels => _modelList.Where(m =>
        {
            var modelId = m["id"]?.ToString() ?? "";
            var displayName = m["displayName"]?.ToString() ?? "";
            var cap = m["capability"]?.ToString() ?? "";
            var prov = m["provider"]?.ToString() ?? "";
            var isEnabled = IsEnabled(m);
            var isLab = m.TryGetPropertyValue("labMode", out var labNode) && labNode?.GetValue<bool>() == true;
            var isDeprecated = IsDeprecated(m);

            if (!string.IsNullOrWhiteSpace(_filterQuery))
            {
                var q = _filterQuery.Trim();
                if (!modelId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !displayName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !prov.Contains(q, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(_filterCapability) && !string.Equals(cap, _filterCapability, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(_filterProvider) && !string.Equals(prov, _filterProvider, StringComparison.OrdinalIgnoreCase))
                return false;

            if (_filterStatus == "enabled") return isEnabled && !isDeprecated;
            if (_filterStatus == "disabled") return !isEnabled && !isDeprecated;
            if (_filterStatus == "lab") return isLab && !isDeprecated;
            if (_filterStatus == "deprecated") return isDeprecated;
            if (_filterStatus == "all") return true;

            // Default (empty filterStatus): hide deprecated models!
            return !isDeprecated;
        });

        internal List<string> AvailableProviders => _modelList
            .Select(m => m["provider"]?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList()!;

        internal static bool IsEnabled(JsonObject m) =>
            m.TryGetPropertyValue("enabled", out var en) && en?.GetValue<bool>() == true;

        internal static bool IsDeprecated(JsonObject m) =>
            m.TryGetPropertyValue("deprecated", out var dep) && dep?.GetValue<bool>() == true;

        internal void ToggleModelDeprecated(JsonObject m)
        {
            var isDep = IsDeprecated(m);
            if (isDep)
                m.Remove("deprecated");
            else
                m["deprecated"] = true;
            S.Raw.SyncModelListToRawJson();
            S._message = isDep ? $"Restored model '{m["id"]}' (undeprecated)." : $"Deprecated model '{m["id"]}'. Save to persist.";
        }

        internal async Task LoadCatalogAsync()
        {
            _loading = true;
            S._error = null;
            try
            {
                var res = await S.Api.GetModelsCatalogAsync();
                S.Raw._rawJsonText = res.RawJson;
                S.Raw.ParseRawJson();
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                _loading = false;
            }
        }

        internal void ToggleModelEnabled(JsonObject m)
        {
            m["enabled"] = !IsEnabled(m);
            S.Raw.SyncModelListToRawJson();
        }

        internal async Task RemoveModelAsync(JsonObject m)
        {
            var id = m["id"]?.ToString() ?? "?";
            var ok = await S.JS.InvokeAsync<bool>("confirm", $"Delete model '{id}' from the catalog table? Save is still required to persist.");
            if (!ok) return;
            _modelList.Remove(m);
            if (ReferenceEquals(S.Editor._editModel, m)) S.Editor.CloseEditor();
            S.Raw.SyncModelListToRawJson();
            S._message = $"Removed '{id}' from table. Save to persist.";
        }

        internal Task OnCatalogSearchChanged(string? value)
        {
            _filterQuery = value ?? "";
            return Task.CompletedTask;
        }

        internal static string StatusRowClass(string status) => status switch
        {
            "unchanged" => "table-success",
            "changed" => "table-danger",
            "not_found" => "table-warning",
            "error" => "table-warning",
            _ => "",
        };

        internal static string StatusBadgeClass(string status) => status switch
        {
            "unchanged" => "bg-success",
            "changed" => "bg-danger",
            "not_found" => "bg-warning text-dark",
            "error" => "bg-warning text-dark",
            _ => "bg-secondary",
        };

        internal string GetCapBadgeClass(string cap) => cap switch
        {
            "Chat" => "bg-primary",
            "Video" => "bg-purple",
            "Image" => "bg-info text-dark",
            "Vision" => "bg-warning text-dark",
            "Audio" => "bg-success",
            "Voice" => "bg-secondary",
            "LipSync" => "bg-dark",
            _ => "bg-secondary"
        };
    }
}
