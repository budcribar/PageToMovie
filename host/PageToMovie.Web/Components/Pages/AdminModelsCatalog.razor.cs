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
    private static readonly string[] Capabilities = { "Chat", "Vision", "Video", "Image", "Audio", "Voice", "LipSync" };

    private bool _loading = true;
    private bool _busy;
    private bool _showRawJson;
    private string? _error;
    private string? _message;
    private string _rawJsonText = "";
    private JsonObject? _rootObj;
    private List<JsonObject> _modelList = new();
    private List<string>? _validationErrors;

    private JsonObject? _editModel;
    private bool _editIsNew;
    private string _editId = "";
    private string _editDisplayName = "";
    private string _editCapability = "Chat";
    private string _editProvider = "Xai";
    private bool _editEnabled = true;
    private bool _editDeprecated;
    private bool _editLabMode;
    private string _editLabNotes = "";
    private string _editEndpointPath = "";
    private int? _editMaxPromptLength;
    private string _editLastVerifiedAt = "";
    private string _editPricingLastReviewedAt = "";
    private string _editPricingNotes = "";
    private int? _editMaxInputTokens;
    private int? _editMaxOutputTokens;
    private double? _editInputCost;
    private double? _editOutputCost;
    private int? _editMinClip;
    private int? _editMaxClip;
    private int? _editAbsMaxClip;
    private int? _editMaxRefs;
    private bool _editSupportsContinue;
    private double? _editExtendCost;
    private double? _editRefImageCost;
    private string _editVideoPerSecJson = "";
    private string _editVideoBaseJson = "";
    private double? _editImageCost;
    private int? _editMaxAudio;
    private bool _editSupportsVocals;

    private string _filterQuery = "";
    private string _filterCapability = "";
    private string _filterProvider = "";
    private string _filterStatus = "";

    private void ResetFilters()
    {
        _filterQuery = "";
        _filterCapability = "";
        _filterProvider = "";
        _filterStatus = "";
    }

    private IEnumerable<JsonObject> FilteredModels => _modelList.Where(m =>
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

    private List<string> AvailableProviders => _modelList
        .Select(m => m["provider"]?.ToString())
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => p)
        .ToList()!;

    protected override async Task OnInitializedAsync() => await LoadCatalogAsync();

    private static bool IsEnabled(JsonObject m) =>
        m.TryGetPropertyValue("enabled", out var en) && en?.GetValue<bool>() == true;

    private static bool IsDeprecated(JsonObject m) =>
        m.TryGetPropertyValue("deprecated", out var dep) && dep?.GetValue<bool>() == true;

    private void ToggleModelDeprecated(JsonObject m)
    {
        var isDep = IsDeprecated(m);
        if (isDep)
            m.Remove("deprecated");
        else
            m["deprecated"] = true;
        SyncModelListToRawJson();
        _message = isDep ? $"Restored model '{m["id"]}' (undeprecated)." : $"Deprecated model '{m["id"]}'. Save to persist.";
    }

    private async Task LoadCatalogAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            var res = await Api.GetModelsCatalogAsync();
            _rawJsonText = res.RawJson;
            ParseRawJson();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ParseRawJson()
    {
        try
        {
            var node = JsonNode.Parse(_rawJsonText);
            if (node is JsonObject obj && obj.TryGetPropertyValue("models", out var arrNode) && arrNode is JsonArray arr)
            {
                _rootObj = obj;
                _modelList = arr.OfType<JsonObject>().ToList();
            }
        }
        catch (Exception ex)
        {
            _error = $"JSON parse error: {ex.Message}";
        }
    }

    private void SyncModelListToRawJson()
    {
        if (_rootObj is null) return;
        var arr = new JsonArray();
        foreach (var m in _modelList)
            arr.Add(JsonNode.Parse(m.ToJsonString()));
        _rootObj["models"] = arr;
        _rawJsonText = _rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void ToggleModelEnabled(JsonObject m)
    {
        m["enabled"] = !IsEnabled(m);
        SyncModelListToRawJson();
    }

    private async Task RemoveModelAsync(JsonObject m)
    {
        var id = m["id"]?.ToString() ?? "?";
        var ok = await JS.InvokeAsync<bool>("confirm", $"Delete model '{id}' from the catalog table? Save is still required to persist.");
        if (!ok) return;
        _modelList.Remove(m);
        if (ReferenceEquals(_editModel, m)) CloseEditor();
        SyncModelListToRawJson();
        _message = $"Removed '{id}' from table. Save to persist.";
    }

    private void BeginAdd()
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

    private void BeginEdit(JsonObject m)
    {
        _editIsNew = false;
        _editModel = m;
        _editId = m["id"]?.ToString() ?? "";
        _editDisplayName = m["displayName"]?.ToString() ?? "";
        _editCapability = m["capability"]?.ToString() ?? "Chat";
        _editProvider = m["provider"]?.ToString() ?? "";
        _editEnabled = IsEnabled(m);
        _editDeprecated = IsDeprecated(m);
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

    private void ClearCapabilityFields()
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

    private void CloseEditor()
    {
        _editModel = null;
        _editIsNew = false;
    }

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private static int? GetInt(JsonObject m, string key)
    {
        if (!m.TryGetPropertyValue(key, out var n) || n is null) return null;
        try { return n.GetValue<int>(); } catch { return null; }
    }

    private static double? GetDouble(JsonObject m, string key)
    {
        if (!m.TryGetPropertyValue(key, out var n) || n is null) return null;
        try { return n.GetValue<double>(); } catch { return null; }
    }

    [Inject] private IAppLocalizer Localizer { get; set; } = default!;

    private void ApplyEditorToList()
    {
        _error = null;
        if (string.IsNullOrWhiteSpace(_editId))
        {
            _error = Localizer["Catalog.ModelIdRequired"];
            return;
        }

        JsonObject obj;
        if (_editIsNew)
        {
            if (_modelList.Any(m => string.Equals(m["id"]?.ToString(), _editId.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                _error = Localizer.Format("Catalog.ModelExists", _editId);
                return;
            }
            obj = new JsonObject();
            _modelList.Add(obj);
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

        SyncModelListToRawJson();
        _message = $"Applied '{_editId}' to table. Run Validate, then Save to persist.";
    }

    private void ReviewAndApplyAsync()
    {
        _editLastVerifiedAt = Today();
        _editPricingLastReviewedAt = Today();
        if (string.IsNullOrWhiteSpace(_editPricingNotes))
            _editPricingNotes = $"Reviewed {Today()} — confirm vendor list prices still match.";
        // Review means production-ready intent: leave lab only if user keeps the switch on.
        ApplyEditorToList();
        _message = $"Marked '{_editId}' reviewed ({Today()}). Clear Lab mode when fields are complete, then Save.";
    }

    private void ReviewModel(JsonObject m)
    {
        BeginEdit(m);
        ReviewAndApplyAsync();
    }

    private static void SetOrRemove(JsonObject obj, string key, string? value)
    {
        if (value is null) obj.Remove(key);
        else obj[key] = value;
    }

    private static void SetOrRemoveNum(JsonObject obj, string key, int? value)
    {
        if (value is null) obj.Remove(key);
        else obj[key] = value.Value;
    }

    private static void SetOrRemoveNum(JsonObject obj, string key, double? value)
    {
        if (value is null) obj.Remove(key);
        else obj[key] = value.Value;
    }

    private static void SetJsonObject(JsonObject obj, string key, string json)
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

    private void ToggleRawJsonEditor()
    {
        _showRawJson = !_showRawJson;
        if (!_showRawJson) ParseRawJson();
    }

    private async Task ValidateCatalogAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        _validationErrors = null;
        try
        {
            if (!_showRawJson) SyncModelListToRawJson();
            var res = await Api.ValidateModelsCatalogRawAsync(_rawJsonText);
            if (!string.IsNullOrWhiteSpace(res.Error) && res.Errors is not { Count: > 0 })
            {
                _error = res.Error;
                return;
            }
            _validationErrors = res.Errors ?? new List<string>();
            _message = res.Message ?? (res.Ok ? "Validation OK" : "Validation found issues");
            if (!res.Ok && _validationErrors.Count == 0 && !string.IsNullOrWhiteSpace(res.Error))
                _error = res.Error;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveCatalogAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        _validationErrors = null;
        try
        {
            if (!_showRawJson) SyncModelListToRawJson();
            // Pre-validate so the UI shows the same self-test the server will run
            var pre = await Api.ValidateModelsCatalogRawAsync(_rawJsonText);
            if (!pre.Ok)
            {
                _validationErrors = pre.Errors ?? new List<string>();
                _error = pre.Message ?? pre.Error ?? "Catalog self-test failed — fix before save.";
                return;
            }
            var msg = await Api.SaveModelsCatalogRawAsync(_rawJsonText);
            _message = msg;
            CloseEditor();
            await LoadCatalogAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private CatalogUpdateScanClientResult? _scan;
    private bool _scanning;

    private async Task ScanUpdatesAsync()
    {
        _scanning = true;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            _scan = await Api.CheckModelsCatalogUpdatesAsync();
            _message = $"Scan complete: {_scan.Summary.ChangedFields} changed, {_scan.Summary.NotFoundFields} not found, {_scan.Summary.NewModels} new.";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _scanning = false;
            _busy = false;
        }
    }

    private void AcceptFieldChange(string modelId, CatalogFieldProbeDto f)
    {
        var m = _modelList.FirstOrDefault(x => string.Equals(x["id"]?.ToString(), modelId, StringComparison.OrdinalIgnoreCase));
        if (m is null || f.LiveValue is null) return;
        try
        {
            ApplyLiveValueToModel(m, f.Field, f.LiveValue);
        }
        catch (Exception ex)
        {
            _error = $"Accept failed for {modelId}.{f.Field}: {ex.Message}";
            return;
        }
        SyncModelListToRawJson();
        _message = $"Accepted {modelId}.{f.Field} = {f.LiveValue}. Save to persist.";
        f.CatalogValue = f.LiveValue;
        f.Status = "unchanged";
        f.Message = "Accepted into draft table.";
    }

    /// <summary>
    /// Writes a scan live value into the draft model. Supports dotted paths into nested objects,
    /// e.g. <c>videoCostPerSecondByResolution.720p</c>, and <c>parent.*</c> (all existing child keys).
    /// </summary>
    private static void ApplyLiveValueToModel(JsonObject model, string fieldPath, string liveValue)
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

    private static JsonNode ParseLiveJsonNode(string liveValue)
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

    private void AcceptNewModel(CatalogNewModelHintDto nm)
    {
        if (_modelList.Any(x => string.Equals(x["id"]?.ToString(), nm.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _error = $"Model '{nm.Id}' already in table.";
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
            ["lastVerifiedAt"] = Today(),
        };
        _modelList.Add(obj);
        SyncModelListToRawJson();
        _message = $"Added '{nm.Id}' as LAB. Fill limits/costs, then Save.";
    }

    private static string StatusRowClass(string status) => status switch
    {
        "unchanged" => "table-success",
        "changed" => "table-danger",
        "not_found" => "table-warning",
        "error" => "table-warning",
        _ => "",
    };

    private static string StatusBadgeClass(string status) => status switch
    {
        "unchanged" => "bg-success",
        "changed" => "bg-danger",
        "not_found" => "bg-warning text-dark",
        "error" => "bg-warning text-dark",
        _ => "bg-secondary",
    };

    private string GetCapBadgeClass(string cap) => cap switch
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

    private Task OnCatalogSearchChanged(string? value)
    {
        _filterQuery = value ?? "";
        return Task.CompletedTask;
    }

}

