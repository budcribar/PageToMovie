using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_SceneCard
{
    [Parameter]
    public ScreenplayScene Scene { get; set; } = new();

    [Parameter]
    public bool IsFirst { get; set; }

    [Parameter]
    public bool IsLast { get; set; }

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    [Parameter]
    public EventCallback OnMoveUpCallback { get; set; }

    [Parameter]
    public EventCallback OnMoveDownCallback { get; set; }

    [Parameter]
    public EventCallback OnDeleteCallback { get; set; }

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }

    public async Task MoveUp()
    {
        if (OnMoveUpCallback.HasDelegate)
        {
            await OnMoveUpCallback.InvokeAsync();
        }
    }

    public async Task MoveDown()
    {
        if (OnMoveDownCallback.HasDelegate)
        {
            await OnMoveDownCallback.InvokeAsync();
        }
    }

    public async Task Delete()
    {
        if (OnDeleteCallback.HasDelegate)
        {
            await OnDeleteCallback.InvokeAsync();
        }
    }

    public async Task AddBeat(BeatType type)
    {
        var newBeat = new ScreenplayBeat { BeatType = type };
        if (type == BeatType.Dialogue)
        {
            newBeat.Speaker = "CHARACTER";
            newBeat.SpokenText = "Dialogue text...";
        }
        else if (type == BeatType.Action)
        {
            newBeat.ActionText = "Visual description...";
        }
        else if (type == BeatType.Transition)
        {
            newBeat.TransitionText = "CUT TO:";
        }
        Scene.Beats.Add(newBeat);
        await OnChanged();
    }

    public async Task MoveBeatUp(int index)
    {
        if (index > 0 && index < Scene.Beats.Count)
        {
            var item = Scene.Beats[index];
            Scene.Beats.RemoveAt(index);
            Scene.Beats.Insert(index - 1, item);
            await OnChanged();
        }
    }

    public async Task MoveBeatDown(int index)
    {
        if (index >= 0 && index < Scene.Beats.Count - 1)
        {
            var item = Scene.Beats[index];
            Scene.Beats.RemoveAt(index);
            Scene.Beats.Insert(index + 1, item);
            await OnChanged();
        }
    }

    public async Task DeleteBeat(int index)
    {
        if (index >= 0 && index < Scene.Beats.Count)
        {
            Scene.Beats.RemoveAt(index);
            await OnChanged();
        }
    }
}
