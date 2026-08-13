using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport_SetupBanner
{
    [CascadingParameter] public required AdaptationImport Host { get; set; }
}
