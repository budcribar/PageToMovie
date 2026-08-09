using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_SceneHistory
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public string? Message { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public bool Reverting { get; set; }
    [Parameter] public IReadOnlyList<SceneCommitHistoryItem>? History { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<string> OnRevert { get; set; }
}
