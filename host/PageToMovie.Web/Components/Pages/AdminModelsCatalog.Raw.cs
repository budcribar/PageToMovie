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

// Forwarders: AdminModelsRaw → Host.*
public partial class AdminModelsCatalog
{
    internal void ParseRawJson() => Raw.ParseRawJson();

    internal void SyncModelListToRawJson() => Raw.SyncModelListToRawJson();

    internal void ToggleRawJsonEditor() => Raw.ToggleRawJsonEditor();
}
