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
    /// <summary>Persist/scan domain for the AdminModelsCatalog page. Owns related UI state and behavior.</summary>
    internal sealed class AdminModelsPersist
    {
        private readonly AdminModelsCatalog S;
        public AdminModelsPersist(AdminModelsCatalog host) => S = host;

        internal List<string>? _validationErrors;

        internal CatalogUpdateScanClientResult? _scan;

        internal bool _scanning;


        internal async Task ValidateCatalogAsync()
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            _validationErrors = null;
            try
            {
                if (!S.Raw._showRawJson) S.Raw.SyncModelListToRawJson();
                var res = await S.Api.ValidateModelsCatalogRawAsync(S.Raw._rawJsonText);
                if (!string.IsNullOrWhiteSpace(res.Error) && res.Errors is not { Count: > 0 })
                {
                    S._error = res.Error;
                    return;
                }
                _validationErrors = res.Errors ?? new List<string>();
                S._message = res.Message ?? (res.Ok ? "Validation OK" : "Validation found issues");
                if (!res.Ok && _validationErrors.Count == 0 && !string.IsNullOrWhiteSpace(res.Error))
                    S._error = res.Error;
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }

        internal async Task SaveCatalogAsync()
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            _validationErrors = null;
            try
            {
                if (!S.Raw._showRawJson) S.Raw.SyncModelListToRawJson();
                // Pre-validate so the UI shows the same self-test the server will run
                var pre = await S.Api.ValidateModelsCatalogRawAsync(S.Raw._rawJsonText);
                if (!pre.Ok)
                {
                    _validationErrors = pre.Errors ?? new List<string>();
                    S._error = pre.Message ?? pre.Error ?? "Catalog self-test failed — fix before save.";
                    return;
                }
                var msg = await S.Api.SaveModelsCatalogRawAsync(S.Raw._rawJsonText);
                S._message = msg;
                S.Editor.CloseEditor();
                await S.List.LoadCatalogAsync();
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }

        internal async Task ScanUpdatesAsync()
        {
            _scanning = true;
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                _scan = await S.Api.CheckModelsCatalogUpdatesAsync();
                S._message = $"Scan complete: {_scan.Summary.ChangedFields} changed, {_scan.Summary.NotFoundFields} not found, {_scan.Summary.NewModels} new.";
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                _scanning = false;
                S._busy = false;
            }
        }

        internal void AcceptFieldChange(string modelId, CatalogFieldProbeDto f)
        {
            var m = S.List._modelList.FirstOrDefault(x => string.Equals(x["id"]?.ToString(), modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null || f.LiveValue is null) return;
            try
            {
                ApplyLiveValueToModel(m, f.Field, f.LiveValue);
            }
            catch (Exception ex)
            {
                S._error = $"Accept failed for {modelId}.{f.Field}: {ex.Message}";
                return;
            }
            S.Raw.SyncModelListToRawJson();
            S._message = $"Accepted {modelId}.{f.Field} = {f.LiveValue}. Save to persist.";
            f.CatalogValue = f.LiveValue;
            f.Status = "unchanged";
            f.Message = "Accepted into draft table.";
        }

        /// <summary>
        /// Writes a scan live value into the draft model. Supports dotted paths into nested objects,
        /// e.g. <c>videoCostPerSecondByResolution.720p</c>, and <c>parent.*</c> (all existing child keys).
        /// </summary>
        internal static void ApplyLiveValueToModel(JsonObject model, string fieldPath, string liveValue)
        {
            if (string.IsNullOrWhiteSpace(fieldPath))
                throw new ArgumentException("Field path is empty.");

            var path = fieldPath.Trim();
            // Ignore non-data probe rows
            if (path.StartsWith('(') || path is "live_probe" or "pricing_docs" or "docs.extension" or "docs.generation")
                throw new InvalidOperationException("This probe row is informational and cannot be accepted as a catalog field.");

            JsonNode valueNode = ParseLiveJsonNode(liveValue);

            var dot = path.IndexOf('.');
            if (dot < 0)
            {
                model[path] = valueNode;
                return;
            }

            var parentKey = path[..dot];
            var childKey = path[(dot + 1)..];
            if (string.IsNullOrWhiteSpace(parentKey) || string.IsNullOrWhiteSpace(childKey))
                throw new InvalidOperationException($"Invalid nested field path '{path}'.");

            if (model[parentKey] is not JsonObject nested)
            {
                nested = new JsonObject();
                model[parentKey] = nested;
            }

            if (childKey == "*")
            {
                // Apply live value to every existing child key; if empty, seed common resolution tiers for video maps.
                if (nested.Count == 0
                    && parentKey.Contains("Resolution", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var tier in new[] { "480p", "720p", "1080p" })
                        nested[tier] = valueNode?.DeepClone() ?? valueNode;
                }
                else
                {
                    foreach (var key in nested.Select(kv => kv.Key).ToList())
                        nested[key] = valueNode?.DeepClone() ?? valueNode;
                }
                return;
            }

            nested[childKey] = valueNode;
        }

        internal static JsonNode ParseLiveJsonNode(string liveValue)
        {
            var s = liveValue.Trim();
            if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var iv))
                return JsonValue.Create(iv);
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv))
                return JsonValue.Create(dv);
            if (bool.TryParse(s, out var bv))
                return JsonValue.Create(bv);
            // Try JSON fragment (object/array) before plain string
            if ((s.StartsWith('{') && s.EndsWith('}')) || (s.StartsWith('[') && s.EndsWith(']')))
            {
                try { return JsonNode.Parse(s) ?? JsonValue.Create(s); }
                catch { /* fall through to string */ }
            }
            return JsonValue.Create(s);
        }

        internal void AcceptNewModel(CatalogNewModelHintDto nm)
        {
            if (S.List._modelList.Any(x => string.Equals(x["id"]?.ToString(), nm.Id, StringComparison.OrdinalIgnoreCase)))
            {
                S._error = $"Model '{nm.Id}' already in table.";
                return;
            }
            var obj = new JsonObject
            {
                ["id"] = nm.Id,
                ["displayName"] = nm.Id,
                ["capability"] = nm.SuggestedCapability,
                ["provider"] = nm.Provider,
                ["providerId"] = nm.ProviderId,
                ["enabled"] = true,
                ["labMode"] = true,
                ["labNotes"] = nm.LabNotes ?? "Discovered via update scan",
                ["lastVerifiedAt"] = AdminModelsEditor.Today(),
            };
            S.List._modelList.Add(obj);
            S.Raw.SyncModelListToRawJson();
            S._message = $"Added '{nm.Id}' as LAB. Fill limits/costs, then Save.";
        }
    }
}
