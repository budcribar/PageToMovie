using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class CapabilityLockedControl
{
    [Parameter] public bool Enabled { get; set; }
    [Parameter] public string BlockedReason { get; set; } = "This feature needs a model in Settings.";
    [Parameter] public string SettingsHref { get; set; } = "/configuration#api-keys";
    [Parameter] public string? TitleWhenEnabled { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public string TestId { get; set; } = "capability-locked";
    [Parameter] public RenderFragment? ChildContent { get; set; }
}
