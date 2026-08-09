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


    private bool OutlineEnabled => AdaptationPageBase.OutlineEnabled(Host.Status);

    private bool ShotsEnabled => AdaptationPageBase.ShotsEnabled(Host.Status);

    /// <summary>There was a prior sign-off, but the draft has since changed (vs. never approved at all).</summary>
    private bool NeedsReapprove =>
        Host.Status?.Screenplay is { } s
        && !s.ReadyForShots
        && !string.IsNullOrWhiteSpace(s.SignedHash);

    private bool ShowJobPanel => AdaptationPageBase.ShowJobPanel(Session.IsAdmin, Host.Job, Step);

    /// <summary>
    /// True when the generic message banner would just restate what the finished job panel already
    /// shows (on job completion the host sets Message = OperatorJobDoneMessage). Suppress the banner
    /// in that case so the same outcome isn't printed twice. Non-admins get no job panel, so their
    /// banner still shows.
    /// </summary>
    private bool MessageDuplicatesJobPanel =>
        ShowJobPanel && !Host.JobRunning && Host.Job is not null
        && string.Equals(Host.Message, AdaptationPageBase.OperatorJobDoneMessage(Host.Job), StringComparison.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
    }

    private bool ShowNextBanner =>
        AdaptationPageBase.ShowNextStepBanner(Host.Status, Host.SuppressGuidanceBanners, Step ?? "");
}
