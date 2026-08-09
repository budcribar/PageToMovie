using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class SceneCard
{
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public string? Title { get; set; }
    [Parameter] public bool Selected { get; set; }
    [Parameter] public EventCallback<bool> SelectedChanged { get; set; }
    [Parameter] public bool ShowCheckbox { get; set; } = true;
    [Parameter] public string? StatusText { get; set; }
    [Parameter] public string CssClass { get; set; } = "card mb-2";
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? Actions { get; set; }

    async Task OnCheckboxChanged(ChangeEventArgs e)
    {
        var v = e.Value is bool b && b
                || string.Equals(e.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        await SelectedChanged.InvokeAsync(v);
    }
}
