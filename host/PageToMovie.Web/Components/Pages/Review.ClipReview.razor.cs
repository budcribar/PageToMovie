using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Review_ClipReview : PageSliceComponent
{
    [CascadingParameter] public required Review Host { get; set; }
    [CascadingParameter] public Review.ReviewListState? List { get; set; }
    [CascadingParameter] public Review.ReviewJobs? Jobs { get; set; }
    [CascadingParameter] public Review.ReviewPlayback? Playback { get; set; }
    [CascadingParameter] public Review.ReviewAutoReview? AutoReview { get; set; }

    private static string ReviewButtonLabel(bool autoBusy, bool hasDraft, bool editing)
    {
        if (autoBusy) return "Reviewing…";
        if (hasDraft && !editing) return "Review again";
        return "Review";
    }
}
