using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationShell
{
    [Parameter, EditorRequired] public AdaptationPageBase Host { get; set; } = null!;
    [Parameter, EditorRequired] public string Step { get; set; } = "book";
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private bool OutlineEnabled => AdaptationPageBase.AdaptationStepUi.OutlineEnabled(Host.Status);

    private bool ShotsEnabled => AdaptationPageBase.AdaptationStepUi.ShotsEnabled(Host.Status);

    /// <summary>There was a prior sign-off, but the draft has since changed (vs. never approved at all).</summary>
    private bool NeedsReapprove =>
        Host.Status?.Screenplay is { } s
        && !s.ReadyForShots
        && !string.IsNullOrWhiteSpace(s.SignedHash);

    private bool ShowJobPanel =>
        AdaptationPageBase.AdaptationStepUi.ShowJobPanel(Session.IsAdmin, Host.Jobs.Job, Step);

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
}
