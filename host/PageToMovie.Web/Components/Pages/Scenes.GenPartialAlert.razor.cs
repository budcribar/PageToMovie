using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_GenPartialAlert
{
    [CascadingParameter] public required Scenes Host { get; set; }
    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }


    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }
}
