using System;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PageToMovie.Core.Options;

namespace PageToMovie.Web.Components.Layout;

public partial class MainLayout : IDisposable
{
    private bool _sidebarCollapsed;
    private bool _hydrated;
    private bool _locationHooked;
    private bool _showTermsModal;
    private string _currentUserId = "";
    private List<string> _presenceOthers = new();
    private System.Threading.Timer? _presenceTimer;
    private string? _presenceProjectId;

    protected override void OnInitialized()
    {
        ActiveProject.Changed += OnActiveProjectChanged;
        if (_locationHooked) return;
        _locationHooked = true;
        // Re-check on every navigation so /demo stays open to guests and studio routes re-gate.
        Nav.LocationChanged += OnLocationChanged;
        MediaFolder.Changed += OnMediaFolderChanged;
    }

    private void OnMediaFolderChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnActiveProjectChanged()
    {
        _ = InvokeAsync(async () =>
        {
            await RefreshPresenceAsync();
            StateHasChanged();
        });
    }

    private async Task RefreshPresenceAsync()
    {
        var pid = ActiveProject.ProjectId;
        if (string.IsNullOrWhiteSpace(pid) || !Session.IsLoggedIn)
        {
            _presenceOthers = new();
            _presenceProjectId = null;
            return;
        }
        try
        {
            await Engine.PresenceHeartbeatAsync(pid);
            var list = await Engine.ListPresenceAsync(pid);
            var me = (Session.UserId ?? "").Trim();
            _presenceOthers = list
                .Select(p => p.UserId)
                .Where(u => !string.IsNullOrWhiteSpace(u)
                    && !string.Equals(u, me, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => u)
                .ToList();
            _presenceProjectId = pid;
            EnsurePresenceTimer();
        }
        catch
        {
            /* soft */
        }
    }

    private void EnsurePresenceTimer()
    {
        if (_presenceTimer is not null) return;
        _presenceTimer = new System.Threading.Timer(_ =>
        {
            _ = InvokeAsync(async () =>
            {
                await RefreshPresenceAsync();
                StateHasChanged();
            });
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        if (!_hydrated) return;
        _ = InvokeAsync(async () =>
        {
            try { await EnforceLoginGateAsync(); }
            catch { /* ignore */ }
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hydrated) return;
        _hydrated = true;
        try
        {
            var v = await Js.InvokeAsync<string?>("localStorage.getItem", "fs.sidebarCollapsed");
            if (string.Equals(v, "1", StringComparison.Ordinal))
            {
                _sidebarCollapsed = true;
                StateHasChanged();
            }
        }
        catch { /* ignore */ }

        // Local/dev: ?admin=1 — Railway: ?me=LOGIN_OVERRIDE_SECRET
        await TryBootstrapAdminFromQueryAsync();
        await TryBootstrapOperatorOverrideFromQueryAsync();
        // Dev-only: when the server runs on fakes, auto-sign-in a dev user (no login screen).
        await TryBootstrapFakesDevLoginAsync();

        // Short-lived media tokens for &lt;img&gt;/&lt;video&gt; (never put session JWT in query).
        try
        {
            await Session.EnsureHydratedAsync();
            if (Session.IsLoggedIn)
            {
                await Engine.EnsureMediaAccessAsync();
                // Client storage feature 8: listen for finished clips app-wide so a
                // local-save fallback warning can fire even if the user leaves Scenes.
                try { await MediaFolder.EnsureHubHookAsync(); } catch { /* optional */ }
            }
        }
        catch { /* optional */ }

        // Signed-in required for studio pages (projects, config keys, import/OCR).
        // Public: /demo, /about, /login, /signup — no account required.
        await EnforceLoginGateAsync();
        await CheckTermsAcceptanceAsync();
    }

    private async Task CheckTermsAcceptanceAsync()
    {
        try
        {
            await Session.EnsureHydratedAsync();
            if (Session.IsLoggedIn && !string.IsNullOrWhiteSpace(Session.UserId))
            {
                _currentUserId = Session.UserId;
                var hasAccepted = await Engine.HasAcceptedTermsAsync(_currentUserId);
                if (!hasAccepted)
                {
                    _showTermsModal = true;
                    StateHasChanged();
                }
            }
        }
        catch { /* optional */ }
    }

    private void OnTermsAccepted()
    {
        _showTermsModal = false;
        StateHasChanged();
    }

    private async Task EnforceLoginGateAsync()
    {
        try
        {
            await Session.EnsureHydratedAsync();
            if (Session.IsLoggedIn)
                return;

            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            var path = uri.AbsolutePath.TrimStart('/');
            if (IsPublicPath(path))
                return;

            // Never put ?me=SECRET into returnUrl (leaks override; also confuses login).
            var returnPath = string.IsNullOrEmpty(path) ? "/" : "/" + path;
            var qs = new List<string> { "returnUrl=" + Uri.EscapeDataString(returnPath) };

            // Surface why operator override did not sign the user in (if they tried ?me=).
            if (QueryHelpers.ParseQuery(uri.Query).TryGetValue("me", out var meVals))
            {
                var me = meVals.FirstOrDefault()?.Trim() ?? "";
                if (me.Length == 0)
                    qs.Add("overrideError=" + Uri.EscapeDataString("missing"));
                else if (me.Length < AuthOptions.MinOperatorOverrideSecretLength)
                    qs.Add("overrideError=" + Uri.EscapeDataString("short"));
                else
                    qs.Add("overrideError=" + Uri.EscapeDataString("failed"));
            }

            Nav.NavigateTo("/login?" + string.Join("&", qs), forceLoad: false);
        }
        catch
        {
            // ignore navigation blips during circuit start
        }
    }

    /// <summary>
    /// Routes that work without sign-in (demo gallery is public for everyone).
    /// </summary>
    private static bool IsPublicPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false; // home requires login
        path = path.Split('?', '#')[0].Trim().TrimStart('/');
        // Tolerate trailing slash: "demo/" → "demo"
        path = path.TrimEnd('/');
        if (path.Equals("login", StringComparison.OrdinalIgnoreCase)
            || path.Equals("signup", StringComparison.OrdinalIgnoreCase)
            || path.Equals("about", StringComparison.OrdinalIgnoreCase)
            || path.Equals("demo", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("admin/login", StringComparison.OrdinalIgnoreCase))
            return true;
        // Nested public demo routes e.g. demo/xyz — not admin
        if (path.StartsWith("demo/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("admin/", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Query string bootstrap for local runs only:
    /// <c>?admin=1</c> → admin/admin (dev defaults),
    /// <c>?admin=admin:secret</c> → username:password.
    /// </summary>
    private async Task TryBootstrapAdminFromQueryAsync()
    {
        try
        {
            await Session.EnsureHydratedAsync();
            if (Session.IsLoggedIn) return;

            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            if (!IsLocalHost(uri.Host) && !Env.IsDevelopment())
                return;

            if (!QueryHelpers.ParseQuery(uri.Query).TryGetValue("admin", out var vals))
                return;
            var raw = vals.FirstOrDefault()?.Trim() ?? "";
            if (raw.Length == 0) return;

            string user = "admin", pass = "admin";
            if (raw is "1" or "true" or "yes")
            {
                // defaults
            }
            else if (raw.Contains(':'))
            {
                var parts = raw.Split(':', 2);
                user = parts[0].Trim();
                pass = parts[1];
            }
            else
            {
                pass = raw;
            }

            var login = await Engine.LoginAsync(user, pass);
            if (login?.Ok == true && !string.IsNullOrWhiteSpace(login.Token))
                await ApplyLoginAndStripQueryAsync(login, uri, user);
        }
        catch
        {
            // silent
        }
    }

    /// <summary>
    /// Dev-only login bypass: when the server runs with fakes enabled it exposes
    /// <c>POST /api/auth/dev-login</c>, which returns a deterministic dev-user session so the whole
    /// UI is browsable without signing in. In any real deployment the endpoint 404s and this is a
    /// silent no-op, so the normal login gate still applies. The server is the sole authority for
    /// whether fakes mode is on — this cannot be forced on from the client.
    /// </summary>
    private async Task TryBootstrapFakesDevLoginAsync()
    {
        try
        {
            await Session.EnsureHydratedAsync();
            if (Session.IsLoggedIn) return;

            var login = await Engine.TryDevLoginAsync();
            if (login?.Ok == true && !string.IsNullOrWhiteSpace(login.Token))
                await Session.SetSessionAsync(login.Token!, login.UserId, login.Roles, login.ExpiresAt);
        }
        catch
        {
            // Not fakes mode (or endpoint unavailable) — fall through to the normal login gate.
        }
    }

    /// <summary>
    /// Production-safe operator override: <c>?me=YOUR_SECRET</c> when Railway has
    /// env <c>PageToMovie_LOGIN_OVERRIDE</c> set to that secret
    /// (min <see cref="AuthOptions.MinOperatorOverrideSecretLength"/> chars).
    /// Always runs when <c>me</c> is present — even if a stale non-operator session
    /// is already logged in (so Configuration / admin stay reachable).
    /// </summary>
    private async Task TryBootstrapOperatorOverrideFromQueryAsync()
    {
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            if (!QueryHelpers.ParseQuery(uri.Query).TryGetValue("me", out var vals))
                return;
            var secret = vals.FirstOrDefault()?.Trim() ?? "";
            // Must match AdminAuthService — empty / too-short ignored (login page explains).
            if (secret.Length < AuthOptions.MinOperatorOverrideSecretLength) return;

            await Session.EnsureHydratedAsync();
            // Replace any existing session so a normal user JWT does not block operator access.
            if (Session.IsLoggedIn)
                await Session.ClearAsync();

            var login = await Engine.LoginWithOperatorOverrideAsync(secret);
            if (login?.Ok == true && !string.IsNullOrWhiteSpace(login.Token))
                await ApplyLoginAndStripQueryAsync(login, uri, login.UserId ?? "admin");
        }
        catch
        {
            // silent — wrong secret / missing env just fails closed (overrideError on login)
        }
    }

    private async Task ApplyLoginAndStripQueryAsync(LoginResponse login, Uri uri, string fallbackUser)
    {
        await Session.SetSessionAsync(
            login.Token!,
            login.UserId ?? fallbackUser,
            login.Roles,
            login.ExpiresAt);
        // Drop any previous account's active project before the next page loads it.
        ActiveProject.Clear();
        var clean = uri.GetLeftPart(UriPartial.Path);
        if (!string.IsNullOrEmpty(uri.Fragment))
            clean += uri.Fragment;
        Nav.NavigateTo(clean, replace: true);
    }

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "::1" ||
        host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    private async Task ToggleSidebarAsync()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        try
        {
            await Js.InvokeVoidAsync(
                "localStorage.setItem",
                "fs.sidebarCollapsed",
                _sidebarCollapsed ? "1" : "0");
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        MediaFolder.Changed -= OnMediaFolderChanged;
        _presenceTimer?.Dispose();
        _presenceTimer = null;
        ActiveProject.Changed -= OnActiveProjectChanged;
        if (_locationHooked)
        {
            Nav.LocationChanged -= OnLocationChanged;
            _locationHooked = false;
        }
    }
}
