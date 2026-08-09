using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class ToggleSwitch
{
    [Parameter] public bool Value { get; set; }
    [Parameter] public EventCallback<bool> ValueChanged { get; set; }
    [Parameter] public string? Id { get; set; }
    [Parameter] public RenderFragment? Label { get; set; }
    [Parameter] public string LabelClass { get; set; } = "form-check-label";
    [Parameter] public string WrapperClass { get; set; } = "form-check form-switch";
    [Parameter] public bool Disabled { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }

    async Task OnChangeAsync(ChangeEventArgs e)
    {
        var v = e.Value is bool b && b;
        Value = v;
        await ValueChanged.InvokeAsync(v);
    }
}
