using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_CreditsHeader : ComponentBase
{
    [Parameter]
    public ScreenplayCredits Credits { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }
}
