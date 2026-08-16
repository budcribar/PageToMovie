using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Services;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport_Importing : PageSliceComponent
{
    [CascadingParameter] public required AdaptationImport Host { get; set; }
    [Inject] private AdminSessionService Session { get; set; } = default!;

    private string ImportJobStatus
    {
        get
        {
            if (Host.Jobs.JobRunning)
                return Host.Jobs.Job?.Status ?? "running";
            return Host.Drop._importing ? "running" : "idle";
        }
    }
}
