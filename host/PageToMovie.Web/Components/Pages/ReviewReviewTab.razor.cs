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

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ReviewReviewTab : PageSliceComponent
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
