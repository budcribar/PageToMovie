using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_MetadataHeader
{
    [Parameter]
    public ScreenplayMetadata Metadata { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    public bool IsCollapsed { get; set; } = false;

    public void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }
}
