using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_GenErrorAlert
{
    [CascadingParameter] public Scenes Host { get; set; } = default;
    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }


    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }
}
