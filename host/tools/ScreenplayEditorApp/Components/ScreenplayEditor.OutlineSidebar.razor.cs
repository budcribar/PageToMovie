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
    public EventCallback OnSelectMetadata { get; set; }

    [Parameter]
    public EventCallback<int> OnSelectScene { get; set; }

    [Parameter]
    public EventCallback OnAddScene { get; set; }

    [Parameter]
    public EventCallback<int> OnDeleteScene { get; set; }

    [Parameter]
    public EventCallback OnSelectionChangedCallback { get; set; }

    [Parameter]
    public EventCallback<(int from, int to)> OnReorderScenes { get; set; }

    public int DeletingIndex { get; set; } = -1;
    public int PreviewingIndex { get; set; } = -1;

    public int ActiveDragIndex { get; set; } = -1;

    public bool IsAllSelected => Scenes.Count > 0 && Scenes.All(s => s.IsSelected);

    public async Task ToggleSelectAll(ChangeEventArgs e)
    {
        bool selected = e.Value is bool b && b;
        foreach (var scene in Scenes)
        {
            scene.IsSelected = selected;
        }
        await OnSelectionChanged();
    }

    public async Task OnSelectionChanged()
    {
        if (OnSelectionChangedCallback.HasDelegate)
        {
            await OnSelectionChangedCallback.InvokeAsync();
        }
    }

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
            await OnSelectMetadata.InvokeAsync();
        }
    }

    public async Task SelectScene(int index)
    {
        if (index >= 0 && index < Scenes.Count)
        {
            // Uncheck all other scenes and check ONLY this scene on single click
            foreach (var s in Scenes) s.IsSelected = false;
            Scenes[index].IsSelected = true;

            if (OnSelectScene.HasDelegate)
            {
                await OnSelectScene.InvokeAsync(index);
            }
        }
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
        if (DeletingIndex >= 0 && DeletingIndex < Scenes.Count)
        {
            int idx = DeletingIndex;
            DeletingIndex = -1;
            if (OnDeleteScene.HasDelegate)
            {
                await OnDeleteScene.InvokeAsync(idx);
            }
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

    public void HandleDragStart(int index)
    {
        ActiveDragIndex = index;
    }

    public async Task HandleDrop(int targetIndex)
    {
        if (ActiveDragIndex >= 0 && ActiveDragIndex != targetIndex)
        {
            if (OnReorderScenes.HasDelegate)
            {
                await OnReorderScenes.InvokeAsync((ActiveDragIndex, targetIndex));
            }
        }
        ActiveDragIndex = -1;
    }
}
