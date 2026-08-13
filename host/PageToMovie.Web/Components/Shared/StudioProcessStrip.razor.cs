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
    private const string ReviewStep = "review";

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
    private (string? Href, string Label, string? BlockedReason) PrevStep => ComputePrevStep();

    private (string? Href, string Label, string? BlockedReason) ComputePrevStep()
    {
        if (UseSimple) return PrevStepSimple();
        return PrevStepFull();
    }

    private (string? Href, string Label, string? BlockedReason) PrevStepSimple() => ActiveKey switch
    {
        "cast" => ("simple-voice", "Story", null),
        "film" => ("simple-voice", "Your voice", null),
        ReviewStep => (ActiveProject.CanScenes ? "scenes?simple=1" : null, "Movie",
            ActiveProject.CanScenes ? null : ActiveProject.ScenesBlockedReason),
        _ => (null, "Back", null),
    };

    private (string? Href, string Label, string? BlockedReason) PrevStepFull() => ActiveKey switch
    {
        "setup" => (null, "Back", null),
        "book" => PrevFromBook(),
        "estimate" or "cast" or "characters" or "locations" => PrevFromEstimateOrCast(),
        "film" => PrevFromFilm(),
        ReviewStep => PrevFromReview(),
        _ => (null, "Back", null),
    };

    private (string? Href, string Label, string? BlockedReason) PrevFromBook() =>
        NeedsSetup ? ("configuration", "Setup", null) : (null, "Back", null);

    private (string? Href, string Label, string? BlockedReason) PrevFromEstimateOrCast() =>
        (BookLocked ? null : "adaptation", "Book", BookLocked ? "Connect API keys in Setup first" : null);

    private (string? Href, string Label, string? BlockedReason) PrevFromFilm() =>
        (ActiveProject.CanEstimate ? "cost" : null, "Estimate",
            ActiveProject.CanEstimate ? null : ActiveProject.EstimateBlockedReason);

    private (string? Href, string Label, string? BlockedReason) PrevFromReview() =>
        (ActiveProject.CanScenes ? "scenes" : "cost", "Film", null);

    private (string? Href, string Label, string? BlockedReason) NextStep
    {
        get
        {
            if (UseSimple) return NextStepSimple();
            return NextStepFull();
        }
    }

    private (string? Href, string Label, string? BlockedReason) NextStepSimple() => ActiveKey switch
    {
        "book" or "setup" => ("simple-voice", "Your voice", null),
        "cast" => NextSimpleFromCast(),
        "film" => NextFromFilm(),
        _ => (null, "Next", null),
    };

    private (string? Href, string Label, string? BlockedReason) NextSimpleFromCast() =>
        (ActiveProject.CanScenes ? "scenes?simple=1" : null, "Movie",
            ActiveProject.CanScenes ? null : ActiveProject.ScenesBlockedReason);

    private (string? Href, string Label, string? BlockedReason) NextStepFull() => ActiveKey switch
    {
        "setup" => NextFromSetup(),
        "book" => NextFromBook(),
        "estimate" or "cast" or "characters" or "locations" => NextFromEstimateOrCast(),
        "film" => NextFromFilm(),
        _ => (null, "Next", null),
    };

    private (string? Href, string Label, string? BlockedReason) NextFromSetup() =>
        (BookLocked ? null : "adaptation", "Book", BookLocked ? "Connect API keys first" : null);

    private (string? Href, string Label, string? BlockedReason) NextFromBook() =>
        (ActiveProject.CanEstimate ? "cost" : null, "Estimate",
            ActiveProject.CanEstimate ? null : ActiveProject.EstimateBlockedReason);

    private (string? Href, string Label, string? BlockedReason) NextFromEstimateOrCast() =>
        (ActiveProject.CanScenes ? "scenes" : null, "Film",
            ActiveProject.CanScenes
                ? null
                : (ActiveProject.CanEstimate
                    ? "Generate movie on Estimate first"
                    : ActiveProject.ScenesBlockedReason));

    private (string? Href, string Label, string? BlockedReason) NextFromFilm() =>
        (ActiveProject.CanReview ? ReviewStep : null, "Review",
            ActiveProject.CanReview ? null : "Review unlocks after you have a cut");

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
