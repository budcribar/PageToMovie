using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin : IAsyncDisposable
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private AdminJobs? _jobs;
    internal AdminJobs Jobs => _jobs ??= new AdminJobs(this);
    private AdminArchive? _archive;
    internal AdminArchive Archive => _archive ??= new AdminArchive(this);
    private AdminTelemetry? _telemetry;
    internal AdminTelemetry Telemetry => _telemetry ??= new AdminTelemetry(this);
    private AdminState? _stateDomain;
    internal AdminState State => _stateDomain ??= new AdminState(this);
    private AdminUi? _ui;
    internal AdminUi Ui => _ui ??= new AdminUi(this);

    internal void EnsureDomains()
    {
        _ = Jobs; _ = Archive; _ = Telemetry; _ = State; _ = Ui;
    }

    /// <summary>Child sections cannot call protected StateHasChanged on Admin — use this.</summary>
    public Task NotifyChangedAsync() => InvokeAsync(StateHasChanged);

    // Shell-owned shared status
    internal string? _error;
    internal string? _actionMsg;
    internal bool _busy;

    internal static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "—";
        return id.Length <= 10 ? id : id[..8] + "…";
    }

    protected override Task OnAfterRenderAsync(bool firstRender) => Ui.OnAfterRenderAsync(firstRender);

    public ValueTask DisposeAsync()
    {
        if (_stateDomain is not null)
            Hub.AdminState -= _stateDomain.OnAdminState;
        if (_ui is not null)
            MediaFolder.Changed -= _ui.OnMediaFolderChanged;
        _stateDomain?.DisposePolling();
        // Do NOT Hub.DisposeAsync() here — JobHubClient is a shared, app-wide singleton (every
        // page's SignalR subscriptions, including ClientMediaFolderService's auto-save-on-
        // generate hook, ride the same connection). Disposing it on navigating away from /admin
        // killed that connection for the rest of the session: ClientMediaFolderService.
        // EnsureHubHookAsync() latches "_hubHooked" true on first call and never retries, so once
        // the underlying connection was torn down here it never came back — every job's generated
        // media (music, clips, anything) would stop reaching the local media folder app-wide,
        // silently, until a full page reload. This page merely stops listening to it.
        return ValueTask.CompletedTask;
    }
}
