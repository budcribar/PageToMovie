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

namespace PageToMovie.Web.Components;

public partial class StudioProcessStrip
{
    /// <summary>setup | book | cast | estimate | film | review</summary>
    [Parameter] public string Active { get; set; } = "book";

    /// <summary>
    /// Bait path: library book + narrator voice only.
    /// Ignored when <see cref="FullStudio"/> is true.
    /// </summary>
    [Parameter] public bool SimpleMode { get; set; }

    /// <summary>
    /// Force the full Book → Estimate → Film strip (Cast optional) even when the
    /// project’s stored path is simple-voice (used on Full studio home and cost).
    /// </summary>
    [Parameter] public bool FullStudio { get; set; }

    /// <summary>Step 0 only while personal studio keys are missing (BYOK).</summary>
    private bool NeedsSetup =>
        ActiveProject.Status is { XaiConfigured: false };

    private bool BookLocked => NeedsSetup && Active != "book";

    private bool UseSimple => !FullStudio && (SimpleMode || ActiveProject.IsSimpleVoice);

    /// <summary>Advanced/BYOK: a usable AI planning model is selected and a key is configured.</summary>
    private bool ModelReady =>
        ActiveProject.Status is { XaiConfigured: true } st && IsUsableModel(st.PlanningModel);

    private static bool IsUsableModel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var s = id.Trim();
        return !(s.Equals("none", StringComparison.OrdinalIgnoreCase)
                 || s.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                 || s.Equals("auto", StringComparison.OrdinalIgnoreCase));
    }

    private void GoToModelSettings() => Nav.NavigateTo("configuration?focus=planning");

    private bool FilmReady => ActiveProject.CanScenes || Active == "film";
    private bool FilmViaEstimate => !FilmReady && ActiveProject.CanEstimate;
    private string FilmHref =>
        FilmReady ? "scenes" : FilmViaEstimate ? "cost" : "javascript:void(0)";
    private string FilmTitle =>
        FilmReady
            ? "Watch and generate clips"
            : FilmViaEstimate
                ? "Generate from Estimate first"
                : ActiveProject.ScenesBlockedReason;
    private bool FilmDisabled => !FilmReady && !FilmViaEstimate;
}
