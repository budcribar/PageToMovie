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

public partial class ConfigurationCoverageCard : PageSliceComponent
{
    [CascadingParameter] public required Configuration Host { get; set; }

    [CascadingParameter] public Configuration.ConfigurationCatalog? Catalog { get; set; }
    [CascadingParameter] public Configuration.ConfigurationKeys? Keys { get; set; }
    [CascadingParameter] public Configuration.ConfigurationCoverage? Coverage { get; set; }
    [CascadingParameter] public Configuration.ConfigurationProjectForm? Form { get; set; }
    [CascadingParameter] public Configuration.ConfigurationMediaTheme? Media { get; set; }

    /// <summary>
    /// Add / Replace / Add provider open a local paste panel — do not depend on the Coverage
    /// cascade alone (IsFixed slices can miss it) and do not gate on Host._busy.
    /// </summary>
    private Configuration.ConfigurationKeys KeyActions => Keys ?? Host.Keys;

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        Coverage ??= Host.Coverage;
        Keys ??= Host.Keys;
        Catalog ??= Host.Catalog;
        Form ??= Host.Form;
        Media ??= Host.Media;
    }
}
