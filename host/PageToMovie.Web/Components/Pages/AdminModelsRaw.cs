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
    /// <summary>Raw JSON domain for the AdminModelsCatalog page. Owns related UI state and behavior.</summary>
    internal sealed class AdminModelsRaw
    {
        private readonly AdminModelsCatalog S;
        public AdminModelsRaw(AdminModelsCatalog host) => S = host;

        internal bool _showRawJson;
        internal string _rawJsonText = "";
        internal JsonObject? _rootObj;


        internal void ParseRawJson()
        {
            try
            {
                var node = JsonNode.Parse(_rawJsonText);
                if (node is JsonObject obj && obj.TryGetPropertyValue("models", out var arrNode) && arrNode is JsonArray arr)
                {
                    _rootObj = obj;
                    S.List._modelList = arr.OfType<JsonObject>().ToList();
                }
            }
            catch (Exception ex)
            {
                S._error = $"JSON parse error: {ex.Message}";
            }
        }

        internal void SyncModelListToRawJson()
        {
            if (_rootObj is null) return;
            var arr = new JsonArray();
            foreach (var m in S.List._modelList)
                arr.Add(JsonNode.Parse(m.ToJsonString()));
            _rootObj["models"] = arr;
            _rawJsonText = _rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        internal void ToggleRawJsonEditor()
        {
            _showRawJson = !_showRawJson;
            if (!_showRawJson) ParseRawJson();
        }
    }
}
