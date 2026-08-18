using PageToMovie.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace PageToMovie.Web.Services;

public sealed class JobHubClient : IAsyncDisposable
{
    private readonly EngineApiOptions _opts;
    private readonly AdminSessionService? _session;
    private readonly NavigationManager? _nav;
    private readonly ServerHealthState? _health;
    private HubConnection? _connection;

    public event Action<JobSnapshot>? JobUpdated;
    public event Action<string>? JobLog;
    public event Action<object?>? AdminState;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public JobHubClient(
        IOptions<EngineApiOptions> opts,
        AdminSessionService? session = null,
        NavigationManager? nav = null,
        ServerHealthState? health = null)
    {
        _opts = opts.Value;
        _session = session;
        _nav = nav;
        _health = health;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection is { State: HubConnectionState.Connected or HubConnectionState.Connecting })
            return;

        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch { /* ignore */ }
            _connection = null;
        }

        var baseUrl = ResolveApiBase().TrimEnd('/');
        var userId = _session?.UserId ?? "local";
        var url = $"{baseUrl}/hubs/jobs?userId={Uri.EscapeDataString(userId)}";

        _connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                if (!string.IsNullOrWhiteSpace(_session?.Token))
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(_session.Token);
                }
                options.Headers[AuthHeaderUserId] = userId;
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JobSnapshot>(JobHubEvents.JobUpdated, snap => JobUpdated?.Invoke(snap));
        _connection.On<string>(JobHubEvents.JobLog, line => JobLog?.Invoke(line));
        _connection.On<object>(JobHubEvents.AdminState, payload => AdminState?.Invoke(payload));
        // Hub lifecycle is a second outage signal (server restarts drop the socket before any
        // REST call notices). Reconnected proves the server is back; a Closed with no error is a
        // deliberate StopAsync, not an outage.
        if (_health is not null)
        {
            var health = _health;
            _connection.Reconnecting += ex => { health.ReportFailure(ex?.Message ?? "hub reconnecting"); return Task.CompletedTask; };
            _connection.Reconnected += _ => { health.ReportSuccess(); return Task.CompletedTask; };
            _connection.Closed += ex =>
            {
                if (ex is not null)
                {
                    health.ReportFailure(ex);
                }
                return Task.CompletedTask;
            };
        }

        try
        {
            await _connection.StartAsync(ct);
        }
        catch (Exception ex) when (_health is not null && ServerHealthState.IsOutageException(ex, ct))
        {
            _health.ReportFailure(ex);
            throw;
        }
    }

    /// <summary>Best-effort connect — SignalR is optional for browse-only pages, so failures are swallowed.</summary>
    public async Task EnsureStartedAsync()
    {
        if (IsConnected) return;
        try { await StartAsync(); } catch { /* optional */ }
    }

    private string ResolveApiBase()
    {
        if (!string.IsNullOrWhiteSpace(_opts.BaseUrl))
            return _opts.BaseUrl.Trim().TrimEnd('/');
        if (_nav is not null)
            return _nav.BaseUri.TrimEnd('/');
        return "";
    }

    private const string AuthHeaderUserId = "X-User-Id";

    public async Task StopAsync()
    {
        if (_connection is null) return;
        await _connection.StopAsync();
        await _connection.DisposeAsync();
        _connection = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
