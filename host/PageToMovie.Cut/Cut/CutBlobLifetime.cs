namespace PageToMovie.Cut.Cut;

/// <summary>
/// When a blob URL may be revoked. JS <c>cut.js</c> uses the same rules.
/// Concat / JIT / hop-slice inputs stay until ffmpeg is done with them.
/// </summary>
public static class CutBlobLifetime
{
    public static bool IsBlobUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.StartsWith("blob:", StringComparison.Ordinal);

    /// <summary>
    /// Revoke only owned temps that are not in the current compose/JIT
    /// input list and not pinned as the live preview or prefix.
    /// Source take URLs are never owned temps — do not revoke those here.
    /// </summary>
    public static bool CanRevoke(
        string? url,
        IReadOnlyCollection<string> ownedTemps,
        IReadOnlyCollection<string> inUse,
        IReadOnlyCollection<string> pinned)
    {
        if (!IsBlobUrl(url))
            return false;
        if (inUse.Contains(url!))
            return false;
        if (pinned.Contains(url!))
            return false;
        return ownedTemps.Contains(url!);
    }

    public static IReadOnlyList<string> Revocable(
        IEnumerable<string?> ownedTemps,
        IEnumerable<string?> inUse,
        IEnumerable<string?> pinned)
    {
        var owned = ToSet(ownedTemps);
        var busy = ToSet(inUse);
        var keep = ToSet(pinned);
        return owned.Where(u => CanRevoke(u, owned, busy, keep)).ToList();
    }

    private static HashSet<string> ToSet(IEnumerable<string?> urls)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in urls)
        {
            if (!string.IsNullOrWhiteSpace(url))
                set.Add(url);
        }

        return set;
    }
}
