using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Home_ImportPanel
{
    [CascadingParameter] public required Home Host { get; set; }
    [CascadingParameter] public Home.HomeImport? Import { get; set; }
}
