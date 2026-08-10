using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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

    [Parameter]
    public EventCallback OnGroupsChanged { get; set; }

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
    public string? EditingGroupTitle { get; set; }
    public string GroupRenameDraft { get; set; } = "";

    public bool IsAllSelected => Scenes.Count > 0 && Scenes.All(s => s.IsSelected);

    public sealed class OutlineGroupBlock
    {
        public string GroupTitle { get; init; } = "";
        public List<ScreenplayScene> Scenes { get; init; } = new();
    }

    public IEnumerable<OutlineGroupBlock> BuildOutlineBlocks()
    {
        var blocks = new List<OutlineGroupBlock>();
        OutlineGroupBlock? cur = null;
        foreach (var scene in Scenes)
        {
            var title = string.IsNullOrWhiteSpace(scene.GroupTitle)
                ? (string.IsNullOrWhiteSpace(scene.Location) ? "Sequence" : scene.Location)
                : scene.GroupTitle;
            if (cur is null || !cur.GroupTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
            {
                cur = new OutlineGroupBlock { GroupTitle = title, Scenes = new List<ScreenplayScene>() };
                blocks.Add(cur);
            }
            cur.Scenes.Add(scene);
            // Normalize empty titles so collapse state sticks
            if (string.IsNullOrWhiteSpace(scene.GroupTitle))
                scene.GroupTitle = title;
        }
        return blocks;
    }

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

    public async Task ToggleGroup(string groupTitle)
    {
        var block = BuildOutlineBlocks().FirstOrDefault(b =>
            b.GroupTitle.Equals(groupTitle, StringComparison.OrdinalIgnoreCase));
        if (block is null || block.Scenes.Count == 0) return;
        var next = !block.Scenes[0].IsGroupCollapsed;
        foreach (var s in block.Scenes)
            s.IsGroupCollapsed = next;
        await NotifyGroupsChanged();
    }

    public async Task ToggleGroupSelect(List<ScreenplayScene> groupScenes, ChangeEventArgs e)
    {
        var selected = e.Value is bool b && b;
        foreach (var s in groupScenes)
            s.IsSelected = selected;
        await OnSelectionChanged();
    }

    public async Task SelectGroupScenes(List<ScreenplayScene> groupScenes)
    {
        foreach (var s in Scenes) s.IsSelected = false;
        foreach (var s in groupScenes) s.IsSelected = true;
        if (groupScenes.Count > 0)
        {
            var idx = Scenes.IndexOf(groupScenes[0]);
            if (idx >= 0 && OnSelectScene.HasDelegate)
                await OnSelectScene.InvokeAsync(idx);
        }
        await OnSelectionChanged();
    }

    public void BeginGroupRename(string groupTitle)
    {
        EditingGroupTitle = groupTitle;
        GroupRenameDraft = groupTitle;
    }

    public async Task OnGroupRenameKey(KeyboardEventArgs e, string oldTitle)
    {
        if (e.Key == "Enter")
            await CommitGroupRename(oldTitle);
        else if (e.Key == "Escape")
        {
            EditingGroupTitle = null;
            GroupRenameDraft = "";
        }
    }

    public async Task CommitGroupRename(string oldTitle)
    {
        if (EditingGroupTitle is null) return;
        var draft = (GroupRenameDraft ?? "").Trim();
        EditingGroupTitle = null;
        if (string.IsNullOrWhiteSpace(draft) || draft.Equals(oldTitle, StringComparison.OrdinalIgnoreCase))
            return;
        foreach (var s in Scenes)
        {
            if (s.GroupTitle.Equals(oldTitle, StringComparison.OrdinalIgnoreCase))
                s.GroupTitle = draft;
        }
        GroupRenameDraft = "";
        await NotifyGroupsChanged();
    }

    private async Task NotifyGroupsChanged()
    {
        if (OnGroupsChanged.HasDelegate)
            await OnGroupsChanged.InvokeAsync();
        StateHasChanged();
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
