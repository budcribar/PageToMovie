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
    [Parameter, EditorRequired] public DemoListItem Film { get; set; } = default!;
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

    private static string AboutText(DemoListItem d)
    {
        if (!string.IsNullOrWhiteSpace(d.Description))
            return d.Description.Trim();
        var title = !string.IsNullOrWhiteSpace(d.Title) ? d.Title.Trim() : d.ProjectId?.Trim();
        if (!string.IsNullOrWhiteSpace(title))
            return $"A cinematic short film adaptation of “{title}” produced with PageToMovie.";
        return "A cinematic short film adaptation produced with PageToMovie.";
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

    private static string RelativeDate(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 2) return "Just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 14) return $"{(int)span.TotalDays}d ago";
        if (local.Year == DateTime.Now.Year) return local.ToString("MMM d");
        return local.ToString("MMM d, yyyy");
    }

    private static bool IdentitiesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Norm(string s) => s.Trim().TrimStart('@');
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}
