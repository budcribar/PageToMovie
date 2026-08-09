using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_BadgePill : ComponentBase
{
    [Parameter]
    public string Text { get; set; } = "";

    [Parameter]
    public ComponentVariant Variant { get; set; } = ComponentVariant.Secondary;

    [Parameter]
    public string CssClass { get; set; } = "";
}
