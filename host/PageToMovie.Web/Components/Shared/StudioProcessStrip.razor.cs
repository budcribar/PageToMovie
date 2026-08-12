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

    private string ActiveKey => (Active ?? "book").Trim().ToLowerInvariant();

    /// <summary>Previous primary step (setup/book/estimate/film/review spine).</summary>
    private (string? Href, string Label, string? BlockedReason) PrevStep
    {
        get
        {
            if (UseSimple)
            {
                return ActiveKey switch
                {
                    "cast" => ("simple-voice", "Story", null),
                    "film" => ("simple-voice", "Your voice", null),
                    "review" => (ActiveProject.CanScenes ? "scenes?simple=1" : null, "Movie",
                        ActiveProject.CanScenes ? null : ActiveProject.ScenesBlockedReason),
                    _ => (null, "Back", null),
                };
            }

            return ActiveKey switch
            {
                "setup" => (null, "Back", null),
                "book" => NeedsSetup ? ("configuration", "Setup", null) : (null, "Back", null),
                "estimate" or "cast" or "characters" or "locations" =>
                    (BookLocked ? null : "adaptation", "Book", BookLocked ? "Connect API keys in Setup first" : null),
                "film" => (ActiveProject.CanEstimate ? "cost" : null, "Estimate",
                    ActiveProject.CanEstimate ? null : ActiveProject.EstimateBlockedReason),
                "review" => (ActiveProject.CanScenes ? "scenes" : "cost", "Film", null),
                _ => (null, "Back", null),
            };
        }
    }

    private (string? Href, string Label, string? BlockedReason) NextStep
    {
        get
        {
            if (UseSimple)
            {
                return ActiveKey switch
                {
                    "book" or "setup" => ("simple-voice", "Your voice", null),
                    "cast" => (ActiveProject.CanScenes ? "scenes?simple=1" : null, "Movie",
                        ActiveProject.CanScenes ? null : ActiveProject.ScenesBlockedReason),
                    "film" => (ActiveProject.CanReview ? "review" : null, "Review",
                        ActiveProject.CanReview ? null : "Review unlocks after you have a cut"),
                    _ => (null, "Next", null),
                };
            }

            return ActiveKey switch
            {
                "setup" => (BookLocked ? null : "adaptation", "Book", BookLocked ? "Connect API keys first" : null),
                "book" => (ActiveProject.CanEstimate ? "cost" : null, "Estimate",
                    ActiveProject.CanEstimate ? null : ActiveProject.EstimateBlockedReason),
                "estimate" or "cast" or "characters" or "locations" =>
                    (ActiveProject.CanScenes ? "scenes" : null, "Film",
                        ActiveProject.CanScenes
                            ? null
                            : (ActiveProject.CanEstimate
                                ? "Generate movie on Estimate first"
                                : ActiveProject.ScenesBlockedReason)),
                "film" => (ActiveProject.CanReview ? "review" : null, "Review",
                    ActiveProject.CanReview ? null : "Review unlocks after you have a cut"),
                _ => (null, "Next", null),
            };
        }
    }

    private bool CanGoBack => !string.IsNullOrWhiteSpace(PrevStep.Href);
    private bool CanGoNext => !string.IsNullOrWhiteSpace(NextStep.Href);

    private void GoBack()
    {
        if (PrevStep.Href is { Length: > 0 } href)
            Nav.NavigateTo(href);
    }

    private void GoNext()
    {
        if (NextStep.Href is { Length: > 0 } href)
            Nav.NavigateTo(href);
    }
}
