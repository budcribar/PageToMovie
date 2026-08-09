using Microsoft.AspNetCore.Components;
using ScreenplayEditorApp.Models;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_BeatEditor
{
    [Parameter]
    public ScreenplayBeat Beat { get; set; } = new();

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

    protected async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }

    protected async Task MoveUp()
    {
        if (OnMoveUpCallback.HasDelegate)
        {
            await OnMoveUpCallback.InvokeAsync();
        }
    }

    protected async Task MoveDown()
    {
        if (OnMoveDownCallback.HasDelegate)
        {
            await OnMoveDownCallback.InvokeAsync();
        }
    }

    protected async Task Delete()
    {
        if (OnDeleteCallback.HasDelegate)
        {
            await OnDeleteCallback.InvokeAsync();
        }
    }
}
