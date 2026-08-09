using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_BadgePill : ComponentBase
{
    [Parameter]
    public string Text { get; set; } = "";

    [Parameter]
    public ComponentVariant Variant { get; set; } = ComponentVariant.Secondary;

    [Parameter]
    public string CssClass { get; set; } = "";
}
