using System.Collections.Concurrent;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>Global + per-user concurrency for API/Grok work (Phase C).</summary>
public sealed class ApiWorkerPool
{
    private readonly IOptions<PageToMovieOptions> _opts;
    private readonly IServerMetricsService? _metrics;
    private readonly object _semGate = new();
    private SemaphoreSlim _global;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perUser =
        new(StringComparer.OrdinalIgnoreCase);
    private int _configuredGlobal;
    private int _configuredPerUser;

    public ApiWorkerPool(IOptions<PageToMovieOptions> opts, IServerMetricsService? metrics = null)
    {
        _opts = opts;
        _metrics = metrics;
        var cap = opts.Value.Capacity ?? new CapacityOptions();
        _configuredGlobal = Math.Max(1, cap.MaxVideoInFlight);
        _configuredPerUser = Math.Max(1, cap.MaxVideoInFlightPerUser);
        _global = new SemaphoreSlim(_configuredGlobal, _configuredGlobal);
    }

    public int MaxGlobal
    {
        get
        {
            EnsureCaps();
            return _configuredGlobal;
        }
    }

    public int InFlight
    {
        get
        {
            try
            {
                return Math.Max(0, _configuredGlobal - _global.CurrentCount);
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Wait for a global + per-user slot, run <paramref name="work"/>, release.
    /// Fairness: SemaphoreSlim FIFO waiters + per-user cap (RR-ish under load).
    /// </summary>
    public async Task RunAsync(string userId, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        EnsureCaps();
        userId = string.IsNullOrWhiteSpace(userId) ? "local" : userId.Trim();
        // Capture instances under lock so a concurrent resize cannot dispose the sem we Wait on.
        SemaphoreSlim global;
        SemaphoreSlim userSem;
        lock (_semGate)
        {
            global = _global;
            userSem = GetUserSemaphoreUnlocked(userId);
        }

        await global.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await userSem.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { global.Release(); } catch (ObjectDisposedException) { /* resized */ }
            throw;
        }

        _metrics?.NoteApiSlotAcquired(userId);
        try
        {
            await work(ct).ConfigureAwait(false);
        }
        finally
        {
            try { userSem.Release(); } catch (ObjectDisposedException) { /* resized */ }
            try { global.Release(); } catch (ObjectDisposedException) { /* resized */ }
            _metrics?.NoteApiSlotReleased(userId);
        }
    }

    private SemaphoreSlim GetUserSemaphoreUnlocked(string userId)
    {
        return _perUser.GetOrAdd(userId, _ =>
        {
            var n = Math.Max(1, _configuredPerUser);
            return new SemaphoreSlim(n, n);
        });
    }

    private void EnsureCaps()
    {
        var cap = _opts.Value.Capacity ?? new CapacityOptions();
        var g = Math.Max(1, cap.MaxVideoInFlight);
        var p = Math.Max(1, cap.MaxVideoInFlightPerUser);
        if (g == _configuredGlobal && p == _configuredPerUser)
            return;

        lock (_semGate)
        {
            // Only replace when fully idle — disposing a SemaphoreSlim with waiters
            // faults in-flight RunAsync (ObjectDisposedException on Wait/Release).
            if (g != _configuredGlobal && _global.CurrentCount == _configuredGlobal)
            {
                var old = _global;
                _global = new SemaphoreSlim(g, g);
                _configuredGlobal = g;
                try { old.Dispose(); } catch { /* ignore */ }
            }
            // else: keep old cap; retry on next EnsureCaps when idle

            if (p != _configuredPerUser)
            {
                // Drop map entries only — do not dispose semaphores that may still be held
                // by in-flight work. New users get the new cap via GetOrAdd.
                _configuredPerUser = p;
                _perUser.Clear();
            }
        }
    }
}
