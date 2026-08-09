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

public partial class NavMenu
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

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender || _started) return;
            _started = true;
            await Session.EnsureHydratedAsync();
            Session.Changed += OnSessionChanged;
            ActiveProject.Changed += OnProjectChanged;
            await ActiveProject.RefreshFromServerAsync(Engine);
            await RefreshThemeAsync();
            await MediaFolder.TryReconnectAsync();
            StateHasChanged();
        }

        private void OnSessionChanged() => InvokeAsync(() =>
        {
            if (!Session.IsLoggedIn)
                _userMenuOpen = false;
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
            await Engine.LogoutAsync();
            // LogoutAsync already ClearAsync's the session; ensure storage is gone before forceLoad.
            await Session.ClearAsync();
            Nav.NavigateTo("/login", forceLoad: true);
        }

        protected override void OnInitialized()
        {
            MediaFolder.Changed += OnMediaFolderChanged;
            L.CultureChanged += OnCultureChanged;
        }

        private void OnMediaFolderChanged() => _ = InvokeAsync(StateHasChanged);
        private void OnCultureChanged(System.Globalization.CultureInfo culture) => _ = InvokeAsync(StateHasChanged);

        public void Dispose()
        {
            L.CultureChanged -= OnCultureChanged;
            MediaFolder.Changed -= OnMediaFolderChanged;
            Session.Changed -= OnSessionChanged;
            ActiveProject.Changed -= OnProjectChanged;
        }
}
