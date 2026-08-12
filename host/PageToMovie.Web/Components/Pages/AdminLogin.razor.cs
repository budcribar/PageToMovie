using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminLogin
{

    private string _username = "admin";
    private string _password = "admin";
    private bool _showPassword;
    internal string? _error;
    internal string _status = "Checking session…";
    internal bool _busy;
    private bool _started;

    private void ToggleShowPassword() => _showPassword = !_showPassword;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started) return;
        _started = true;

        try
        {
            await Session.EnsureHydratedAsync();
            if (Session.IsAdmin)
            {
                _busy = true;
                _status = $"Signed in as {Session.UserId}. Opening dashboard…";
                StateHasChanged();
                // forceLoad: full navigation so layout/nav re-bind cleanly
                Nav.NavigateTo("/admin", forceLoad: true);
                return;
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }

        StateHasChanged();
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await LoginAsync();
    }

    private async Task LoginAsync()
    {
        _busy = true;
        _error = null;
        _status = "Signing in…";
        try
        {
            var resp = await Api.LoginAsync(_username ?? "", _password ?? "");
            if (resp is null || !resp.Ok || string.IsNullOrWhiteSpace(resp.Token))
            {
                _error = resp?.Error
                         ?? "Login failed — is PageToMovie.Api running on http://127.0.0.1:5088?";
                return;
            }

            await Session.SetSessionAsync(resp.Token!, resp.UserId, resp.Roles, resp.ExpiresAt);

            if (!Session.IsAdmin)
            {
                await Session.ClearAsync();
                _error = "Token missing admin role. Check PageToMovie:Auth on the API.";
                return;
            }

            _status = "Opening dashboard…";
            Nav.NavigateTo("/admin", forceLoad: true);
        }
        catch (HttpRequestException ex)
        {
            _error = $"Cannot reach API: {ex.Message}. Start PageToMovie.Api (port 5088).";
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
