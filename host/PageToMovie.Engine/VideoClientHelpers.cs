using System.Diagnostics;
using System.Text.Json;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Shared poll-status helpers for the Grok/Gemini video clients. Each client still owns its
/// provider-specific done-payload parsing and telemetry.
/// </summary>
internal static class VideoClientHelpers
{
    public static (DateTime DeadlineUtc, int IntervalSeconds) PollWindow(PageToMovieOptions opts) =>
        (DateTime.UtcNow.AddSeconds(Math.Max(60, opts.GrokTimeoutSeconds)),
         Math.Max(2, opts.GrokPollSeconds));

    public static bool IsPollFailedOrExpired(string? status) =>
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase);

    public static string FormatPollProgress(string? status, string? progress) =>
        progress is null ? $"status={status}" : $"status={status} ({progress}%)";

    public static string PollErrorDetail(JsonElement root, string body) =>
        root.TryGetProperty("error", out var err) ? err.ToString() : body;

    public static VideoJobOutcome ExpiredOrFailed(string? status) =>
        string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase)
            ? VideoJobOutcome.Expired
            : VideoJobOutcome.ProviderFailed;

    public static async Task<string> ThrowTimedOutAsync(
        ProjectTelemetryService telemetry,
        string requestId,
        Stopwatch sw,
        int polls,
        int timeoutSeconds,
        string timeoutMessage,
        CancellationToken ct)
    {
        await telemetry.LogOutcomeAsync(
            null, requestId, VideoJobOutcome.TimedOut, sw.ElapsedMilliseconds, polls,
            ok: false, $"timed out after {timeoutSeconds}s", ct).ConfigureAwait(false);
        throw new TimeoutException(timeoutMessage);
    }
}
