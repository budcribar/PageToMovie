using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class EmptyState
{
    [Parameter] public string? Title { get; set; }
    [Parameter] public string? Message { get; set; }
    [Parameter] public string CssClass { get; set; } = "text-muted py-3";
    [Parameter] public string? ActionHref { get; set; }
    [Parameter] public string? ActionLabel { get; set; }
    [Parameter] public EventCallback OnAction { get; set; }
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    bool HasContent =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Message)
        || OnAction.HasDelegate
        || !string.IsNullOrWhiteSpace(ActionHref)
        || ChildContent is not null;
}
