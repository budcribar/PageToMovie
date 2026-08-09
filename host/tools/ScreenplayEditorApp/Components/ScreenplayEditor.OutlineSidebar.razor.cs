using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_OutlineSidebar : ComponentBase
{
    [Parameter]
    public List<ScreenplayScene> Scenes { get; set; } = new();

    [Parameter]
    public string ActiveView { get; set; } = "metadata";

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

    [Parameter]
    public EventCallback<int> OnDeleteScene { get; set; }

    [Parameter]
    public EventCallback<(int from, int to)> OnReorderScenes { get; set; }

    public int DraggedIndex { get; set; } = -1;
    public int DeletingIndex { get; set; } = -1;
    public int PreviewingIndex { get; set; } = -1;

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

    public void HandleDragStart(int index)
    {
        DraggedIndex = index;
    }

    public async Task HandleDrop(int targetIndex)
    {
        if (DraggedIndex >= 0 && DraggedIndex < Scenes.Count && targetIndex >= 0 && targetIndex < Scenes.Count && DraggedIndex != targetIndex)
        {
            if (OnReorderScenes.HasDelegate)
            {
                await OnReorderScenes.InvokeAsync((DraggedIndex, targetIndex));
            }
        }
        DraggedIndex = -1;
    }

    public void RequestDelete(int index)
    {
        DeletingIndex = index;
    }

    public void CancelDelete()
    {
        DeletingIndex = -1;
    }

    public async Task ConfirmDelete()
    {
        int indexToDelete = DeletingIndex;
        DeletingIndex = -1;
        if (indexToDelete >= 0 && OnDeleteScene.HasDelegate)
        {
            await OnDeleteScene.InvokeAsync(indexToDelete);
        }
    }

    public void PreviewScene(int index)
    {
        PreviewingIndex = index;
    }

    public void ClosePreview()
    {
        PreviewingIndex = -1;
    }
}
