using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationShots
{

    public override string StepKey => "shots";

    /// <summary>Landed from Estimate DecisionCard Generate path (?from=decision).</summary>
    private bool FromDecisionCard =>
        string.Equals(StudioDeepLinks.QueryValue(Nav, "from"), "decision", StringComparison.OrdinalIgnoreCase);
}
