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

namespace PageToMovie.Web.Components.Pages;

public partial class Configuration_CoverageCard
{
    [CascadingParameter] public Configuration Host { get; set; } = default!;
    [CascadingParameter] public Configuration.ConfigurationCatalog? Catalog { get; set; }
    [CascadingParameter] public Configuration.ConfigurationKeys? Keys { get; set; }
    [CascadingParameter] public Configuration.ConfigurationCoverage? Coverage { get; set; }
    [CascadingParameter] public Configuration.ConfigurationProjectForm? Form { get; set; }
    [CascadingParameter] public Configuration.ConfigurationMediaTheme? Media { get; set; }
}
