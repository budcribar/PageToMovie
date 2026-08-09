using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminBookCache
{

    private BookCacheAdminDto? _snap;
    private string? _error;
    private bool _busy;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started) return;
        _started = true;
        await Session.EnsureHydratedAsync();
        if (!Session.IsAdmin)
        {
            Nav.NavigateTo("/admin/login", forceLoad: true);
            return;
        }
        await RefreshAsync();
        StateHasChanged();
    }

    private bool _started;

    private async Task RefreshAsync()
    {
        if (!Session.IsAdmin) return;
        _busy = true;
        _error = null;
        try
        {
            _snap = await Api.GetAdminBookCacheAsync(120);
            if (_snap is null || !_snap.Ok)
                _error = "Could not load book cache snapshot.";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _snap = null;
        }
        finally
        {
            _busy = false;
        }
    }

    private static string ShortId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "—";
        return id.Length <= 14 ? id : id[..10] + "…";
    }

    private static string FormatBytes(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:0.#} KB";
        return $"{n / (1024.0 * 1024.0):0.##} MB";
    }

    private static string FormatWhen(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (DateTimeOffset.TryParse(iso, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return iso.Length > 16 ? iso[..16] : iso;
    }

    private static string FormatExpiry(long? unix)
    {
        if (unix is null or 0) return "—";
        var dt = DateTimeOffset.FromUnixTimeSeconds(unix.Value);
        var left = dt - DateTimeOffset.UtcNow;
        if (left.TotalDays < 0) return "expired";
        if (left.TotalDays >= 2) return $"{left.TotalDays:0}d left";
        return $"{left.TotalHours:0}h left";
    }

    private string GetBookTitle(string bookId)
    {
        var b = _snap?.Books?.FirstOrDefault(x => x.BookId == bookId);
        return string.IsNullOrWhiteSpace(b?.BookTitle) ? bookId : b.BookTitle;
    }
}
