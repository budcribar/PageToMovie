using Microsoft.AspNetCore.Components;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImportSourceForm : PageSliceComponent
{
    [CascadingParameter] public required AdaptationImport Host { get; set; }
}
