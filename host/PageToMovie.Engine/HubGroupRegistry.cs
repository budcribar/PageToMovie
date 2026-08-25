using System.Collections.Concurrent;

namespace PageToMovie.Engine;

/// <summary>
/// Counts live JobHub connections per user group, on THIS instance.
/// </summary>
/// <remarks>
/// A job publish goes to <c>user:{snapshot.UserId}</c> through the in-process IHubContext, and
/// SignalR is registered with no backplane. So a broadcast to a group this instance holds no
/// connection for reaches nobody, silently and successfully — no exception, no log, a job that
/// completes perfectly server-side and a browser that sees nothing. That is indistinguishable
/// from a healthy run unless someone counts, which is what this does.
///
/// Two ways to land there: the browser's socket is on a different instance (scale-out without a
/// backplane), or the group name the client joined does not match the UserId stamped on the job
/// record. The counter cannot tell those apart on its own, but it turns "no progress bar" into a
/// server-side fact, and <see cref="Describe"/> names the groups that DO exist so a mismatch is
/// visible on sight.
/// </remarks>
public sealed class HubGroupRegistry
{
    /// <summary>Enough to cover a session's worth of connects and finished jobs without letting a
    /// long-lived process grow unbounded.</summary>
    private const int MaxEvents = 300;

    private readonly ConcurrentDictionary<string, int> _byUser = new(StringComparer.Ordinal);
    private readonly Queue<HubDeliveryEvent> _events = new();
    private readonly object _eventGate = new();

    public void Add(string userId) =>
        _byUser.AddOrUpdate(userId, 1, (_, n) => n + 1);

    public void Remove(string userId) =>
        _byUser.AddOrUpdate(userId, 0, (_, n) => Math.Max(0, n - 1));

    public int Count(string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && _byUser.TryGetValue(userId, out var n) ? n : 0;

    /// <summary>
    /// Record something worth seeing later. These go into the server-log export zip rather than
    /// only to stdout: the export is what actually reaches whoever is debugging, and a diagnostic
    /// that only exists in container logs nobody pulls is not a diagnostic.
    /// </summary>
    public void Note(string kind, string detail)
    {
        lock (_eventGate)
        {
            _events.Enqueue(new HubDeliveryEvent(DateTimeOffset.UtcNow, kind, detail, Describe()));
            while (_events.Count > MaxEvents)
                _events.Dequeue();
        }
    }

    /// <summary>Newest last, for the log export.</summary>
    public IReadOnlyList<HubDeliveryEvent> RecentEvents()
    {
        lock (_eventGate)
            return _events.ToArray();
    }

    /// <summary>Groups with at least one live connection — deliberately ordinal, since SignalR
    /// group matching is ordinal too and a case difference is exactly the kind of mismatch worth
    /// seeing spelled out.</summary>
    public string Describe()
    {
        var live = _byUser.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}({kv.Value})").ToArray();
        return live.Length == 0 ? "<none>" : string.Join(", ", live);
    }
}

/// <summary>One hub connect / disconnect / undelivered-job entry for the server-log export.</summary>
/// <param name="LiveGroups">Group census at the moment it happened — an undelivered job next to a
/// census naming the same user under different casing is an id mismatch; next to a census naming
/// nobody it is a client that was simply not connected.</param>
public sealed record HubDeliveryEvent(
    DateTimeOffset At, string Kind, string Detail, string LiveGroups);
