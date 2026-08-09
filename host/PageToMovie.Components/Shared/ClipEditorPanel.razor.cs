using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class ClipEditorPanel
{
    [Parameter] public string Title { get; set; } = "Edit clip";
    [Parameter] public string Prompt { get; set; } = "";
    [Parameter] public EventCallback<string> PromptChanged { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? Fields { get; set; }
    [Parameter] public RenderFragment? Actions { get; set; }

    Task OnPromptInput(ChangeEventArgs e) =>
        PromptChanged.InvokeAsync(e.Value?.ToString() ?? "");
}
