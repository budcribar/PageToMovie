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

public partial class Home_DemoFilmsCard : PageSliceComponent
{
    [CascadingParameter] public Home Host { get; set; } = default;
    [CascadingParameter] public Home.HomeCosts? Costs { get; set; }

    private static string DemoHref(string? yt, DemoListItem d)
    {
        if (yt is not null)
            return YouTubeVideoId.WatchUrl(yt);
        if (!string.IsNullOrWhiteSpace(d.YoutubeUrl) && Uri.TryCreate(d.YoutubeUrl.Trim(), UriKind.Absolute, out _))
            return d.YoutubeUrl.Trim();
        return "/demo";
    }
}
