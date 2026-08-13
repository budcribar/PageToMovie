namespace PageToMovie.Core.Utils;

/// <summary>Short elapsed labels for live job cards ("18m 40s").</summary>
public static class ElapsedClock
{
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s";
        return $"{Math.Max(0, elapsed.Seconds)}s";
    }

    public static string FormatSince(DateTimeOffset started, DateTimeOffset? now = null)
        => Format((now ?? DateTimeOffset.UtcNow) - started);
}
