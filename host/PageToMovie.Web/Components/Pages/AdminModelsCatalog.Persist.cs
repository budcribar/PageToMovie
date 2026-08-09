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

// Forwarders: AdminModelsPersist → Host.*
public partial class AdminModelsCatalog
{
    internal Task ValidateCatalogAsync() => Persist.ValidateCatalogAsync();

    internal Task SaveCatalogAsync() => Persist.SaveCatalogAsync();

    internal Task ScanUpdatesAsync() => Persist.ScanUpdatesAsync();

    internal void AcceptFieldChange(string modelId, CatalogFieldProbeDto f) => Persist.AcceptFieldChange(modelId, f);

    internal static void ApplyLiveValueToModel(JsonObject model, string fieldPath, string liveValue) =>
        AdminModelsPersist.ApplyLiveValueToModel(model, fieldPath, liveValue);

    internal static JsonNode ParseLiveJsonNode(string liveValue) => AdminModelsPersist.ParseLiveJsonNode(liveValue);

    internal void AcceptNewModel(CatalogNewModelHintDto nm) => Persist.AcceptNewModel(nm);
}
