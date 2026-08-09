using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_BeatEditor : ComponentBase
{
    [Parameter]
    public ScreenplayBeat Beat { get; set; } = new();

    [Parameter]
    public int Index { get; set; }

    [Parameter]
    public bool IsFirst { get; set; }

    [Parameter]
    public bool IsLast { get; set; }

    [Parameter]
    public List<string> AvailableCharacters { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    [Parameter]
    public EventCallback OnEditCharactersClick { get; set; }

    [Parameter]
    public EventCallback OnMoveUpCallback { get; set; }

    [Parameter]
    public EventCallback OnMoveDownCallback { get; set; }

    [Parameter]
    public EventCallback OnDeleteCallback { get; set; }

    [Parameter]
    public EventCallback<(int from, int to)> OnReorderBeats { get; set; }

    public static int ActiveDragIndex { get; set; } = -1;

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }

    public async Task MoveUp()
    {
        if (OnMoveUpCallback.HasDelegate)
        {
            await OnMoveUpCallback.InvokeAsync();
        }
    }

    public async Task MoveDown()
    {
        if (OnMoveDownCallback.HasDelegate)
        {
            await OnMoveDownCallback.InvokeAsync();
        }
    }

    public async Task Delete()
    {
        if (OnDeleteCallback.HasDelegate)
        {
            await OnDeleteCallback.InvokeAsync();
        }
    }

    public void HandleDragStart()
    {
        ActiveDragIndex = Index;
    }

    public async Task HandleDrop()
    {
        if (ActiveDragIndex >= 0 && ActiveDragIndex != Index)
        {
            if (OnReorderBeats.HasDelegate)
            {
                await OnReorderBeats.InvokeAsync((ActiveDragIndex, Index));
            }
        }
        ActiveDragIndex = -1;
    }
}
