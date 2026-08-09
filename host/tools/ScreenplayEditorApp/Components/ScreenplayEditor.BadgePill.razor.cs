using Microsoft.AspNetCore.Components;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_BadgePill : ComponentBase
{
    [Parameter]
    public string Text { get; set; } = "";

    [Parameter]
    public string Variant { get; set; } = "secondary"; // primary, info, success, warning, secondary, dark

    [Parameter]
    public string CssClass { get; set; } = "";

    public string VariantClass => Variant.StartsWith("text-bg-") || Variant.StartsWith("bg-") ? Variant : $"text-bg-{Variant}";
}
