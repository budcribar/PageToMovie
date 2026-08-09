using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class ReadinessGateBanner
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public string? Reason { get; set; }
    [Parameter] public string FallbackReason { get; set; } = "Complete the previous step first.";
    [Parameter] public string? ActionHref { get; set; }
    [Parameter] public string ActionLabel { get; set; } = "Open →";
    [Parameter] public string CssClass { get; set; } = "text-muted mb-3";
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }
}
