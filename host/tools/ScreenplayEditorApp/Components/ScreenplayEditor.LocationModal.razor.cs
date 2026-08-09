using Microsoft.AspNetCore.Components;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_LocationModal : ComponentBase
{
    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    [Parameter]
    public List<string> Locations { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    public string NewLocationName { get; set; } = "";

    public async Task Close()
    {
        IsOpen = false;
        if (IsOpenChanged.HasDelegate)
        {
            await IsOpenChanged.InvokeAsync(false);
        }
    }

    public async Task AddLocation()
    {
        if (!string.IsNullOrWhiteSpace(NewLocationName))
        {
            string upper = NewLocationName.Trim().ToUpperInvariant();
            if (!Locations.Contains(upper))
            {
                Locations.Add(upper);
                NewLocationName = "";
                if (OnChangedCallback.HasDelegate)
                {
                    await OnChangedCallback.InvokeAsync();
                }
            }
        }
    }

    public async Task RemoveLocation(int index)
    {
        if (index >= 0 && index < Locations.Count)
        {
            Locations.RemoveAt(index);
            if (OnChangedCallback.HasDelegate)
            {
                await OnChangedCallback.InvokeAsync();
            }
        }
    }
}
