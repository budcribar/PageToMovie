using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor
{
    [Parameter]
    public ScreenplayModel Model { get; set; } = new();

    [Parameter]
    public EventCallback<ScreenplayModel> ModelChanged { get; set; }

    protected bool ShowFountainModal { get; set; }
    protected string FountainModalMode { get; set; } = "import";
    protected string FountainModalText { get; set; } = "";

    protected int TotalBeats => Model.Scenes.Sum(s => s.Beats.Count);

    protected async Task OnChanged()
    {
        ReindexSceneNumbers();
        if (ModelChanged.HasDelegate)
        {
            await ModelChanged.InvokeAsync(Model);
        }
    }

    protected void ReindexSceneNumbers()
    {
        for (var i = 0; i < Model.Scenes.Count; i++)
        {
            Model.Scenes[i].SceneNumber = i + 1;
        }
    }

    protected async Task AddScene()
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

    protected async Task MoveSceneUp(int index)
    {
        if (index > 0 && index < Model.Scenes.Count)
        {
            var item = Model.Scenes[index];
            Model.Scenes.RemoveAt(index);
            Model.Scenes.Insert(index - 1, item);
            await OnChanged();
        }
    }

    protected async Task MoveSceneDown(int index)
    {
        if (index >= 0 && index < Model.Scenes.Count - 1)
        {
            var item = Model.Scenes[index];
            Model.Scenes.RemoveAt(index);
            Model.Scenes.Insert(index + 1, item);
            await OnChanged();
        }
    }

    protected async Task DeleteScene(int index)
    {
        if (index >= 0 && index < Model.Scenes.Count)
        {
            Model.Scenes.RemoveAt(index);
            await OnChanged();
        }
    }

    protected void OpenImportModal()
    {
        FountainModalMode = "import";
        FountainModalText = "";
        ShowFountainModal = true;
    }

    protected void OpenExportModal()
    {
        FountainModalMode = "export";
        FountainModalText = FountainFormatter.ToFountain(Model);
        ShowFountainModal = true;
    }

    protected async Task HandleFountainImport(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Model = FountainFormatter.Parse(text);
            await OnChanged();
        }
    }
}
