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

public partial class Demo
{
    internal string? _youtubeSyncHint;

    private List<DemoListItem> _demos = new();
    internal bool _loading = true;
    private bool _busy;
    internal string? _error;
    internal string? _message;
    internal string? _highlightId;
    private string _sort = "top";
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            if (!string.IsNullOrEmpty(uri.Fragment))
                _highlightId = uri.Fragment.TrimStart('#');
        }
        catch { /* ignore */ }

        await ReloadAsync();
    }

    private Task SortByTopAsync() => SetSortAsync("top");
    private Task SortByNewAsync() => SetSortAsync("new");

    private async Task SetSortAsync(string sort)
    {
        if (string.Equals(_sort, sort, StringComparison.OrdinalIgnoreCase)) return;
        _sort = sort;
        await ReloadAsync();
    }

    private static string? FormatYoutubeSyncHint(DemoYoutubeSyncInfo? sync, int demoCount)
    {
        if (demoCount > 0) return null;
        if (sync is null) return null;
        if (!string.IsNullOrWhiteSpace(sync.LastError))
            return "YouTube sync: " + sync.LastError + " — reconnect PageToMovieStudio (Admin) and force sync.";
        if (sync.LastSuccessUtc is null)
            return "YouTube channel not synced yet. Admin: connect PageToMovieStudio and sync.";
        return null;
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            var (demos, ytSync) = await Engine.ListDemosDetailedAsync(100, _sort);
            _demos = demos;
            _youtubeSyncHint = FormatYoutubeSyncHint(ytSync, demos.Count);
        }
        catch (Exception ex)
        {
            _error = FriendlyNetError(ex);
            _demos = new();
            _youtubeSyncHint = null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Turn "Failed to fetch" / connection refused into actionable studio help.</summary>
    private static string FriendlyNetError(Exception ex)
    {
        var msg = ex.Message ?? "";
        var full = ex.ToString();
        if (msg.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || full.Contains("ERR_CONNECTION_REFUSED", StringComparison.OrdinalIgnoreCase)
            || ex is HttpRequestException or TaskCanceledException)
        {
            return "Can't reach the studio API. Start PageToMovie.Api (http://localhost:5088) and open the site from that host — Demo films and SignalR both need the API process. YouTube env vars alone don't start the server.";
        }
        return msg;
    }

    private static bool IsOwnDemo(DemoListItem d, AdminSessionService session) =>
        session.IsLoggedIn
        && !string.IsNullOrWhiteSpace(d.CreatedBy)
        && IdentitiesMatch(session.UserId, d.CreatedBy);

    private static bool IdentitiesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Norm(string s) => s.Trim().TrimStart('@');
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    private async Task ForkDemoAsync(DemoListItem d)
    {
        if (string.IsNullOrWhiteSpace(d.ProjectId)) return;
        if (!Session.IsLoggedIn)
        {
            Nav.NavigateTo("/login?returnUrl=/demo");
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            var forked = await Engine.ForkProjectAsync(d.ProjectId);
            if (forked is not null)
            {
                Nav.NavigateTo("adaptation");
            }
            else
            {
                _error = "Could not fork project.";
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ToggleStarAsync(DemoListItem d)
    {
        if (_busy || IsOwnDemo(d, Session)) return;
        if (!Session.IsLoggedIn)
        {
            Nav.NavigateTo("/login?returnUrl=/demo");
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            var (count, me) = d.UpvotedByMe
                ? await Engine.RemoveDemoUpvoteAsync(d.Id)
                : await Engine.UpvoteDemoAsync(d.Id);
            d.UpvoteCount = count;
            d.UpvotedByMe = me;
            if (_sort == "top")
                _demos = _demos
                    .OrderByDescending(x => x.UpvoteCount)
                    .ThenByDescending(x => x.CreatedAt)
                    .ToList();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReportAsync(DemoListItem d)
    {
        if (_busy || _reported.Contains(d.Id)) return;
        _busy = true;
        _error = null;
        try
        {
            await Engine.ReportDemoAsync(d.Id, "Reported from public gallery");
            _reported.Add(d.Id);
            _message = "Thanks — report received. Moderators will review.";
            // If auto-pending, it may disappear from public list.
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Feature 11: fork the studio project behind this public film.</summary>
    private async Task ForkAsync(DemoListItem d)
    {
        if (_busy || !d.CanFork) return;
        if (!Session.IsLoggedIn)
        {
            Nav.NavigateTo("/login?returnUrl=" + Uri.EscapeDataString("/demo#" + d.Id));
            return;
        }

        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var result = await Engine.ForkDemoProjectAsync(d.Id);
            var label = result.Title ?? result.ProjectId ?? "your fork";
            _message = result.Message
                       ?? $"Created “{label}”. Open Home to select it and start adapting.";
            // Land on Home so the user can pick the new fork (parent badge + Sync Origin).
            Nav.NavigateTo("/");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteAsync(string id)
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        try
        {
            await Engine.DeleteDemoAsync(id);
            _message = "Film removed.";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
