using Microsoft.AspNetCore.Components;
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

    /// <summary>Local toggle; starts open so Settings lands on keys.</summary>
    private bool _studioOpen = true;

    /// <summary>
    /// Add / Replace / Add provider open a local paste panel — do not depend on the Coverage
    /// cascade alone (IsFixed slices can miss it) and do not gate on Host._busy.
    /// </summary>
    private Configuration.ConfigurationKeys KeyActions => Keys ?? Host.Keys;
    private Configuration.ConfigurationCoverage Cov => Coverage ?? Host.Coverage;

    /// <summary>
    /// Visibility is C# <c>@if</c>, not a long-lived <c>&lt;details open&gt;</c>.
    /// <paramref name="coverageOpen"/> / <paramref name="editId"/> keep the body shown after
    /// Add key / Add provider / Replace key re-renders (Aug 10 intent).
    /// </summary>
    internal static bool ShouldShowStudioBody(bool localOpen, bool coverageOpen, string? editId) =>
        localOpen || coverageOpen || !string.IsNullOrWhiteSpace(editId);

    private bool IsStudioOpen =>
        ShouldShowStudioBody(
            _studioOpen,
            Coverage?.StudioCoverageOpen == true,
            Coverage?._coverageEditId);

    private void ToggleStudioCoverage()
    {
        // Keep open while the key panel is active.
        if (!string.IsNullOrWhiteSpace(Coverage?._coverageEditId) && IsStudioOpen)
            return;
        var next = !IsStudioOpen;
        _studioOpen = next;
        if (Coverage is not null)
            Coverage.StudioCoverageOpen = next;
    }

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
