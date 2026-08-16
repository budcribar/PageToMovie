using Microsoft.AspNetCore.Components;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class HomeImportPanel : PageSliceComponent
{
    [CascadingParameter] public required Home Host { get; set; }
    [CascadingParameter] public Home.HomeImport? Import { get; set; }
}
