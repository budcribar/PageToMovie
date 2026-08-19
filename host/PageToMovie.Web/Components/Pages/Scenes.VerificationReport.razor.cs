using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_VerificationReport
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public int ClipNumber { get; set; }
    [Parameter] public ClipDialogueVerificationResult? Report { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>Blocking → the clip is wrong (auto-retry regenerates); Degraded → usable but flagged;
    /// Cosmetic → notes only. Same tiers the guard and the correction planner use.</summary>
    internal sealed record IssueTier(string Label, string BadgeClass, string Help, Func<string?, bool> Match);

    internal static readonly IssueTier[] IssueTiers =
    {
        new("Blocking", "text-bg-danger", "the clip is wrong — regenerated automatically when QA retry is on", DialogueIssueKinds.IsBlocking),
        new("Degraded", "text-bg-warning text-dark", "usable, but delivery / timing is off", DialogueIssueKinds.IsDegraded),
        new("Cosmetic", "text-bg-secondary", "notes only — does not lower a verbatim line", DialogueIssueKinds.IsCosmetic),
        new("Other", "text-bg-dark border", "", k => !DialogueIssueKinds.IsBlocking(k) && !DialogueIssueKinds.IsDegraded(k) && !DialogueIssueKinds.IsCosmetic(k)),
    };
}
