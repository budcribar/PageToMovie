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

public partial class AdminLoadSimSection : PageSliceComponent
{
    [CascadingParameter] public Admin Host { get; set; } = default;
    [CascadingParameter] public Admin.AdminTelemetry? Telemetry { get; set; }
    [CascadingParameter] public Admin.AdminState? State { get; set; }
    [CascadingParameter] public Admin.AdminUi? Ui { get; set; }
}
