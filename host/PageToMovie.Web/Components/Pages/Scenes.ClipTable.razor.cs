using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_ClipTable : PageSliceComponent
{
    [CascadingParameter] public Scenes Host { get; set; } = default;
    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }

    [CascadingParameter] public Scenes.ScenesClipSelection? ClipSel { get; set; }

    [CascadingParameter] public Scenes.ScenesClipForm? ClipForm { get; set; }


    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }

    [CascadingParameter] public Scenes.ScenesClipVersions? ClipVer { get; set; }

    private static string ClipRowClass(bool active, bool isChecked)
    {
        if (active) return "table-primary";
        if (isChecked) return "table-warning";
        return "";
    }

    private static string BeatIdDisplay(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "—";
        if (id.Length > 14) return id[..12] + "…";
        return id;
    }
}
