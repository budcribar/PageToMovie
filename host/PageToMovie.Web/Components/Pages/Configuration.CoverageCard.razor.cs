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

public partial class Configuration_CoverageCard : PageSliceComponent
{
    [CascadingParameter] public required Configuration Host { get; set; }

    [CascadingParameter] public Configuration.ConfigurationCatalog? Catalog { get; set; }
    [CascadingParameter] public Configuration.ConfigurationKeys? Keys { get; set; }
    [CascadingParameter] public Configuration.ConfigurationCoverage? Coverage { get; set; }
    [CascadingParameter] public Configuration.ConfigurationProjectForm? Form { get; set; }
    [CascadingParameter] public Configuration.ConfigurationMediaTheme? Media { get; set; }

    /// <summary>
    /// Controlled <details open> — Blazor re-renders after "Add key" would otherwise collapse
    /// an uncontrolled details element and hide the paste panel.
    /// </summary>
    private bool IsStudioOpen =>
        Coverage is not null
        && (Coverage.StudioCoverageOpen || !string.IsNullOrWhiteSpace(Coverage._coverageEditId));

    private void ToggleStudioCoverage()
    {
        if (Coverage is null) return;
        // Keep open while the key panel is active.
        if (!string.IsNullOrWhiteSpace(Coverage._coverageEditId) && Coverage.StudioCoverageOpen)
            return;
        Coverage.StudioCoverageOpen = !Coverage.StudioCoverageOpen;
    }
}
