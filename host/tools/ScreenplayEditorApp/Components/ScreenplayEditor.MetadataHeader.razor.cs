using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_MetadataHeader
{
    [Parameter]
    public ScreenplayMetadata Metadata { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    protected bool IsCollapsed { get; set; } = false;

    protected void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    protected async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }
}
