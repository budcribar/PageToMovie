using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor : ComponentBase
{
    [Parameter]
    public ScreenplayModel Model { get; set; } = new();

    [Parameter]
    public EventCallback<ScreenplayModel> ModelChanged { get; set; }

    public bool ShowFountainModal { get; set; }
    public string FountainModalMode { get; set; } = "import";
    public string FountainModalText { get; set; } = "";

    public bool ShowLocationModal { get; set; } = false;
    public bool ShowCharacterModal { get; set; } = false;

    public string ActiveViewMode { get; set; } = "scene";
    public int SelectedSceneIndex { get; set; } = 0;
    public bool IsSidebarCompact { get; set; } = false;

    public int TotalBeats => Model.Scenes.Sum(s => s.Beats.Count);

    protected override void OnInitialized()
    {
        EnsureSceneSelection();
    }

    public async Task OnChanged()
    {
        ReindexSceneNumbers();
        EnsureSceneSelection();
        if (ModelChanged.HasDelegate)
        {
            await ModelChanged.InvokeAsync(Model);
        }
    }

    public void EnsureSceneSelection()
    {
        if (Model.Scenes.Count > 0 && !Model.Scenes.Any(s => s.IsSelected))
        {
            Model.Scenes[0].IsSelected = true;
        }
    }

    public void OnSelectionChanged()
    {
        ActiveViewMode = "scene";
        StateHasChanged();
    }

    public void OpenLocationModal()
    {
        ShowLocationModal = true;
    }

    public void OpenCharacterModal()
    {
        ShowCharacterModal = true;
    }

    public void SelectMetadataView()
    {
        ActiveViewMode = "metadata";
    }

    public void SelectCreditsView()
    {
        ActiveViewMode = "credits";
    }

    public void SelectSceneView(int index)
    {
        if (index >= 0 && index < Model.Scenes.Count)
        {
            SelectedSceneIndex = index;
            ActiveViewMode = "scene";
        }
    }

    public void SelectPreviousScene()
    {
        if (SelectedSceneIndex > 0)
        {
            SelectedSceneIndex--;
            ActiveViewMode = "scene";
        }
    }

    public void SelectNextScene()
    {
        if (SelectedSceneIndex < Model.Scenes.Count - 1)
        {
            SelectedSceneIndex++;
            ActiveViewMode = "scene";
        }
    }

    public void ShowAllScenesView()
    {
        ActiveViewMode = ActiveViewMode == "all" ? "scene" : "all";
    }

    public async Task ReorderScenes((int from, int to) args)
    {
        int from = args.from;
        int to = args.to;

        if (from >= 0 && from < Model.Scenes.Count && to >= 0 && to < Model.Scenes.Count && from != to)
        {
            var scene = Model.Scenes[from];
            Model.Scenes.RemoveAt(from);
            Model.Scenes.Insert(to, scene);
            SelectedSceneIndex = to;
            await OnChanged();
        }
    }

    public async Task CollapseAllScenes()
    {
        foreach (var s in Model.Scenes)
        {
            s.IsCollapsed = true;
        }
        await OnChanged();
    }

    public async Task ExpandAllScenes()
    {
        foreach (var s in Model.Scenes)
        {
            s.IsCollapsed = false;
        }
        await OnChanged();
    }

    public void ReindexSceneNumbers()
    {
        for (var i = 0; i < Model.Scenes.Count; i++)
        {
            Model.Scenes[i].SceneNumber = i + 1;
        }
    }

    public async Task AddScene()
    {
        var locs = Model.GetAllLocations();
        var newScene = new ScreenplayScene
        {
            SceneNumber = Model.Scenes.Count + 1,
            Environment = "INT.",
            Location = "NEW LOCATION",
            TimeOfDay = "DAY",
            IsSelected = true
        };
        newScene.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Action,
            ActionText = "Describe visual scene action here..."
        });
        Model.Scenes.Add(newScene);
        SelectedSceneIndex = Model.Scenes.Count - 1;
        ActiveViewMode = "scene";
        await OnChanged();
    }

    public async Task MoveSceneUp(int index)
    {
        if (index > 0 && index < Model.Scenes.Count)
        {
            var item = Model.Scenes[index];
            Model.Scenes.RemoveAt(index);
            Model.Scenes.Insert(index - 1, item);
            SelectedSceneIndex = index - 1;
            await OnChanged();
        }
    }

    public async Task MoveSceneDown(int index)
    {
        if (index >= 0 && index < Model.Scenes.Count - 1)
        {
            var item = Model.Scenes[index];
            Model.Scenes.RemoveAt(index);
            Model.Scenes.Insert(index + 1, item);
            SelectedSceneIndex = index + 1;
            await OnChanged();
        }
    }

    public async Task DeleteScene(int index)
    {
        if (index >= 0 && index < Model.Scenes.Count)
        {
            Model.Scenes.RemoveAt(index);
            if (Model.Scenes.Count == 0)
            {
                ActiveViewMode = "metadata";
                SelectedSceneIndex = 0;
            }
            else if (SelectedSceneIndex >= Model.Scenes.Count)
            {
                SelectedSceneIndex = Model.Scenes.Count - 1;
            }
            await OnChanged();
        }
    }

    public void OpenImportModal()
    {
        FountainModalMode = "import";
        FountainModalText = "";
        ShowFountainModal = true;
    }

    public void OpenExportModal()
    {
        FountainModalMode = "export";
        FountainModalText = FountainFormatter.ToFountain(Model);
        ShowFountainModal = true;
    }

    public async Task HandleFountainImport(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Model = FountainFormatter.Parse(text);
            ReindexSceneNumbers();
            if (Model.Scenes.Count > 0)
            {
                SelectedSceneIndex = 0;
                ActiveViewMode = "scene";
                EnsureSceneSelection();
            }
            else
            {
                ActiveViewMode = "metadata";
            }
            await OnChanged();
        }
    }
}
