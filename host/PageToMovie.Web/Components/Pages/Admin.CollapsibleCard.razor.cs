using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin_CollapsibleCard
{
    [Parameter] public string Title { get; set; } = "";
    [Parameter] public string? Badge { get; set; }
    [Parameter] public string BadgeClass { get; set; } = "text-bg-secondary";
    [Parameter] public bool Expanded { get; set; }
    [Parameter] public EventCallback<bool> ExpandedChanged { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private async Task Toggle() => await ExpandedChanged.InvokeAsync(!Expanded);
}
