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
using Microsoft.AspNetCore.WebUtilities;

namespace PageToMovie.Web.Components.Pages;

public partial class Join
{
    private string? _token;
    internal bool _busy;
    internal string? _error;
    internal string? _forkedProjectId;
    internal string? _forkedTitle;

    protected override async Task OnInitializedAsync()
    {
        var uri = new Uri(Nav.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);
        _token = query.TryGetValue("token", out var t) ? t.ToString() : null;

        await Session.EnsureHydratedAsync();
        if (!string.IsNullOrWhiteSpace(_token) && Session.IsLoggedIn)
            await AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var res = await Engine.AcceptInviteAsync(_token!);
            if (res is { Ok: true })
            {
                _forkedProjectId = res.ProjectId;
                _forkedTitle = res.Title;
            }
            else
            {
                _error = res?.Error ?? "Could not accept this invite.";
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
}
