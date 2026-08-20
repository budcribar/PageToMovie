using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class StudioPageHeader
{
    [Parameter] public string Kicker { get; set; } = "";
    [Parameter] public string Title { get; set; } = "";
    [Parameter] public string CssClass { get; set; } = "mb-3";
    [Parameter] public string LedeClass { get; set; } = "mb-0";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    /// <summary>Optional top-right actions (same slot as Screenplay's green next control).</summary>
    [Parameter] public RenderFragment? Actions { get; set; }
}
