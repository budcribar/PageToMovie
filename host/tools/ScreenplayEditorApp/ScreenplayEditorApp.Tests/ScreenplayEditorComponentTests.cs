#pragma warning disable BL0005

using System.Threading.Tasks;
using ScreenplayEditorApp.Components;
using ScreenplayEditorApp.Models;
using Xunit;

namespace ScreenplayEditorApp.Tests;

public class ScreenplayEditorComponentTests
{
    [Fact]
    public async Task TestScreenplayEditorComponentMethods()
    {
        var editor = new ScreenplayEditor();
        var model = new ScreenplayModel();
        editor.Model = model;

        bool modelChangedFired = false;
        editor.ModelChanged = new Microsoft.AspNetCore.Components.EventCallback<ScreenplayModel>(null, (ScreenplayModel m) => { modelChangedFired = true; });

        // Total Beats
        Assert.Equal(0, editor.TotalBeats);

        // Test AddScene
        await editor.AddScene();
        Assert.Single(model.Scenes);
        Assert.Equal("INT.", model.Scenes[0].Environment);
        Assert.Equal("NEW LOCATION", model.Scenes[0].Location);
        Assert.True(modelChangedFired);

        // Add second scene
        await editor.AddScene();
        Assert.Equal(2, model.Scenes.Count);
        Assert.Equal(1, model.Scenes[0].SceneNumber);
        Assert.Equal(2, model.Scenes[1].SceneNumber);

        // Move scene out-of-bounds
        await editor.MoveSceneUp(-1);
        await editor.MoveSceneUp(0);
        await editor.MoveSceneDown(99);

        // Move scene down & up
        await editor.MoveSceneDown(0);
        Assert.Equal(1, model.Scenes[0].SceneNumber);

        await editor.MoveSceneUp(1);
        Assert.Equal(1, model.Scenes[0].SceneNumber);

        // Open Import Modal
        editor.OpenImportModal();
        Assert.Equal("import", editor.FountainModalMode);
        Assert.True(editor.ShowFountainModal);

        // Open Export Modal
        editor.OpenExportModal();
        Assert.Equal("export", editor.FountainModalMode);

        // Open Export PDF Modal
        editor.OpenExportPdfModal();
        Assert.Equal("export-pdf", editor.FountainModalMode);
        Assert.True(editor.ShowFountainModal);
        Assert.NotNull(editor.FountainModalPdfBytes);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(editor.FountainModalPdfBytes!, 0, 5));

        // Handle Fountain Import (empty & valid)
        await editor.HandleFountainImport("");
        string sampleFountain = "Title: New Title\n\nINT. HALLWAY - NIGHT\n\nHero enters.";
        await editor.HandleFountainImport(sampleFountain);
        Assert.Equal("New Title", editor.Model.Metadata.Title);
        Assert.Single(editor.Model.Scenes);

        // Delete scene out-of-bounds & valid
        await editor.DeleteScene(-1);
        await editor.DeleteScene(99);
        await editor.DeleteScene(0);
        Assert.Empty(editor.Model.Scenes);
    }

    [Fact]
    public async Task TestMetadataHeaderComponentMethods()
    {
        var header = new ScreenplayEditor_MetadataHeader();
        var meta = new ScreenplayMetadata { Title = "Test Title" };
        header.Metadata = meta;

        bool eventFired = false;
        header.OnChangedCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { eventFired = true; });

        header.ToggleCollapse();
        Assert.True(header.IsCollapsed);
        header.ToggleCollapse();
        Assert.False(header.IsCollapsed);

        await header.OnChanged();
        Assert.True(eventFired);
    }

    [Fact]
    public async Task TestSceneCardComponentMethods()
    {
        var card = new ScreenplayEditor_SceneCard();
        var scene = new ScreenplayScene { Environment = "INT.", Location = "ROOM", TimeOfDay = "DAY" };
        card.Scene = scene;

        bool changedFired = false;
        bool moveUpFired = false;
        bool moveDownFired = false;
        bool deleteFired = false;

        card.OnChangedCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { changedFired = true; });
        card.OnMoveUpCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { moveUpFired = true; });
        card.OnMoveDownCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { moveDownFired = true; });
        card.OnDeleteCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { deleteFired = true; });

        // Add Action Beat
        await card.AddBeat(BeatType.Action);
        Assert.Single(scene.Beats);
        Assert.Equal(BeatType.Action, scene.Beats[0].BeatType);

        // Add Dialogue Beat
        await card.AddBeat(BeatType.Dialogue);
        Assert.Equal(2, scene.Beats.Count);
        Assert.Equal(BeatType.Dialogue, scene.Beats[1].BeatType);

        // Add Transition Beat
        await card.AddBeat(BeatType.Transition);
        Assert.Equal(3, scene.Beats.Count);

        // Move Beat Out-of-bounds
        await card.MoveBeatUp(0);
        await card.MoveBeatUp(-1);
        await card.MoveBeatDown(2);
        await card.MoveBeatDown(99);
        await card.DeleteBeat(-1);
        await card.DeleteBeat(99);

        // Move Beat Down & Up
        await card.MoveBeatDown(0);
        await card.MoveBeatUp(1);

        // Delete Beat
        await card.DeleteBeat(0);
        Assert.Equal(2, scene.Beats.Count);

        // Fire callbacks
        await card.MoveUp();
        await card.MoveDown();
        await card.Delete();

        Assert.True(changedFired);
        Assert.True(moveUpFired);
        Assert.True(moveDownFired);
        Assert.True(deleteFired);
    }

    [Fact]
    public async Task TestBeatEditorComponentMethods()
    {
        var beatEditor = new ScreenplayEditor_BeatEditor();
        var beat = new ScreenplayBeat { BeatType = BeatType.Action, ActionText = "Running..." };
        beatEditor.Beat = beat;

        bool changedFired = false;
        bool moveUpFired = false;
        bool moveDownFired = false;
        bool deleteFired = false;

        beatEditor.OnChangedCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { changedFired = true; });
        beatEditor.OnMoveUpCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { moveUpFired = true; });
        beatEditor.OnMoveDownCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { moveDownFired = true; });
        beatEditor.OnDeleteCallback = new Microsoft.AspNetCore.Components.EventCallback(null, () => { deleteFired = true; });

        await beatEditor.OnChanged();
        await beatEditor.MoveUp();
        await beatEditor.MoveDown();
        await beatEditor.Delete();

        Assert.True(changedFired);
        Assert.True(moveUpFired);
        Assert.True(moveDownFired);
        Assert.True(deleteFired);
    }

    [Fact]
    public async Task TestFountainModalComponentMethods()
    {
        var modal = new ScreenplayEditor_FountainModal();
        modal.IsOpen = true;
        modal.Mode = "import";
        modal.FountainText = "Title: Sample";

        bool importedFired = false;
        modal.OnImportCallback = new Microsoft.AspNetCore.Components.EventCallback<string>(null, (string txt) => { importedFired = true; });

        await modal.Import();
        Assert.True(importedFired);
        Assert.False(modal.IsOpen);

        modal.IsOpen = true;
        await modal.Close();
        Assert.False(modal.IsOpen);

        await modal.CopyText();
    }
}
