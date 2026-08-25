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
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private HubConnection? _connection;
    private bool _disposed;
    /// <summary>Identity the live connection joined the server's per-user group with — see
    /// <see cref="OnSessionChanged"/> for why a stale one must force a reconnect.</summary>
    private (string UserId, bool HasToken)? _connectedIdentity;

    public event Action<JobSnapshot>? JobUpdated;
    public event Action<string>? JobLog;
    public event Action<object?>? AdminState;
    /// <summary>
    /// Hub finished a reconnect (or a test raises it). Pages that hold a local
    /// in-flight snapshot must re-fetch — the job store is in-memory and a
    /// restart drops it without a JobUpdated.
    /// </summary>
    public event Action? Reconnected;

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
        if (_session is not null)
            _session.Changed += OnSessionChanged;
    }

    private (string UserId, bool HasToken) CurrentIdentity() =>
        (_session?.UserId ?? "local", !string.IsNullOrWhiteSpace(_session?.Token));

    /// <summary>
    /// The server groups each connection into user:{userId} at connect time, and job events go
    /// only to that group. If the hub was started before the stored session hydrated (e.g. the
    /// media folder's silent auto-reconnect on app start), the socket joined as "local" and this
    /// user's JobUpdated events — including the ClientMediaUrl ticks that save each generated
    /// clip into the local media folder — never arrive, silently. Reconnect with the real
    /// identity as soon as the session reports it.
    /// </summary>
    private void OnSessionChanged()
    {
        if (_disposed || _connection is null || _connectedIdentity == CurrentIdentity())
            return;
        _ = RestartWithCurrentIdentityAsync();
    }

    private async Task RestartWithCurrentIdentityAsync()
    {
        try
        {
            if (_disposed)
                return;
            await _lifecycleGate.WaitAsync();
            try
            {
                if (_disposed || _connectedIdentity == CurrentIdentity())
                    return;
                await StopCoreAsync();
                await StartCoreAsync(CancellationToken.None);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch { /* optional — the next EnsureStartedAsync retries */ }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return;
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return;
            await StartCoreAsync(ct);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (_connection is { State: HubConnectionState.Connected or HubConnectionState.Connecting })
            return;

        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch { /* ignore */ }
            _connection = null;
        }

        var baseUrl = ResolveApiBase().TrimEnd('/');
        var identity = CurrentIdentity();
        var userId = identity.UserId;
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

        _connection.On<JobSnapshot>(JobHubEvents.JobUpdated, snap =>
        {
            // One line per delivery, on a filterable prefix. Whether events arrive AT ALL is the
            // first thing worth knowing when a job finishes server-side and the page never
            // notices, and it is not answerable from the server: a send to a group that has
            // members succeeds whether or not the browser ever processes it.
            Log($"JobUpdated {snap?.JobId} {snap?.Status} {snap?.Index}/{snap?.Total} " +
                $"media={(string.IsNullOrWhiteSpace(snap?.ClientMediaUrl) ? "no" : "yes")}");
            LastSocketUpdateAt = DateTimeOffset.UtcNow;
            JobUpdated?.Invoke(snap!);
        });
        _connection.On<string>(JobHubEvents.JobLog, line => JobLog?.Invoke(line));
        _connection.On<object>(JobHubEvents.AdminState, payload => AdminState?.Invoke(payload));
        // Hub lifecycle is a second outage signal (server restarts drop the socket before any
        // REST call notices). Reconnected proves the server is back; a Closed with no error is a
        // deliberate StopAsync, not an outage. Pages must re-fetch the current job on
        // Reconnected — ReportSuccess alone does not refresh a stale snapshot.
        _connection.Reconnecting += ex =>
        {
            Log($"reconnecting: {ex?.Message ?? "(no error)"}");
            _health?.ReportFailure(ex?.Message ?? "hub reconnecting");
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            Log("reconnected");
            _health?.ReportSuccess();
            RaiseReconnected();
            return Task.CompletedTask;
        };
        _connection.Closed += ex =>
        {
            Log($"closed: {ex?.Message ?? "(clean)"}");
            if (ex is not null)
                _health?.ReportFailure(ex);
            return Task.CompletedTask;
        };

        try
        {
            await _connection.StartAsync(ct);
            _connectedIdentity = identity;
            // The group this socket joined is half of every delivery question: the server sends to
            // user:{id} taken from the job record, and the two only match if this id does.
            Log($"connected as user:{userId} (token={(identity.HasToken ? "yes" : "no")}) -> {baseUrl}");
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
        // A connection made under a stale identity (see OnSessionChanged) counts as not started:
        // it's in the wrong per-user group and hears none of this user's job events.
        if (_disposed || (IsConnected && _connectedIdentity == CurrentIdentity())) return;
        try
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                if (_disposed || (IsConnected && _connectedIdentity == CurrentIdentity()))
                    return;
                if (_connection is not null)
                    await StopCoreAsync();
                await StartCoreAsync(CancellationToken.None);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch { /* optional */ }
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

    /// <summary>Browser-console trace on a filterable prefix. Deliberately plain Console.WriteLine
    /// rather than ILogger: this has to show up in devtools with no configuration, in a build the
    /// operator is already running, at the moment the fault happens.</summary>
    private static void Log(string message) => Console.WriteLine($"[hub] {message}");

    /// <summary>
    /// When the socket last delivered a JobUpdated. Null until one arrives.
    /// </summary>
    /// <remarks>
    /// Lets a caller tell a poll that merely re-read a healthy job from one that had to cover for a
    /// silent socket. Without it the two are indistinguishable — the poll runs on its own timer
    /// whether or not the hub is working, and every tick returns a fresh snapshot object.
    /// </remarks>
    public DateTimeOffset? LastSocketUpdateAt { get; private set; }

    /// <summary>Hub reconnect / health-recovery seam. Subscribers re-fetch the current job.</summary>
    public void RaiseReconnected() => Reconnected?.Invoke();

    /// <summary>
    /// Feed a snapshot fetched over REST through the same pipeline the socket uses, so a poll
    /// fallback reaches EVERY subscriber, not just the page that polled.
    /// </summary>
    /// <remarks>
    /// ClientMediaFolderService saves each generated clip to the user's folder on JobUpdated and
    /// nowhere else, while the API host deletes its own copy the moment it publishes
    /// ClientMediaUrl. So a run where the socket delivered nothing did not merely look stuck — the
    /// clips were generated, paid for, released server-side and never saved anywhere durable but
    /// the provider URL in the sidecar (Mary19 S02C02 takes 6-8, 2026-08-25). Correcting only the
    /// polling page would have left that hole open.
    /// </remarks>
    public void RaiseJobUpdated(JobSnapshot? snapshot)
    {
        if (snapshot is not null)
            JobUpdated?.Invoke(snapshot);
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_connection is null) return;
        await _connection.StopAsync();
        await _connection.DisposeAsync();
        _connection = null;
        _connectedIdentity = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            _session.Changed -= OnSessionChanged;
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopCoreAsync();
        }
        finally
        {
            // _lifecycleGate is deliberately NOT disposed. SemaphoreSlim.Dispose only frees the
            // lazily-created AvailableWaitHandle, which this class never touches — so it reclaims
            // nothing here, while a caller already parked in WaitAsync would get an
            // ObjectDisposedException instead of a clean no-op. The _disposed checks above are
            // what stop post-teardown work.
            _lifecycleGate.Release();
        }
    }
}
