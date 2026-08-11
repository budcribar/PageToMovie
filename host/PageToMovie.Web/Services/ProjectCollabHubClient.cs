using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace PageToMovie.Web.Services;

/// <summary>
/// I11–I12: SignalR client for project presence + PlanDirty estimate refresh.
/// Soft-fail — collab is optional for solo use.
/// </summary>
public sealed class ProjectCollabHubClient : IAsyncDisposable
{
    private readonly EngineApiOptions _opts;
    private readonly AdminSessionService? _session;
    private readonly NavigationManager? _nav;
    private HubConnection? _connection;
    private string? _joinedProjectId;

    public event Action<string /*projectId*/, long /*rev*/, string? /*byUser*/>? PlanDirty;
    public event Action<string /*projectId*/>? PresenceChanged;
    public event Action<string /*projectId*/, string /*resource*/, string? /*holder*/>? LeaseChanged;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public ProjectCollabHubClient(
        IOptions<EngineApiOptions> opts,
        AdminSessionService? session = null,
        NavigationManager? nav = null)
    {
        _opts = opts.Value;
        _session = session;
        _nav = nav;
    }

    public async Task EnsureJoinedAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        try
        {
            await EnsureStartedAsync(ct).ConfigureAwait(false);
            if (_connection is null) return;
            if (string.Equals(_joinedProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                && IsConnected)
                return;
            if (!string.IsNullOrWhiteSpace(_joinedProjectId)
                && !string.Equals(_joinedProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                try { await _connection.InvokeAsync("LeaveProject", _joinedProjectId, ct).ConfigureAwait(false); }
                catch { /* soft */ }
            }
            await _connection.InvokeAsync("JoinProject", projectId, ct).ConfigureAwait(false);
            _joinedProjectId = projectId;
        }
        catch
        {
            /* optional collab path */
        }
    }

    public async Task LeaveAsync(CancellationToken ct = default)
    {
        if (_connection is null || string.IsNullOrWhiteSpace(_joinedProjectId)) return;
        try
        {
            await _connection.InvokeAsync("LeaveProject", _joinedProjectId, ct).ConfigureAwait(false);
        }
        catch { /* soft */ }
        _joinedProjectId = null;
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_connection is { State: HubConnectionState.Connected or HubConnectionState.Connecting })
            return;

        if (_connection is not null)
        {
            try { await _connection.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _connection = null;
        }

        var baseUrl = ResolveApiBase().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        var userId = _session?.UserId ?? "local";
        var url = $"{baseUrl}/hubs/project?userId={Uri.EscapeDataString(userId)}";

        _connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                if (!string.IsNullOrWhiteSpace(_session?.Token))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(_session!.Token);
                options.Headers["X-User-Id"] = userId;
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string, long, string?>("PlanDirty", (pid, rev, by) =>
            PlanDirty?.Invoke(pid, rev, by));
        _connection.On<string>("PresenceChanged", pid => PresenceChanged?.Invoke(pid));
        _connection.On<string, string, string?>("LeaseChanged", (pid, res, holder) =>
            LeaseChanged?.Invoke(pid, res, holder));

        await _connection.StartAsync(ct).ConfigureAwait(false);
    }

    private string ResolveApiBase()
    {
        if (!string.IsNullOrWhiteSpace(_opts.BaseUrl))
            return _opts.BaseUrl.Trim().TrimEnd('/');
        if (_nav is not null)
            return _nav.BaseUri.TrimEnd('/');
        return "";
    }

    public async ValueTask DisposeAsync()
    {
        try { await LeaveAsync().ConfigureAwait(false); } catch { /* */ }
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync().ConfigureAwait(false); } catch { /* */ }
            _connection = null;
        }
    }
}
