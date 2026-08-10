using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

using PageToMovie.Engine;

namespace PageToMovie.Web.Components;

public partial class InlineAlert
{
    [Parameter] public string? Message { get; set; }
    [Parameter] public NotificationSeverity? Severity { get; set; }
    [Parameter] public string Variant { get; set; } = "danger";
    [Parameter] public string CssClass { get; set; } = "";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }

    public string EffectiveVariant => Severity switch
    {
        NotificationSeverity.Info => "info",
        NotificationSeverity.Success => "success",
        NotificationSeverity.Warning => "warning",
        NotificationSeverity.Error => "danger",
        _ => Variant
    };
}
