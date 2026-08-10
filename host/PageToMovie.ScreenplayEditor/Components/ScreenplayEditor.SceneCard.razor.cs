using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_SceneCard : ComponentBase
{
    [Parameter]
    public ScreenplayScene Scene { get; set; } = new();

    [Parameter]
    public bool IsFirst { get; set; }

    [Parameter]
    public bool IsLast { get; set; }

    [Parameter]
    public List<string> AvailableLocations { get; set; } = new();

    [Parameter]
    public List<string> AvailableCharacters { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    /// <summary>Opens the locations modal, preferably focused on this scene's place name.</summary>
    [Parameter]
    public EventCallback<string?> OnEditLocationsClick { get; set; }

    /// <summary>Opens the characters modal, optionally focused on a speaker name.</summary>
    [Parameter]
    public EventCallback<string?> OnEditCharactersClick { get; set; }

    [Parameter]
    public EventCallback OnMoveUpCallback { get; set; }

    [Parameter]
    public EventCallback OnMoveDownCallback { get; set; }

    [Parameter]
    public EventCallback OnDeleteCallback { get; set; }

    public async Task ToggleCollapse()
    {
        Scene.IsCollapsed = !Scene.IsCollapsed;
        await OnChanged();
    }

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }

    public async Task EditLocationsAsync()
    {
        if (OnEditLocationsClick.HasDelegate)
            await OnEditLocationsClick.InvokeAsync(Scene.Location);
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

    public async Task OnInsertBeatSelected(ChangeEventArgs e)
    {
        if (e.Value is string val && !string.IsNullOrWhiteSpace(val))
        {
            if (Enum.TryParse<BeatType>(val, true, out var beatType))
            {
                await AddBeat(beatType);
            }
        }
    }

    public async Task AddBeat(BeatType type)
    {
        var newBeat = new ScreenplayBeat { BeatType = type };
        if (type == BeatType.Action)
        {
            newBeat.ActionText = "Describe what we see…";
        }
        else if (type == BeatType.Sound)
        {
            newBeat.ActionText = "Describe what we hear…";
        }
        else if (type == BeatType.Dialogue)
        {
            newBeat.Speaker = AvailableCharacters.Count > 0 ? AvailableCharacters[0] : "CHARACTER";
            newBeat.SpokenText = "Spoken line...";
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

    public async Task ReorderBeats((int from, int to) args)
    {
        int from = args.from;
        int to = args.to;

        if (from >= 0 && from < Scene.Beats.Count && to >= 0 && to < Scene.Beats.Count && from != to)
        {
            var beat = Scene.Beats[from];
            Scene.Beats.RemoveAt(from);
            Scene.Beats.Insert(to, beat);
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
