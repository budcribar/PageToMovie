using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Review_MovieReport
{
    [Parameter] public MovieAutoReviewReport? Report { get; set; }

    /// <summary>Body hidden, header (score + verdict + the toggle back) still shown.</summary>
    [Parameter] public bool Collapsed { get; set; }
    [Parameter] public EventCallback OnToggleCollapsed { get; set; }
    [Parameter] public EventCallback OnExpandAll { get; set; }
    [Parameter] public EventCallback OnCollapseAll { get; set; }
    [Parameter] public EventCallback<string> OnToggleGroup { get; set; }
    [Parameter] public Func<string, bool>? IsGroupExpanded { get; set; }

    /// <summary>When set, only that scene's sequence-group notes — not the full-movie writeup.</summary>
    [Parameter] public int? FilterSceneNumber { get; set; }

    private bool IsSceneGroupExpanded(string rangeStr) =>
        IsGroupExpanded?.Invoke(rangeStr) ?? false;

    internal IReadOnlyList<MovieSceneGroupFeedback> VisibleGroups =>
        Report is null
            ? Array.Empty<MovieSceneGroupFeedback>()
            : FilterSceneNumber is int sn
                ? Report.GroupsForScene(sn)
                : Report.GroupFeedback;

    internal bool ShowMovieOverview => FilterSceneNumber is null;

    internal bool ShowCard =>
        Report is not null && (ShowMovieOverview || VisibleGroups.Count > 0);

    /// <summary>Scene-filtered notes stay open; only the full-movie body honors Hide.</summary>
    internal bool ShowBody => !Collapsed || !ShowMovieOverview;

    internal static string ScoreBadgeClass(int score)
    {
        if (score >= 8) return "bg-success";
        if (score >= 6) return "bg-warning text-dark";
        return "bg-danger";
    }
}
