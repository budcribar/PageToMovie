using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Review_MovieReport
{
    [Parameter] public MovieAutoReviewReport? Report { get; set; }
    [Parameter] public EventCallback OnDismiss { get; set; }
    [Parameter] public EventCallback OnExpandAll { get; set; }
    [Parameter] public EventCallback OnCollapseAll { get; set; }
    [Parameter] public EventCallback<string> OnToggleGroup { get; set; }
    [Parameter] public Func<string, bool>? IsGroupExpanded { get; set; }

    private bool IsSceneGroupExpanded(string rangeStr) =>
        IsGroupExpanded?.Invoke(rangeStr) ?? false;

    private static string ScoreBadgeClass(int score)
    {
        if (score >= 8) return "bg-success";
        if (score >= 6) return "bg-warning text-dark";
        return "bg-danger";
    }
}
