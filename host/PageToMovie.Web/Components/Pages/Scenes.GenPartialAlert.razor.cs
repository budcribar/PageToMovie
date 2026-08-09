using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_GenPartialAlert
{
    [CascadingParameter] public Scenes Host { get; set; } = default!;
}
