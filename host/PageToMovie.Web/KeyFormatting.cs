namespace PageToMovie.Web;

/// <summary>
/// Display-name and URL helpers shared by the scene/cost/review pages
/// (each used to keep its own copy — kept here once so they can't drift apart).
/// </summary>
public static class KeyFormatting
{
    public static string ShortChar(string key) =>
        key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');

    public static string ShortLoc(string key)
    {
        if (string.IsNullOrEmpty(key)) return "—";
        return key
            .Replace("Location_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Loc_", "", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');
    }

    public static string ShortDelivery(string? key) => key switch
    {
        "spoken_on_camera" => "Spoken (on camera)",
        "voiceover_internal" => "Voiceover (internal)",
        "off_camera" => "Off camera",
        null or "" or "none" => "—",
        _ => key,
    };

    /// <summary>Appends a timestamp query param so the browser doesn't serve a stale cached video.</summary>
    public static string CacheBust(string url) =>
        CacheBust(url, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>Stable bust token so a reload can refresh the same media path (new plate bytes).</summary>
    public static string CacheBust(string url, long bust) =>
        url + (url.Contains('?') ? "&" : "?") + "v=" + bust;

    /// <summary>
    /// Rough ETA for a running batch/scene job from elapsed-time-per-completed-unit, extrapolated
    /// over the remaining units. Returns null until there's at least one completed unit to extrapolate from.
    /// </summary>
    public static string? EstimateTimeRemaining(DateTimeOffset? startedAt, int index, int total)
    {
        if (startedAt is not { } started || index <= 0 || total <= 0 || index >= total)
            return null;
        var elapsed = DateTimeOffset.UtcNow - started;
        if (elapsed <= TimeSpan.Zero)
            return null;
        var remaining = (elapsed / index) * (total - index);
        if (remaining.TotalSeconds < 1)
            return null;
        return remaining.TotalMinutes < 1
            ? $"~{Math.Max(1, (int)remaining.TotalSeconds)}s left"
            : $"~{(int)Math.Ceiling(remaining.TotalMinutes)}m left";
    }
}
