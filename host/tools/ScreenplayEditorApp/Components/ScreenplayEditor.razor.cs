using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor
{
    [Parameter]
    public ScreenplayModel Model { get; set; } = new();

    [Parameter]
    public EventCallback<ScreenplayModel> ModelChanged { get; set; }

    public bool ShowFountainModal { get; set; }
    public string FountainModalMode { get; set; } = "import";
    public string FountainModalText { get; set; } = "";

    public int TotalBeats => Model.Scenes.Sum(s => s.Beats.Count);

    public async Task OnChanged()
    {
        ReindexSceneNumbers();
        if (ModelChanged.HasDelegate)
        {
            await ModelChanged.InvokeAsync(Model);
        }
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
        var newScene = new ScreenplayScene
        {
            SceneNumber = Model.Scenes.Count + 1,
            Environment = "INT.",
            Location = "NEW LOCATION",
            TimeOfDay = "DAY"
        };
        newScene.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Action,
            ActionText = "Describe visual scene action here..."
        });
        Model.Scenes.Add(newScene);
        await OnChanged();
    }

    public async Task MoveSceneUp(int index)
    {
        if (index > 0 && index < Model.Scenes.Count)
        {
            var item = Model.Scenes[index];
            Model.Scenes.RemoveAt(index);
            Model.Scenes.Insert(index - 1, item);
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
            await OnChanged();
        }
    }

    public async Task DeleteScene(int index)
    {
        if (index >= 0 && index < Model.Scenes.Count)
        {
            Model.Scenes.RemoveAt(index);
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
            await OnChanged();
        }
    }
}
