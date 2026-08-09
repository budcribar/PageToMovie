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
    /// <summary>Editor domain for the AdminModelsCatalog page. Owns related UI state and behavior.</summary>
    internal sealed class AdminModelsEditor
    {
        private readonly AdminModelsCatalog S;
        public AdminModelsEditor(AdminModelsCatalog host) => S = host;

        internal JsonObject? _editModel;
        internal bool _editIsNew;
        internal string _editId = "";
        internal string _editDisplayName = "";
        internal string _editCapability = "Chat";
        internal string _editProvider = "Xai";
        internal bool _editEnabled = true;
        internal bool _editDeprecated;
        internal bool _editLabMode;
        internal string _editLabNotes = "";
        internal string _editEndpointPath = "";
        internal int? _editMaxPromptLength;
        internal string _editLastVerifiedAt = "";
        internal string _editPricingLastReviewedAt = "";
        internal string _editPricingNotes = "";
        internal int? _editMaxInputTokens;
        internal int? _editMaxOutputTokens;
        internal double? _editInputCost;
        internal double? _editOutputCost;
        internal int? _editMinClip;
        internal int? _editMaxClip;
        internal int? _editAbsMaxClip;
        internal int? _editMaxRefs;
        internal bool _editSupportsContinue;
        internal double? _editExtendCost;
        internal double? _editRefImageCost;
        internal string _editVideoPerSecJson = "";
        internal string _editVideoBaseJson = "";
        internal double? _editImageCost;
        internal int? _editMaxAudio;
        internal bool _editSupportsVocals;


        internal void BeginAdd()
        {
            _editIsNew = true;
            _editModel = new JsonObject();
            _editId = "";
            _editDisplayName = "";
            _editCapability = "Chat";
            _editProvider = "Xai";
            _editEnabled = true;
            _editDeprecated = false;
            _editLabMode = true; // new models start as lab until reviewed
            _editLabNotes = "New model — fill required fields then clear Lab mode";
            _editEndpointPath = "";
            _editMaxPromptLength = null;
            _editLastVerifiedAt = Today();
            _editPricingLastReviewedAt = Today();
            _editPricingNotes = "";
            ClearCapabilityFields();
        }

        internal void BeginEdit(JsonObject m)
        {
            _editIsNew = false;
            _editModel = m;
            _editId = m["id"]?.ToString() ?? "";
            _editDisplayName = m["displayName"]?.ToString() ?? "";
            _editCapability = m["capability"]?.ToString() ?? "Chat";
            _editProvider = m["provider"]?.ToString() ?? "";
            _editEnabled = AdminModelsList.IsEnabled(m);
            _editDeprecated = AdminModelsList.IsDeprecated(m);
            _editLabMode = m.TryGetPropertyValue("labMode", out var lm) && lm?.GetValue<bool>() == true;
            _editLabNotes = m["labNotes"]?.ToString() ?? "";
            _editEndpointPath = m["endpointPath"]?.ToString() ?? "";
            _editMaxPromptLength = GetInt(m, "maxPromptLength");
            _editLastVerifiedAt = m["lastVerifiedAt"]?.ToString() ?? "";
            _editPricingLastReviewedAt = m["pricingLastReviewedAt"]?.ToString() ?? "";
            _editPricingNotes = m["pricingNotes"]?.ToString() ?? "";
            _editMaxInputTokens = GetInt(m, "maxInputTokens");
            _editMaxOutputTokens = GetInt(m, "maxOutputTokens");
            _editInputCost = GetDouble(m, "inputCostPerMillionTokens");
            _editOutputCost = GetDouble(m, "outputCostPerMillionTokens");
            _editMinClip = GetInt(m, "minClipDurationSeconds");
            _editMaxClip = GetInt(m, "maxClipDurationSeconds");
            _editAbsMaxClip = GetInt(m, "absMaxClipDurationSeconds");
            _editMaxRefs = GetInt(m, "maxReferenceImages");
            _editSupportsContinue = m.TryGetPropertyValue("supportsVideoContinue", out var sc) && sc?.GetValue<bool>() == true;
            _editExtendCost = GetDouble(m, "videoExtendCostPerSecond");
            _editRefImageCost = GetDouble(m, "videoReferenceImageCost");
            _editVideoPerSecJson = m["videoCostPerSecondByResolution"]?.ToJsonString() ?? "";
            _editVideoBaseJson = m["videoBaseCostByResolution"]?.ToJsonString() ?? "";
            _editImageCost = GetDouble(m, "imageCostPerImage");
            _editMaxAudio = GetInt(m, "maxAudioDurationSeconds");
            _editSupportsVocals = m.TryGetPropertyValue("supportsVocals", out var sv) && sv?.GetValue<bool>() == true;
        }

        internal void ClearCapabilityFields()
        {
            _editMaxInputTokens = _editMaxOutputTokens = null;
            _editInputCost = _editOutputCost = null;
            _editMinClip = _editMaxClip = _editAbsMaxClip = _editMaxRefs = null;
            _editSupportsContinue = false;
            _editExtendCost = _editRefImageCost = _editImageCost = null;
            _editVideoPerSecJson = _editVideoBaseJson = "";
            _editMaxAudio = null;
            _editSupportsVocals = false;
        }

        internal void CloseEditor()
        {
            _editModel = null;
            _editIsNew = false;
        }

        internal static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

        internal static int? GetInt(JsonObject m, string key)
        {
            if (!m.TryGetPropertyValue(key, out var n) || n is null) return null;
            try { return n.GetValue<int>(); } catch { return null; }
        }

        internal static double? GetDouble(JsonObject m, string key)
        {
            if (!m.TryGetPropertyValue(key, out var n) || n is null) return null;
            try { return n.GetValue<double>(); } catch { return null; }
        }

        internal void ApplyEditorToList()
        {
            S._error = null;
            if (string.IsNullOrWhiteSpace(_editId))
            {
                S._error = S.Localizer["Catalog.ModelIdRequired"];
                return;
            }

            JsonObject obj;
            if (_editIsNew)
            {
                if (S.List._modelList.Any(m => string.Equals(m["id"]?.ToString(), _editId.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    S._error = S.Localizer.Format("Catalog.ModelExists", _editId);
                    return;
                }
                obj = new JsonObject();
                S.List._modelList.Add(obj);
                _editModel = obj;
                _editIsNew = false;
            }
            else
            {
                obj = _editModel ?? throw new InvalidOperationException("No model selected");
            }

            obj["id"] = _editId.Trim();
            obj["displayName"] = string.IsNullOrWhiteSpace(_editDisplayName) ? _editId.Trim() : _editDisplayName.Trim();
            obj["capability"] = _editCapability;
            obj["provider"] = _editProvider.Trim();
            obj["enabled"] = _editEnabled;
            if (_editDeprecated)
                obj["deprecated"] = true;
            else
                obj.Remove("deprecated");
            obj["labMode"] = _editLabMode;
            if (_editLabMode)
                obj["labNotes"] = string.IsNullOrWhiteSpace(_editLabNotes) ? "Lab model — incomplete by design" : _editLabNotes.Trim();
            else
                obj.Remove("labNotes");
            SetOrRemove(obj, "endpointPath", string.IsNullOrWhiteSpace(_editEndpointPath) ? null : _editEndpointPath.Trim());
            SetOrRemoveNum(obj, "maxPromptLength", _editMaxPromptLength);
            SetOrRemove(obj, "lastVerifiedAt", string.IsNullOrWhiteSpace(_editLastVerifiedAt) ? null : _editLastVerifiedAt.Trim());
            SetOrRemove(obj, "pricingLastReviewedAt", string.IsNullOrWhiteSpace(_editPricingLastReviewedAt) ? null : _editPricingLastReviewedAt.Trim());
            SetOrRemove(obj, "pricingNotes", string.IsNullOrWhiteSpace(_editPricingNotes) ? null : _editPricingNotes.Trim());

            if (_editCapability is "Chat" or "Vision")
            {
                SetOrRemoveNum(obj, "maxInputTokens", _editMaxInputTokens);
                SetOrRemoveNum(obj, "maxOutputTokens", _editMaxOutputTokens);
                SetOrRemoveNum(obj, "inputCostPerMillionTokens", _editInputCost);
                SetOrRemoveNum(obj, "outputCostPerMillionTokens", _editOutputCost);
            }
            if (_editCapability == "Video")
            {
                SetOrRemoveNum(obj, "minClipDurationSeconds", _editMinClip);
                SetOrRemoveNum(obj, "maxClipDurationSeconds", _editMaxClip);
                SetOrRemoveNum(obj, "absMaxClipDurationSeconds", _editAbsMaxClip);
                SetOrRemoveNum(obj, "maxReferenceImages", _editMaxRefs);
                obj["supportsVideoContinue"] = _editSupportsContinue;
                SetOrRemoveNum(obj, "videoExtendCostPerSecond", _editSupportsContinue ? _editExtendCost : null);
                SetOrRemoveNum(obj, "videoReferenceImageCost", _editRefImageCost);
                SetJsonObject(obj, "videoCostPerSecondByResolution", _editVideoPerSecJson);
                SetJsonObject(obj, "videoBaseCostByResolution", _editVideoBaseJson);
            }
            if (_editCapability == "Image")
            {
                SetOrRemoveNum(obj, "maxReferenceImages", _editMaxRefs);
                SetOrRemoveNum(obj, "imageCostPerImage", _editImageCost);
            }
            if (_editCapability == "Audio")
            {
                SetOrRemoveNum(obj, "maxAudioDurationSeconds", _editMaxAudio);
                obj["supportsVocals"] = _editSupportsVocals;
            }

            S.Raw.SyncModelListToRawJson();
            S._message = $"Applied '{_editId}' to table. Run Validate, then Save to persist.";
        }

        internal void ReviewAndApplyAsync()
        {
            _editLastVerifiedAt = Today();
            _editPricingLastReviewedAt = Today();
            if (string.IsNullOrWhiteSpace(_editPricingNotes))
                _editPricingNotes = $"Reviewed {Today()} — confirm vendor list prices still match.";
            // Review means production-ready intent: leave lab only if user keeps the switch on.
            ApplyEditorToList();
            S._message = $"Marked '{_editId}' reviewed ({Today()}). Clear Lab mode when fields are complete, then Save.";
        }

        internal void ReviewModel(JsonObject m)
        {
            BeginEdit(m);
            ReviewAndApplyAsync();
        }

        internal static void SetOrRemove(JsonObject obj, string key, string? value)
        {
            if (value is null) obj.Remove(key);
            else obj[key] = value;
        }

        internal static void SetOrRemoveNum(JsonObject obj, string key, int? value)
        {
            if (value is null) obj.Remove(key);
            else obj[key] = value.Value;
        }

        internal static void SetOrRemoveNum(JsonObject obj, string key, double? value)
        {
            if (value is null) obj.Remove(key);
            else obj[key] = value.Value;
        }

        internal static void SetJsonObject(JsonObject obj, string key, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                obj.Remove(key);
                return;
            }
            try
            {
                var node = JsonNode.Parse(json);
                if (node is JsonObject)
                    obj[key] = node;
                else
                    throw new InvalidOperationException("Expected a JSON object");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{key}: {ex.Message}");
            }
        }
    }
}
