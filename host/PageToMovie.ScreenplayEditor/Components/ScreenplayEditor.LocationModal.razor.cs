using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_LocationModal : ComponentBase
{
    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    [Parameter]
    public ScreenplayModel Model { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    public string NewLocationName { get; set; } = "";

    public List<string> DiscoveredLocationNames => Model.GetAllLocations();

    public async Task Close()
    {
        IsOpen = false;
        if (IsOpenChanged.HasDelegate)
        {
            await IsOpenChanged.InvokeAsync(false);
        }
    }

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }

    public async Task AddLocation()
    {
        if (!string.IsNullOrWhiteSpace(NewLocationName))
        {
            string upper = NewLocationName.Trim().ToUpperInvariant();
            Model.GetOrCreateLocationProfile(upper);
            NewLocationName = "";
            await OnChanged();
        }
    }

    public async Task RemoveLocation(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            string upper = name.Trim().ToUpperInvariant();
            Model.LocationProfiles.RemoveAll(l => l.Name.Equals(upper, StringComparison.OrdinalIgnoreCase));
            // Also reset any scene headings using this location to "NEW LOCATION"
            foreach (var scene in Model.Scenes)
            {
                if (scene.Location.Equals(upper, StringComparison.OrdinalIgnoreCase))
                {
                    scene.Location = "NEW LOCATION";
                }
            }
            await OnChanged();
        }
    }
}
