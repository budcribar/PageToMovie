using Microsoft.AspNetCore.Components;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_CharacterModal : ComponentBase
{
    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    [Parameter]
    public List<string> Characters { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    public string NewCharacterName { get; set; } = "";

    public async Task Close()
    {
        IsOpen = false;
        if (IsOpenChanged.HasDelegate)
        {
            await IsOpenChanged.InvokeAsync(false);
        }
    }

    public async Task AddCharacter()
    {
        if (!string.IsNullOrWhiteSpace(NewCharacterName))
        {
            string upper = NewCharacterName.Trim().ToUpperInvariant();
            if (!Characters.Contains(upper))
            {
                Characters.Add(upper);
                NewCharacterName = "";
                if (OnChangedCallback.HasDelegate)
                {
                    await OnChangedCallback.InvokeAsync();
                }
            }
        }
    }

    public async Task RemoveCharacter(int index)
    {
        if (index >= 0 && index < Characters.Count)
        {
            Characters.RemoveAt(index);
            if (OnChangedCallback.HasDelegate)
            {
                await OnChangedCallback.InvokeAsync();
            }
        }
    }
}
