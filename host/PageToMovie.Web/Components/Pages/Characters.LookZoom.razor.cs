using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Characters_LookZoom
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public string? Label { get; set; }
    [Parameter] public string? ImageUrl { get; set; }
    [Parameter] public double Scale { get; set; } = 1;
    [Parameter] public bool CanNavigate { get; set; }
    [Parameter] public bool UseDisabled { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnPrev { get; set; }
    [Parameter] public EventCallback OnNext { get; set; }
    [Parameter] public EventCallback OnToggleScale { get; set; }
    [Parameter] public EventCallback OnUse { get; set; }
}
