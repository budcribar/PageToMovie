using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminDemos
{

    internal bool _loading = true;
    private bool _busy;
    internal string? _error;
    internal string? _message;
    private string? _statusFilter = "";
    internal List<DemoListItem> _demos = new();
    internal YouTubeStatusDto? _yt;
    private string _ytUrl = "";
    private string _ytTitle = "";
    private string _ytDesc = "";

    private readonly (string Key, string Label)[] _filters =
    {
        ("", "All"),
        ("public", "Public"),
        ("removed", "Removed"),
        ("rejected", "Rejected"),
        ("pending", "Pending (legacy)"),
    };

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); } catch { /* */ }
        HandleYouTubeQuery();
        if (!Session.IsAdmin)
        {
            _loading = false;
            return;
        }
        await ReloadAllAsync();
    }

    private void HandleYouTubeQuery()
    {
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (q.TryGetValue("youtube", out var yt))
            {
                if (string.Equals(yt.ToString(), "connected", StringComparison.OrdinalIgnoreCase))
                    _message = "YouTube channel connected.";
                else if (string.Equals(yt.ToString(), "error", StringComparison.OrdinalIgnoreCase)
                         && q.TryGetValue("message", out var m))
                    _error = "YouTube connect failed: " + m.ToString();
            }
        }
        catch { /* ignore */ }
    }

    private async Task ReloadAllAsync()
    {
        await RefreshYouTubeAsync();
        await ReloadAsync();
    }

    private async Task RefreshYouTubeAsync()
    {
        try { _yt = await Api.GetYouTubeStatusAsync(); }
        catch { _yt = null; }
    }

    private async Task SetFilterAsync(string key)
    {
        _statusFilter = key;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (!Session.IsAdmin) return;
        _loading = true;
        _error = null;
        try
        {
            var st = string.IsNullOrEmpty(_statusFilter) ? null : _statusFilter;
            var env = await Api.ListAdminDemosAsync(st, 100);
            _demos = env?.Demos ?? new();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _demos = new();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ConnectYouTubeAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var url = await Api.GetYouTubeConnectUrlAsync("/admin/demos");
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("No connect URL returned.");
            Nav.NavigateTo(url, forceLoad: true);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _busy = false;
        }
    }

    private async Task DisconnectYouTubeAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            await Api.DisconnectYouTubeAsync();
            _message = "YouTube channel disconnected.";
            await RefreshYouTubeAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task SyncChannelAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var (ok, msg, err, added, updated, total) = await Api.SyncYouTubeChannelDemosAsync();
            if (!ok)
            {
                _error = err ?? msg ?? "Channel sync failed.";
                return;
            }
            _message = msg ?? $"Synced {total} video(s) ({added} new, {updated} updated).";
            await ReloadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task RegisterFromYouTubeAsync()
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var (ok, msg, err) = await Api.RegisterDemoFromYouTubeAsync(
                _ytUrl.Trim(),
                _ytTitle.Trim(),
                string.IsNullOrWhiteSpace(_ytDesc) ? null : _ytDesc.Trim());
            if (!ok)
            {
                _error = err ?? "Could not add film.";
                return;
            }
            _message = msg ?? "Added to public gallery.";
            _ytUrl = "";
            _ytTitle = "";
            _ytDesc = "";
            await ReloadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task ReviewAsync(string id, string status)
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        try
        {
            await Api.ReviewDemoAsync(id, status);
            _message = status switch
            {
                "public" => "Marked public.",
                "removed" => "Removed from public gallery.",
                _ => "Status updated.",
            };
            await ReloadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task HardDeleteAsync(string id)
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        try
        {
            await Api.DeleteDemoAsync(id);
            _message = "Demo entry deleted.";
            await ReloadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private static string StatusBadgeClass(string? status) =>
        (status ?? "").ToLowerInvariant() switch
        {
            "public" => "text-bg-success",
            "pending" => "text-bg-warning",
            "rejected" => "text-bg-secondary",
            "removed" => "text-bg-dark",
            _ => "text-bg-light",
        };
}
