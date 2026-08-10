using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor : ComponentBase
{
    [Parameter]
    public ScreenplayModel Model { get; set; } = new();

    [Parameter]
    public EventCallback<ScreenplayModel> ModelChanged { get; set; }

    /// <summary>Host owns the toolbar — hide the built-in top chrome.</summary>
    [Parameter]
    public bool HideChrome { get; set; }

    private bool _menuOpen;
    private void ToggleMenu() => _menuOpen = !_menuOpen;
    private void CloseMenu() => _menuOpen = false;


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

    public void OpenLocationModal(string? focusName = null)
    {
        CloseMenu();
        FocusLocationName = focusName;
        ShowLocationModal = true;
    }

    public void OpenCharacterModal(string? focusName = null)
    {
        CloseMenu();
        FocusCharacterName = focusName;
        ShowCharacterModal = true;
    }

    public void OpenCharacterFromOutline(string? name) => OpenCharacterModal(name);

    public void OpenLocationFromOutline(string? name) => OpenLocationModal(name);

    /// <summary>Location name to highlight when the locations modal opens.</summary>
    public string? FocusLocationName { get; set; }

    /// <summary>Character name to highlight when the characters modal opens.</summary>
    public string? FocusCharacterName { get; set; }

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
        CloseMenu();
        foreach (var s in Model.Scenes)
        {
            s.IsCollapsed = true;
        }
        await OnChanged();
    }

    public async Task ExpandAllScenes()
    {
        CloseMenu();
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
        await InsertSceneAfter(Model.Scenes.Count - 1);
    }

    /// <summary>Insert a blank scene after <paramref name="afterIndex"/> (-1 = at start).</summary>
    public async Task InsertSceneAfter(int afterIndex)
    {
        var prev = afterIndex >= 0 && afterIndex < Model.Scenes.Count ? Model.Scenes[afterIndex] : null;
        var newScene = new ScreenplayScene
        {
            SceneNumber = 0,
            Environment = prev?.Environment ?? "INT.",
            Location = prev?.Location ?? "NEW LOCATION",
            TimeOfDay = prev?.TimeOfDay ?? "DAY",
            IsSelected = true
        };
        newScene.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Action,
            ActionText = "Describe what we see…"
        });

        foreach (var s in Model.Scenes)
            s.IsSelected = false;

        var insertAt = Math.Clamp(afterIndex + 1, 0, Model.Scenes.Count);
        Model.Scenes.Insert(insertAt, newScene);
        ReindexSceneNumbers();
        SelectedSceneIndex = insertAt;
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
        CloseMenu();
        FountainModalMode = "import";
        FountainModalText = "";
        ShowFountainModal = true;
    }

    public void OpenExportModal()
    {
        CloseMenu();
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
