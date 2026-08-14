using System;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Layout;

public partial class NavMenu : IDisposable
{
        [Parameter] public bool Collapsed { get; set; }
    private bool _started;
        private bool _userMenuOpen;

        private string UserInitials
        {
            get
            {
                // Initials from public handle, not email
                var id = Session.DisplayHandle.TrimStart('@').Trim();
                if (id.Length == 0) return "?";
                var parts = id.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
                return id.Length >= 2
                    ? id[..2].ToUpperInvariant()
                    : id.ToUpperInvariant();
            }
        }

        private string MediaFolderTitle
        {
            get
            {
                if (MediaFolder.IsConnected) return MediaFolder.FolderName;
                if (MediaFolder.NeedsReconnect)
                    return $"Re-grant access to {MediaFolder.PendingReconnectFolderName}";
                return "Store gen clips on your disk";
            }
        }

        private string MediaFolderLabel
        {
            get
            {
                if (MediaFolder.IsConnected) return $"Media: {MediaFolder.FolderName}";
                if (MediaFolder.NeedsReconnect)
                    return $"Reconnect {MediaFolder.PendingReconnectFolderName}…";
                return "Connect media folder…";
            }
        }

        private void ToggleUserMenu() => _userMenuOpen = !_userMenuOpen;

        private void CloseUserMenu() => _userMenuOpen = false;

        private void ToggleViewAsUser()
        {
            Session.SetViewAsUser(!Session.ViewAsUser);
            _userMenuOpen = false;
            // Land on Home so the preview applies immediately across the app (flag persists for the session).
            Nav.NavigateTo("/");
        }
        private string? _themedProjectId;

        private string? _sessionUserId;
        private Action<CultureInfo>? _onCultureChanged;

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender || _started) return;
            _started = true;
            await Session.EnsureHydratedAsync();
            _sessionUserId = Session.UserId;
            Session.Changed += OnSessionChanged;
            ActiveProject.Changed += OnProjectChanged;
            await ActiveProject.RefreshFromServerAsync(Engine);
            await RefreshThemeAsync();
            await MediaFolder.TryReconnectAsync();
            StateHasChanged();
        }

        private void OnSessionChanged() => InvokeAsync(async () =>
        {
            if (!Session.IsLoggedIn)
            {
                _userMenuOpen = false;
                ActiveProject.Clear();
                _sessionUserId = null;
            }
            else if (!string.Equals(_sessionUserId, Session.UserId, StringComparison.OrdinalIgnoreCase))
            {
                // Different account — drop previous user's active project (e.g. Odyssey).
                _sessionUserId = Session.UserId;
                ActiveProject.Clear();
                try { await ActiveProject.RefreshFromServerAsync(Engine); }
                catch { /* offline */ }
            }
            StateHasChanged();
        });

        // Readiness is refreshed by RefreshFromApiAsync / callers after Set — only re-render here
        // (RefreshReadinessAsync must not be invoked from Changed or it loops).
        private void OnProjectChanged() => InvokeAsync(async () =>
        {
            await RefreshThemeAsync();
            StateHasChanged();
        });

        /// <summary>Pulls ui_theme from the active project's config and applies it to the DOM.
        /// Cheap no-op re-fetch guarded by project id so it only hits the API on an actual switch.</summary>
        private async Task RefreshThemeAsync()
        {
            var pid = ActiveProject.HasProject ? ActiveProject.ProjectId : null;
            if (string.Equals(pid, _themedProjectId, StringComparison.OrdinalIgnoreCase)) return;
            _themedProjectId = pid;

            var pref = "dark";
            if (pid is { Length: > 0 })
            {
                try
                {
                    var dto = await Engine.GetConfigAsync(pid);
                    if (dto?.Config is { } cfg &&
                        cfg.TryGetValue("ui_theme", out var el) &&
                        el.ValueKind == System.Text.Json.JsonValueKind.String)
                        pref = el.GetString() ?? "dark";
                }
                catch { /* API blip — keep default */ }
            }

            Theme.Set(pref);
            try { await Js.InvokeVoidAsync("fsTheme.apply", pref); }
            catch { /* prerender / no JS yet */ }
        }

        private async Task ConnectMediaFolderAsync()
        {
            _userMenuOpen = false;
            if (MediaFolder.NeedsReconnect)
                await MediaFolder.ReconnectAsync();
            else
                await MediaFolder.ConnectFolderAsync();
            await MediaFolder.EnsureHubHookAsync();
            StateHasChanged();
        }

        private async Task LogoutAsync()
        {
            _userMenuOpen = false;
            // I7/I11: release leases + presence before clearing session
            var pid = ActiveProject.ProjectId;
            if (!string.IsNullOrWhiteSpace(pid))
            {
                try
                {
                    await Engine.PresenceLeaveAsync(pid); // also releases leases server-side
                    await Engine.ReleaseAllProjectLeasesAsync(pid);
                }
                catch { /* soft */ }
            }
            await Engine.LogoutAsync();
            // LogoutAsync already ClearAsync's the session; ensure storage is gone before forceLoad.
            await Session.ClearAsync();
            ActiveProject.Clear();
            Nav.NavigateTo("/login", forceLoad: true);
        }

        protected override void OnInitialized()
        {
            MediaFolder.Changed += OnMediaFolderChanged;
            _onCultureChanged = _ => InvokeAsync(StateHasChanged);
            L.CultureChanged += _onCultureChanged;
        }

        private void OnMediaFolderChanged() => _ = InvokeAsync(StateHasChanged);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            if (_onCultureChanged is not null)
                L.CultureChanged -= _onCultureChanged;
            MediaFolder.Changed -= OnMediaFolderChanged;
            Session.Changed -= OnSessionChanged;
            ActiveProject.Changed -= OnProjectChanged;
        }
}
