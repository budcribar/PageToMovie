using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_VideoEditPrompt
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public string Prompt { get; set; } = "";
    [Parameter] public EventCallback<string> PromptChanged { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool JobRunning { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnSubmit { get; set; }

    private Task OnPromptInput(ChangeEventArgs e) =>
        PromptChanged.InvokeAsync(e.Value?.ToString() ?? "");
}
