using System.Collections.Concurrent;

namespace PageToMovie.Engine;

/// <summary>
/// Grants exactly one client the right to write a given generated file into the shared media
/// folder.
/// </summary>
/// <remarks>
/// Every browser signed in as the same user joins the same hub group and receives the same
/// JobUpdated, and ClientMediaFolderService's save de-duplication (_savedKeys/_savingKeys) is a
/// plain in-memory HashSet — per browser, with nothing coordinating across them. So two open
/// windows both download the clip and both write the same path. The writer opens with
/// createWritable() and no keepExistingData, which truncates on open, so the second write either
/// fails on the file lock or truncates what the first one just finished. Files that end up
/// missing or under 1 KB are then skipped by the media folder's prefix resolver, which falls back
/// to the newest surviving take — which is why three distinct takes rendered as one video
/// (Mary19 S02C02 takes 6-8, 2026-08-25).
///
/// The claim is a short lease rather than a permanent record: a client can close its tab or lose
/// its folder handle mid-save, and a permanent claim would leave that file unwritable forever.
/// The winner releases on both success and failure, and the lease expires on its own if the
/// client vanishes without doing either.
/// </remarks>
public sealed class MediaSaveClaims
{
    /// <summary>Long enough to cover a download plus a folder write on a slow link, short enough
    /// that a client that died mid-save does not block a retry for long.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    private readonly record struct Lease(string ClientId, DateTimeOffset Expires);

    public MediaSaveClaims() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>Test seam — lets a lease expire without sleeping.</summary>
    public MediaSaveClaims(Func<DateTimeOffset> now) => _now = now;

    internal static string KeyFor(string projectId, string relativePath) =>
        $"{projectId.Trim()}|{relativePath.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant()}";

    /// <summary>
    /// True when <paramref name="clientId"/> may write the file. Re-asking with the same client id
    /// renews rather than refuses, so a client that retries its own save is never locked out by
    /// its own earlier attempt.
    /// </summary>
    public bool TryClaim(string projectId, string relativePath, string clientId)
    {
        if (string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(relativePath) ||
            string.IsNullOrWhiteSpace(clientId))
            return false;

        var key = KeyFor(projectId, relativePath);
        var now = _now();
        var mine = new Lease(clientId, now.Add(LeaseDuration));

        while (true)
        {
            if (_held.TryAdd(key, mine))
                return true;
            if (!_held.TryGetValue(key, out var held))
                continue; // released between the add and the read — go round again
            if (held.Expires > now && !string.Equals(held.ClientId, clientId, StringComparison.Ordinal))
                return false;
            if (_held.TryUpdate(key, mine, held))
                return true;
        }
    }

    /// <summary>Release after the save finishes or fails. Only the holder may release, so a
    /// straggler finishing late cannot free a lease a different client now holds.</summary>
    public void Release(string projectId, string relativePath, string clientId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(relativePath))
            return;
        var key = KeyFor(projectId, relativePath);
        if (_held.TryGetValue(key, out var held) &&
            string.Equals(held.ClientId, clientId, StringComparison.Ordinal))
        {
            _held.TryRemove(new KeyValuePair<string, Lease>(key, held));
        }
    }

    /// <summary>Live (unexpired) claim count — diagnostics only.</summary>
    public int ActiveCount
    {
        get
        {
            var now = _now();
            return _held.Count(kv => kv.Value.Expires > now);
        }
    }
}
