using System.Globalization;
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
    public sealed class AdminModelsList
    {
        private readonly AdminModelsCatalog S;
        public AdminModelsList(AdminModelsCatalog host) => S = host;

        internal bool _loading = true;

        internal List<JsonObject> _modelList = new();

        internal string _filterQuery = "";

        internal string _filterCapability = "";

        internal string _filterProvider = "";

        internal string _filterStatus = "";

        internal string _sortBy = "";

        internal bool _sortAscending = true;

        private const string Deprecated = "deprecated";

        internal const string SortEnabled = "enabled";
        internal const string SortId = "id";
        internal const string SortName = "name";
        internal const string SortCapability = "capability";
        internal const string SortProvider = "provider";
        internal const string SortReviewed = "reviewed";

        internal void ResetFilters()
        {
            _filterQuery = "";
            _filterCapability = "";
            _filterProvider = "";
            _filterStatus = "";
        }

        internal void ToggleSort(string column)
        {
            if (_sortBy == column)
                _sortAscending = !_sortAscending;
            else
            {
                _sortBy = column;
                _sortAscending = true;
            }
        }

        internal string SortArrow(string column)
        {
            if (_sortBy != column) return "⇅";
            return _sortAscending ? "▲" : "▼";
        }

        internal IEnumerable<JsonObject> FilteredModels
        {
            get
            {
                var filtered = _modelList.Where(MatchesFilters);
                return _sortBy switch
                {
                    SortEnabled => OrderBy(filtered, IsEnabled),
                    SortId => OrderByString(filtered, m => m["id"]?.ToString()),
                    SortName => OrderByString(filtered, m => m["displayName"]?.ToString()),
                    SortCapability => OrderByString(filtered, m => m["capability"]?.ToString()),
                    SortProvider => OrderByString(filtered, m => m[SortProvider]?.ToString()),
                    SortReviewed => OrderReviewed(filtered),
                    _ => filtered
                };
            }
        }

        private IEnumerable<JsonObject> OrderBy<T>(IEnumerable<JsonObject> src, Func<JsonObject, T> key) =>
            _sortAscending ? src.OrderBy(key) : src.OrderByDescending(key);

        private IEnumerable<JsonObject> OrderByString(IEnumerable<JsonObject> src, Func<JsonObject, string?> key)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            return _sortAscending
                ? src.OrderBy(m => key(m) ?? "", cmp)
                : src.OrderByDescending(m => key(m) ?? "", cmp);
        }

        private IEnumerable<JsonObject> OrderReviewed(IEnumerable<JsonObject> src)
        {
            var ordered = _sortAscending
                ? src.OrderBy(ReviewedPrimary)
                : src.OrderByDescending(ReviewedPrimary);
            return _sortAscending
                ? ordered.ThenBy(ReviewedSecondary)
                : ordered.ThenByDescending(ReviewedSecondary);
        }

        private static DateTime ReviewedPrimary(JsonObject m) => ParseCatalogDate(m["lastVerifiedAt"]?.ToString());

        private static DateTime ReviewedSecondary(JsonObject m) => ParseCatalogDate(m["pricingLastReviewedAt"]?.ToString());

        private static DateTime ParseCatalogDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt)
                ? dt
                : DateTime.MinValue;
        }

        private bool MatchesFilters(JsonObject m) =>
            MatchesQuery(m) && MatchesCapability(m) && MatchesProvider(m) && MatchesStatus(m);

        private bool MatchesQuery(JsonObject m)
        {
            if (string.IsNullOrWhiteSpace(_filterQuery)) return true;
            var q = _filterQuery.Trim();
            var modelId = m["id"]?.ToString() ?? "";
            var displayName = m["displayName"]?.ToString() ?? "";
            var prov = m[SortProvider]?.ToString() ?? "";
            return modelId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || displayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || prov.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesCapability(JsonObject m)
        {
            if (string.IsNullOrWhiteSpace(_filterCapability)) return true;
            var cap = m["capability"]?.ToString() ?? "";
            return string.Equals(cap, _filterCapability, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesProvider(JsonObject m)
        {
            if (string.IsNullOrWhiteSpace(_filterProvider)) return true;
            var prov = m[SortProvider]?.ToString() ?? "";
            return string.Equals(prov, _filterProvider, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesStatus(JsonObject m)
        {
            var isDeprecated = IsDeprecated(m);
            if (_filterStatus == Deprecated) return isDeprecated;
            if (_filterStatus == "all") return true;
            if (isDeprecated) return false;
            if (_filterStatus == SortEnabled) return IsEnabled(m);
            if (_filterStatus == "disabled") return !IsEnabled(m);
            if (_filterStatus == "lab")
                return m.TryGetPropertyValue("labMode", out var labNode) && labNode?.GetValue<bool>() == true;
            // Default (empty filterStatus): hide deprecated models!
            return true;
        }

        internal List<string> GetAvailableProviders() => _modelList
            .Select(m => m[SortProvider]?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();

        internal static bool IsEnabled(JsonObject m) =>
            m.TryGetPropertyValue(SortEnabled, out var en) && en?.GetValue<bool>() == true;

        internal static bool IsDeprecated(JsonObject m) =>
            m.TryGetPropertyValue(Deprecated, out var dep) && dep?.GetValue<bool>() == true;

        internal static string RowClass(JsonObject m)
        {
            if (IsDeprecated(m)) return "table-dark opacity-75";
            if (IsEnabled(m)) return "";
            return "table-secondary";
        }

        internal void ToggleModelDeprecated(JsonObject m)
        {
            var isDep = IsDeprecated(m);
            if (isDep)
                m.Remove(Deprecated);
            else
                m[Deprecated] = true;
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
            m[SortEnabled] = !IsEnabled(m);
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

        internal static string GetCapBadgeClass(string cap) => cap switch
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
