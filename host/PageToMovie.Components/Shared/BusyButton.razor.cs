using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class BusyButton
{
    [Parameter] public bool Busy { get; set; }
    /// <summary>Idle label (required for accessibility).</summary>
    [Parameter] public string Label { get; set; } = "";
    /// <summary>Label while Busy. Default: same as Label.</summary>
    [Parameter] public string? BusyLabel { get; set; }
    [Parameter] public string CssClass { get; set; } = "btn btn-primary";
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public string Type { get; set; } = "button";
    [Parameter] public string? Title { get; set; }
    [Parameter] public string? TestId { get; set; }
    [Parameter] public EventCallback OnClick { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }
}
