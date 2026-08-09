using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Layout;

public partial class NavMenu_NavItem
{
    [Parameter] public bool Enabled { get; set; }
    [Parameter] public string Href { get; set; } = "";
    [Parameter] public string Title { get; set; } = "";
    [Parameter] public string IconClass { get; set; } = "";
    [Parameter] public string Label { get; set; } = "";
    [Parameter] public string? TestId { get; set; }
    [Parameter] public bool Collapsed { get; set; }
}
