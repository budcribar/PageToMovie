using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_ActionButton : ComponentBase
{
    [Parameter]
    public string Text { get; set; } = "";

    [Parameter]
    public string Icon { get; set; } = "";

    [Parameter]
    public ComponentVariant Variant { get; set; } = ComponentVariant.Secondary;

    [Parameter]
    public string CssClass { get; set; } = "";

    [Parameter]
    public string Title { get; set; } = "";

    [Parameter]
    public bool Disabled { get; set; } = false;

    [Parameter]
    public EventCallback OnClick { get; set; }
}
