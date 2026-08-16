using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin_GenErrorsSection : PageSliceComponent
{
    [CascadingParameter] public required Admin Host { get; set; }
    [CascadingParameter] public Admin.AdminTelemetry? Telemetry { get; set; }
    [CascadingParameter] public Admin.AdminArchive? Archive { get; set; }
    [CascadingParameter] public Admin.AdminUi? Ui { get; set; }
}
