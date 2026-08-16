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
using static PageToMovie.Web.Components.CostFormatting;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ProjectCosts_AdvancedSection : PageSliceComponent
{
    [CascadingParameter] public required ProjectCosts Host { get; set; }
}
