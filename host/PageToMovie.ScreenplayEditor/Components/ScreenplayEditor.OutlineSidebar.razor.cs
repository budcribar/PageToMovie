using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_OutlineSidebar : ComponentBase
{
    [Parameter]
    public List<ScreenplayScene> Scenes { get; set; } = new();

    [Parameter]
    public List<string> Characters { get; set; } = new();

    [Parameter]
    public string? FocusedCharacter { get; set; }

    [Parameter]
    public List<string> Locations { get; set; } = new();

    [Parameter]
    public string? FocusedLocation { get; set; }

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
    public EventCallback OnSelectCredits { get; set; }

    [Parameter]
    public EventCallback<int> OnSelectScene { get; set; }

    [Parameter]
    public EventCallback OnAddScene { get; set; }

    [Parameter]
    public EventCallback<int> OnInsertSceneAfter { get; set; }

    [Parameter]
    public EventCallback<int> OnDeleteScene { get; set; }

    [Parameter]
    public EventCallback OnSelectionChangedCallback { get; set; }

    [Parameter]
    public EventCallback<(int from, int to)> OnReorderScenes { get; set; }

    /// <summary>Play generated scene video (host navigates to Film / stitches).</summary>
    [Parameter]
    public EventCallback<int> OnPlayScene { get; set; }

    /// <summary>Open character modal; arg is name or null for full list.</summary>
    [Parameter]
    public EventCallback<string?> OnSelectCharacter { get; set; }

    /// <summary>Open location modal; arg is name or null for full list.</summary>
    [Parameter]
    public EventCallback<string?> OnSelectLocation { get; set; }

    public string OutlineTab { get; set; } = "scenes";
    public int DeletingIndex { get; set; } = -1;
    public int PreviewingIndex { get; set; } = -1;
    public int ActiveDragIndex { get; set; } = -1;

    public bool IsAllSelected => Scenes.Count > 0 && Scenes.All(s => s.IsSelected);

    public void SetTab(string tab) =>
        OutlineTab = tab is "cast" or "locations" ? tab : "scenes";

    public void ToggleOutlineTab() =>
        OutlineTab = OutlineTab switch
        {
            "scenes" => "cast",
            "cast" => "locations",
            _ => "scenes",
        };

    public static string Initials(string name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Length <= 2 ? parts[0] : parts[0][..2];
        return $"{parts[0][0]}{parts[^1][0]}";
    }

    public async Task SelectCharacter(string name)
    {
        if (OnSelectCharacter.HasDelegate)
            await OnSelectCharacter.InvokeAsync(name);
    }

    public async Task OpenCastEditor()
    {
        if (OnSelectCharacter.HasDelegate)
            await OnSelectCharacter.InvokeAsync(null);
    }

    public async Task SelectLocation(string name)
    {
        if (OnSelectLocation.HasDelegate)
            await OnSelectLocation.InvokeAsync(name);
    }

    public async Task OpenLocationEditor()
    {
        if (OnSelectLocation.HasDelegate)
            await OnSelectLocation.InvokeAsync(null);
    }

    public async Task ToggleSelectAll(ChangeEventArgs e)
    {
        bool selected = e.Value is bool b && b;
        foreach (var scene in Scenes)
            scene.IsSelected = selected;
        await OnSelectionChanged();
    }

    public async Task OnSelectionChanged()
    {
        if (OnSelectionChangedCallback.HasDelegate)
            await OnSelectionChangedCallback.InvokeAsync();
    }

    public async Task ToggleCompact()
    {
        IsCompact = !IsCompact;
        if (IsCompactChanged.HasDelegate)
            await IsCompactChanged.InvokeAsync(IsCompact);
    }

    public async Task SelectMetadata()
    {
        if (OnSelectMetadata.HasDelegate)
            await OnSelectMetadata.InvokeAsync();
    }

    public async Task SelectCredits()
    {
        if (OnSelectCredits.HasDelegate)
            await OnSelectCredits.InvokeAsync();
    }

    public async Task SelectScene(int index)
    {
        if (index >= 0 && index < Scenes.Count)
        {
            foreach (var s in Scenes) s.IsSelected = false;
            Scenes[index].IsSelected = true;
            if (OnSelectScene.HasDelegate)
                await OnSelectScene.InvokeAsync(index);
        }
    }

    public void RequestDelete(int index) => DeletingIndex = index;

    public void CancelDelete() => DeletingIndex = -1;

    public async Task ConfirmDelete()
    {
        if (DeletingIndex >= 0 && DeletingIndex < Scenes.Count)
        {
            int idx = DeletingIndex;
            DeletingIndex = -1;
            if (OnDeleteScene.HasDelegate)
                await OnDeleteScene.InvokeAsync(idx);
        }
    }

    public void PreviewScene(int index) => PreviewingIndex = index;

    public void ClosePreview() => PreviewingIndex = -1;

    public async Task PlayVideo(int index)
    {
        if (index < 0 || index >= Scenes.Count) return;
        var sn = Scenes[index].SceneNumber;
        if (OnPlayScene.HasDelegate)
            await OnPlayScene.InvokeAsync(sn);
        else
            PreviewScene(index); // standalone: fall back to script preview
    }

    public void HandleDragStart(int index) => ActiveDragIndex = index;

    public async Task InsertAfter(int index)
    {
        if (OnInsertSceneAfter.HasDelegate)
            await OnInsertSceneAfter.InvokeAsync(index);
        else if (OnAddScene.HasDelegate)
            await OnAddScene.InvokeAsync();
    }

    public async Task HandleDrop(int targetIndex)
    {
        if (ActiveDragIndex >= 0 && ActiveDragIndex != targetIndex)
        {
            if (OnReorderScenes.HasDelegate)
                await OnReorderScenes.InvokeAsync((ActiveDragIndex, targetIndex));
        }
        ActiveDragIndex = -1;
    }
}
