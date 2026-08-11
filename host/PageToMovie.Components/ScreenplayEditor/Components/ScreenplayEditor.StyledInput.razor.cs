using Microsoft.AspNetCore.Components;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_StyledInput : ComponentBase
{
    [Parameter]
    public string Label { get; set; } = "";

    [Parameter]
    public string Value { get; set; } = "";

    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    [Parameter]
    public EventCallback OnChanged { get; set; }

    [Parameter]
    public string Placeholder { get; set; } = "";

    [Parameter]
    public string Type { get; set; } = "text";

    [Parameter]
    public string CssClass { get; set; } = "";

    [Parameter]
    public string InputCssClass { get; set; } = "";

    public async Task HandleChange(ChangeEventArgs e)
    {
        string newVal = e.Value?.ToString() ?? "";
        Value = newVal;
        if (ValueChanged.HasDelegate)
        {
            await ValueChanged.InvokeAsync(newVal);
        }
        if (OnChanged.HasDelegate)
        {
            await OnChanged.InvokeAsync();
        }
    }
}
