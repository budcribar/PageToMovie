using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin
{
    /// <summary>UI toggles / lifecycle / logout domain for the Admin page.</summary>
    public sealed class AdminUi
    {
        private readonly Admin S;
        public AdminUi(Admin host) => S = host;

        internal bool _showTestEmailModal;
        internal bool _showJobsAndLocks = true;
        internal bool _showProjectArchiving = true;
        internal bool _showBrowserRendering = false;
        internal bool _showLoadSim = false;
        internal bool _showTimingTelemetry = false;
        internal bool _showGenErrors = false;
        internal bool _showStorageAndCapacity = false;
        /// <summary>Nested under Storage — daily volume history (default collapsed).</summary>
        internal bool _showDiskHistory = false;
        internal bool _started;

        internal void OpenTestEmailModal() => _showTestEmailModal = true;

        internal void CloseTestEmailModal() => _showTestEmailModal = false;

        internal void ToggleJobsAndLocks() => _showJobsAndLocks = !_showJobsAndLocks;
        internal void ToggleProjectArchiving() => _showProjectArchiving = !_showProjectArchiving;
        internal void ToggleLoadSim() => _showLoadSim = !_showLoadSim;
        internal void ToggleTimingTelemetry() => _showTimingTelemetry = !_showTimingTelemetry;
        internal void ToggleGenErrors() => _showGenErrors = !_showGenErrors;
        internal void ToggleStorageAndCapacity() => _showStorageAndCapacity = !_showStorageAndCapacity;
        internal void ToggleDiskHistory() => _showDiskHistory = !_showDiskHistory;

        internal void ExpandAllCards()
        {
            _showJobsAndLocks = true;
            _showProjectArchiving = true;
            _showBrowserRendering = true;
            _showLoadSim = true;
            _showTimingTelemetry = true;
            _showGenErrors = true;
            _showStorageAndCapacity = true;
            _showDiskHistory = true;
            // Nested class methods used as @onclick targets need an explicit re-render.
            _ = S.InvokeAsync(S.StateHasChanged);
        }

        internal void CollapseAllCards()
        {
            _showJobsAndLocks = false;
            _showProjectArchiving = false;
            _showBrowserRendering = false;
            _showLoadSim = false;
            _showTimingTelemetry = false;
            _showGenErrors = false;
            _showStorageAndCapacity = false;
            _showDiskHistory = false;
            _ = S.InvokeAsync(S.StateHasChanged);
        }

        internal void OnMediaFolderChanged() => S.InvokeAsync(S.StateHasChanged);

        internal async Task LogoutAsync()
        {
            await S.Api.LogoutAsync();
            S.Nav.NavigateTo("/admin/login");
        }

        internal async Task OnAfterRenderAsync(bool firstRender)
        {
            S.EnsureDomains();
            if (!firstRender || _started) return;
            _started = true;

            await S.Session.EnsureHydratedAsync();
            if (!S.Session.IsAdmin)
            {
                S.Nav.NavigateTo("/admin/login", forceLoad: true);
                return;
            }

            S.StateHasChanged();

            S.Hub.AdminState += S.State.OnAdminState;
            S.MediaFolder.Changed += OnMediaFolderChanged;
            // Explicit, not just relying on MainLayout's app-wide hook — makes the local-save pipeline
            // (auto-save-on-generate) definitely live before this page starts queuing gen jobs, and
            // surfaces its status/errors here (see OnMediaFolderChanged) instead of nowhere.
            await S.MediaFolder.EnsureHubHookAsync();
            // Do not block UI on SignalR
            _ = S.State.ConnectHubAsync();
            await S.State.RefreshAsync();
            S.StateHasChanged();

            S.State._pollCts = new CancellationTokenSource();
            // 3s so running/queued jobs update without manual Refresh.
            S.State._timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            _ = S.State.PollLoopAsync(S.State._pollCts.Token);
        }
    }
}
