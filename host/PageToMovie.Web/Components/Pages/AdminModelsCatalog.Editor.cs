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

// Forwarders: AdminModelsEditor → Host.*
public partial class AdminModelsCatalog
{
    internal void BeginAdd() => Editor.BeginAdd();

    internal void BeginEdit(JsonObject m) => Editor.BeginEdit(m);

    internal void ClearCapabilityFields() => Editor.ClearCapabilityFields();

    internal void CloseEditor() => Editor.CloseEditor();

    internal static string Today() => AdminModelsEditor.Today();

    internal static int? GetInt(JsonObject m, string key) => AdminModelsEditor.GetInt(m, key);

    internal static double? GetDouble(JsonObject m, string key) => AdminModelsEditor.GetDouble(m, key);

    internal void ApplyEditorToList() => Editor.ApplyEditorToList();

    internal void ReviewAndApplyAsync() => Editor.ReviewAndApplyAsync();

    internal void ReviewModel(JsonObject m) => Editor.ReviewModel(m);

    internal static void SetOrRemove(JsonObject obj, string key, string? value) => AdminModelsEditor.SetOrRemove(obj, key, value);

    internal static void SetOrRemoveNum(JsonObject obj, string key, int? value) => AdminModelsEditor.SetOrRemoveNum(obj, key, value);

    internal static void SetOrRemoveNum(JsonObject obj, string key, double? value) => AdminModelsEditor.SetOrRemoveNum(obj, key, value);

    internal static void SetJsonObject(JsonObject obj, string key, string json) => AdminModelsEditor.SetJsonObject(obj, key, json);
}
