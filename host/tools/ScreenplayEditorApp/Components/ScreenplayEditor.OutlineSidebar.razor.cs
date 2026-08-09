using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_OutlineSidebar : ComponentBase
{
    [Parameter]
    public List<ScreenplayScene> Scenes { get; set; } = new();

    [Parameter]
    public string ActiveView { get; set; } = "metadata"; // metadata | scene | all

    [Parameter]
    public int SelectedSceneIndex { get; set; } = 0;

    [Parameter]
    public bool IsCompact { get; set; } = false;

    [Parameter]
    public EventCallback<bool> IsCompactChanged { get; set; }

    [Parameter]
    public EventCallback<string> OnSelectMetadata { get; set; }

    [Parameter]
    public EventCallback<int> OnSelectScene { get; set; }

    [Parameter]
    public EventCallback OnAddScene { get; set; }

    public async Task ToggleCompact()
    {
        IsCompact = !IsCompact;
        if (IsCompactChanged.HasDelegate)
        {
            await IsCompactChanged.InvokeAsync(IsCompact);
        }
    }

    public async Task SelectMetadata()
    {
        if (OnSelectMetadata.HasDelegate)
        {
            await OnSelectMetadata.InvokeAsync("metadata");
        }
    }

    public async Task SelectScene(int index)
    {
        if (OnSelectScene.HasDelegate)
        {
            await OnSelectScene.InvokeAsync(index);
        }
    }
}
