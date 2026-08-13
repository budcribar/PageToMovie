using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport_Importing
{
    [CascadingParameter] public required AdaptationImport Host { get; set; }
    [Inject] private AdminSessionService Session { get; set; } = default!;
}
