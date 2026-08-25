using System.Collections.Concurrent;

namespace PageToMovie.Api.Hubs;

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
    private readonly ConcurrentDictionary<string, int> _byUser = new(StringComparer.Ordinal);

    public void Add(string userId) =>
        _byUser.AddOrUpdate(userId, 1, (_, n) => n + 1);

    public void Remove(string userId) =>
        _byUser.AddOrUpdate(userId, 0, (_, n) => Math.Max(0, n - 1));

    public int Count(string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && _byUser.TryGetValue(userId, out var n) ? n : 0;

    /// <summary>Groups with at least one live connection — deliberately ordinal, since SignalR
    /// group matching is ordinal too and a case difference is exactly the kind of mismatch worth
    /// seeing spelled out.</summary>
    public string Describe()
    {
        var live = _byUser.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}({kv.Value})").ToArray();
        return live.Length == 0 ? "<none>" : string.Join(", ", live);
    }
}
