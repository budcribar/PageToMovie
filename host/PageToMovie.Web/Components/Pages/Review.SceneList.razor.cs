using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

#pragma warning disable S101 // Blazor dotted filename Review.SceneList.razor requires Review_SceneList
public partial class Review_SceneList : PageSliceComponent
{
    [CascadingParameter] public required Review Host { get; set; }
    [CascadingParameter] public Review.ReviewListState? List { get; set; }
    [CascadingParameter] public Review.ReviewJobs? Jobs { get; set; }
    [CascadingParameter] public Review.ReviewPlayback? Playback { get; set; }
    [CascadingParameter] public Review.ReviewAutoReview? AutoReview { get; set; }
}
#pragma warning restore S101
