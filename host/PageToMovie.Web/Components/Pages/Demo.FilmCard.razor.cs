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

namespace PageToMovie.Web.Components.Pages;

public partial class Demo_FilmCard
{
    [Parameter, EditorRequired] public DemoListItem Film { get; set; } = default;
    [Parameter] public bool IsHighlight { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool IsReported { get; set; }
    [Parameter] public EventCallback<DemoListItem> OnToggleStar { get; set; }
    [Parameter] public EventCallback<DemoListItem> OnFork { get; set; }
    [Parameter] public EventCallback<DemoListItem> OnReport { get; set; }
    [Parameter] public EventCallback<string> OnDelete { get; set; }

    private string About => AboutText(Film);
    private string? YtId => ResolveYoutubeId(Film);
    private string? YtWatchUrl => YtId is not null
        ? YouTubeVideoId.WatchUrl(YtId)
        : AbsoluteYoutubeUrl(Film.YoutubeUrl);
    private string? ThumbUrl => YtId is not null
        ? YouTubeVideoId.ThumbnailUrl(YtId, "hqdefault")
        : null;

    private bool IsOwnDemo =>
        Session.IsLoggedIn
        && !string.IsNullOrWhiteSpace(Film.CreatedBy)
        && IdentitiesMatch(Session.UserId, Film.CreatedBy);

    private bool CanRemove
    {
        get
        {
            if (!Session.IsLoggedIn) return false;
            if (Session.IsAdmin) return true;
            return IdentitiesMatch(Session.UserId, Film.CreatedBy);
        }
    }

    private string StarButtonTitle
    {
        get
        {
            if (IsOwnDemo) return "You can’t star your own demo";
            if (!Session.IsLoggedIn) return "Sign in to star this film";
            return Film.UpvotedByMe ? "Remove star" : "Star this film";
        }
    }

    /// <summary>
    /// Gallery blurb is the project's stored Look (visual medium). Do not invent a look
    /// or fall back to a generic cinematic line when Look is missing.
    /// </summary>
    internal static string AboutText(DemoListItem d)
    {
        if (!string.IsNullOrWhiteSpace(d.Look))
            return d.Look.Trim();
        if (!string.IsNullOrWhiteSpace(d.VisualMedium)
            && !string.Equals(d.VisualMedium.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            return d.VisualMedium.Trim();
        return "Short film.";
    }

    private static string? ResolveYoutubeId(DemoListItem d)
    {
        var fromId = YouTubeVideoId.Extract(d.YoutubeId);
        if (!string.IsNullOrWhiteSpace(fromId)) return fromId;
        return YouTubeVideoId.Extract(d.YoutubeUrl);
    }

    private static string? AbsoluteYoutubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();
        if (s.StartsWith("//", StringComparison.Ordinal))
            s = "https:" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https"))
            return null;
        var id = YouTubeVideoId.Extract(s);
        return id is not null ? YouTubeVideoId.WatchUrl(id) : uri.AbsoluteUri;
    }

    private static bool IdentitiesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Norm(string s) => s.Trim().TrimStart('@');
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}
