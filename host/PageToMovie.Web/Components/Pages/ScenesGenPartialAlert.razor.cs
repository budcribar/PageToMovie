using Microsoft.AspNetCore.Components;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ScenesGenPartialAlert : PageSliceComponent
{
    [CascadingParameter] public required Scenes Host { get; set; }
    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }


    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }
}
