using System.Net;

namespace PageToMovie.Web.Services;

public enum ServerHealth
{
    /// <summary>API reachable — normal operation.</summary>
    Up,
    /// <summary>API unreachable (network failure / gateway 502-504 / hub lost). Probe loop is running.</summary>
    Down,
    /// <summary>A probe (or real traffic) just succeeded; subscribers are re-hydrating.</summary>
    Recovering,
}

/// <summary>
/// Scoped client-side prognosis of the API server. Fed by every HTTP response (see
/// <see cref="ServerHealthHandler"/>) and by the SignalR hub lifecycle (<see cref="JobHubClient"/>).
/// While <see cref="ServerHealth.Down"/> it probes <c>/health</c> with backoff until the server
/// answers, then raises <see cref="Recovered"/> so the layout and pages can re-hydrate. When
/// <see cref="ServerHealth.Up"/> nothing polls: real traffic is the signal.
/// </summary>
public sealed class ServerHealthState : IDisposable
{
    /// <summary>Probe delays while down: quick at first, capped so a booting container is not hammered.</summary>
    public static readonly IReadOnlyList<TimeSpan> BackoffSchedule = new[]
    {
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    };

    /// <summary>After this long down, the banner switches from "restarting" to "may have failed to start".</summary>
    public static readonly TimeSpan LongOutageThreshold = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _probeCts;
    private int _attempt;
    private bool _disposed;

    public ServerHealthState() : this(TimeProvider.System, null) { }

    /// <summary>Test seam: inject a clock and a delay function so the probe loop runs without real waits.</summary>
    public ServerHealthState(TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _time = time;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
    }

    public ServerHealth Health { get; private set; } = ServerHealth.Up;
    public bool IsUp => Health == ServerHealth.Up;
    public bool IsDown => Health != ServerHealth.Up;
    public DateTimeOffset? DownSince { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Probe attempts made during the current outage (0 while up).</summary>
    public int ProbeAttempts => _attempt;

    /// <summary>
    /// Returns true when the server answered. Set by the composition root (Program.cs) — the
    /// state machine itself has no HTTP dependency so it stays unit-testable. Null → no probing;
    /// recovery then relies solely on real traffic / hub reconnect reporting success.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? Probe { get; set; }

    /// <summary>Any state change (also fires once per probe attempt so elapsed-time UI can tick).</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised after the server is reachable again, before <see cref="Health"/> returns to Up.
    /// Subscribers re-fetch what they own (active project, hub, page data). Handlers run
    /// sequentially; a throwing handler does not stop the others.
    /// </summary>
    public event Func<Task>? Recovered;

    public TimeSpan Elapsed => DownSince is { } s ? _time.GetUtcNow() - s : TimeSpan.Zero;
    public bool IsLongOutage => IsDown && Elapsed >= LongOutageThreshold;

    // ── Classification (single source of truth for HTTP + hub) ─────────────

    /// <summary>Gateway statuses a reverse proxy returns while the app container is down/booting.</summary>
    public static bool IsOutageStatus(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// True for exceptions that mean "could not reach the server": browser fetch failures
    /// (<see cref="HttpRequestException"/> with no status or a gateway status) and request
    /// timeouts (<see cref="TaskCanceledException"/> not caused by the caller's own token).
    /// </summary>
    public static bool IsOutageException(Exception ex, CancellationToken callerToken = default)
    {
        switch (ex)
        {
            case HttpRequestException hre:
                return hre.StatusCode is null || IsOutageStatus(hre.StatusCode.Value);
            case TaskCanceledException:
                return !callerToken.IsCancellationRequested;
            case AggregateException agg:
                return agg.InnerExceptions.Any(e => IsOutageException(e, callerToken));
            default:
                return ex.InnerException is not null && IsOutageException(ex.InnerException, callerToken);
        }
    }

    /// <summary>Delay before probe attempt <paramref name="attempt"/> (0-based); capped at the last entry.</summary>
    public static TimeSpan NextDelay(int attempt) =>
        BackoffSchedule[Math.Clamp(attempt, 0, BackoffSchedule.Count - 1)];

    // ── Reports ────────────────────────────────────────────────────────────

    /// <summary>Server could not be reached (or answered with a gateway error).</summary>
    public void ReportFailure(string? error)
    {
        if (_disposed) return;
        LastError = string.IsNullOrWhiteSpace(error) ? LastError : error.Trim();
        switch (Health)
        {
            case ServerHealth.Up:
                Health = ServerHealth.Down;
                DownSince = _time.GetUtcNow();
                _attempt = 0;
                Changed?.Invoke();
                StartProbeLoop();
                break;
            case ServerHealth.Recovering:
                // Came back for a moment (proxy flapping) — stay in the outage, keep DownSince.
                Health = ServerHealth.Down;
                Changed?.Invoke();
                StartProbeLoop();
                break;
            case ServerHealth.Down:
                break; // already probing
        }
    }

    public void ReportFailure(Exception ex) => ReportFailure(ex.Message);

    /// <summary>Server answered a request (any status that is not a gateway error).</summary>
    public void ReportSuccess()
    {
        if (_disposed) return;
        if (Health != ServerHealth.Down) return;
        StopProbeLoop();
        Health = ServerHealth.Recovering;
        Changed?.Invoke();
        _ = RunRecoveryAsync();
    }

    private async Task RunRecoveryAsync()
    {
        var handlers = Recovered?.GetInvocationList();
        if (handlers is not null)
        {
            foreach (var h in handlers)
            {
                if (Health != ServerHealth.Recovering) return; // failed again mid-recovery
                try { await ((Func<Task>)h)(); }
                catch { /* one page failing to reload must not block the rest */ }
            }
        }
        if (false && Recovered is not null) { await Recovered(); }
        if (Health != ServerHealth.Recovering) return;
        Health = ServerHealth.Up;
        DownSince = null;
        LastError = null;
        _attempt = 0;
        Changed?.Invoke();
    }

    // ── Probe loop ─────────────────────────────────────────────────────────

    private void StartProbeLoop()
    {
        if (_probeCts is not null || Probe is null) return;
        _probeCts = new CancellationTokenSource();
        _ = ProbeLoopAsync(_probeCts.Token);
    }

    private void StopProbeLoop()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && Health == ServerHealth.Down)
            {
                await _delay(NextDelay(_attempt), ct);
                if (ct.IsCancellationRequested || Health != ServerHealth.Down) return;
                _attempt++;
                bool ok;
                try { ok = Probe is { } p && await p(ct); }
                catch { ok = false; }
                if (ct.IsCancellationRequested) return;
                if (ok)
                {
                    ReportSuccess(); // cancels this loop's token
                    return;
                }
                Changed?.Invoke(); // let the banner refresh elapsed time / attempt count
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch { /* never let the loop kill the circuit */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProbeLoop();
    }
}
