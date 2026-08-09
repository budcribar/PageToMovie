using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_MusicCompare
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public string? Message { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public IReadOnlyList<MusicVersionItem>? Versions { get; set; }
    [Parameter] public IReadOnlyDictionary<string, List<string>>? Urls { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<string> OnPromote { get; set; }
    [Parameter] public EventCallback<string> OnDelete { get; set; }
}
