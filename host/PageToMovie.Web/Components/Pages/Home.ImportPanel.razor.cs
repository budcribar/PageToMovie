using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Home_ImportPanel
{
    [CascadingParameter] public Home Host { get; set; } = default!;
}
