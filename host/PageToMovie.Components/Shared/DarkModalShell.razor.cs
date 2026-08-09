using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class DarkModalShell
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public RenderFragment? Title { get; set; }
    [Parameter] public string HeaderClass { get; set; } = "d-flex justify-content-between align-items-center";
    [Parameter] public string TitleClass { get; set; } = "m-0";
    [Parameter] public string DialogClass { get; set; } = "modal-dialog-centered";
    [Parameter] public string BodyClass { get; set; } = "";
    [Parameter] public string BackdropOpacity { get; set; } = "0.6";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? Footer { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }
}
