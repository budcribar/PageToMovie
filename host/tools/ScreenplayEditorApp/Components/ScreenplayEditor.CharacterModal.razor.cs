using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_CharacterModal : ComponentBase
{
    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    [Parameter]
    public ScreenplayModel Model { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    public string NewCharacterName { get; set; } = "";

    public List<string> DiscoveredCharacterNames => Model.GetAllCharacters();

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

    public async Task AddCharacter()
    {
        if (!string.IsNullOrWhiteSpace(NewCharacterName))
        {
            string upper = NewCharacterName.Trim().ToUpperInvariant();
            Model.GetOrCreateCharacterProfile(upper);
            NewCharacterName = "";
            await OnChanged();
        }
    }

    public async Task RemoveCharacter(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            string upper = name.Trim().ToUpperInvariant();
            Model.CharacterProfiles.RemoveAll(c => c.Name.Equals(upper, StringComparison.OrdinalIgnoreCase));
            await OnChanged();
        }
    }
}
