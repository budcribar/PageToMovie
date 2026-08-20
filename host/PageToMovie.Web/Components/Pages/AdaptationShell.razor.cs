using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationShell
{
    [Parameter, EditorRequired] public required AdaptationPageBase Host { get; set; }
    [Parameter, EditorRequired] public string Step { get; set; } = "book";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    /// <summary>Optional top-right header actions (Screenplay-style green next control).</summary>
    [Parameter] public RenderFragment? HeaderActions { get; set; }

    private bool ShowJobPanel =>
        !Host.Busy && AdaptationPageBase.AdaptationStepUi.ShowJobPanel(Session.IsAdmin, Host.Jobs.Job, Step);

    /// <summary>
    /// True when the generic message banner would just restate what the finished job panel already
    /// shows. Suppress the banner so the same outcome isn't printed twice.
    /// </summary>
    private bool MessageDuplicatesJobPanel =>
        ShowJobPanel && !Host.Jobs.JobRunning && Host.Jobs.Job is not null
        && string.Equals(
            Host.Message,
            AdaptationPageBase.AdaptationStepUi.OperatorJobDoneMessage(Host.Jobs.Job),
            StringComparison.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
    }

    private bool ShowNextBanner =>
        AdaptationPageBase.AdaptationStepUi.ShowNextStepBanner(
            Host.Status, Host.SuppressGuidanceBanners, Step ?? "");

    private string ResolvePageTitle()
    {
        if (Step == "shots")
            return L["Adaptation.ShotPlanTitle"];
        var filmTitle = Host.Status?.Screenplay?.Title?.Trim();
        if (!string.IsNullOrEmpty(filmTitle))
            return filmTitle;
        var projectName = Host.ProjectLabel?.Trim();
        if (!string.IsNullOrEmpty(projectName))
            return projectName;
        return L["Adaptation.BookAndScreenplay"];
    }
}
