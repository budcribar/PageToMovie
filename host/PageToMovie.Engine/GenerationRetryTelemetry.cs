namespace PageToMovie.Engine;

/// <summary>
/// Shared onRetry logging for every <see cref="AiRetryPolicy.ExecuteWithTransientRetryAsync{T}"/>
/// call site across the provider clients (chat/vision/image/video) — was hand-copied as a private
/// method in each client; centralized so there's one place that decides what a retry attempt
/// record looks like.
/// </summary>
public static class GenerationErrorLoggerExtensions
{
    /// <summary>Logs one row per failed-but-retried attempt via <see cref="GenerationErrorLogger"/>
    /// (best-effort, no-op if <paramref name="errorLogger"/> is null — every call site's DI wiring
    /// treats it as optional).</summary>
    public static async Task LogRetryAttemptAsync(
        this GenerationErrorLogger? errorLogger,
        string stage,
        string? model,
        string requestSummary,
        int attemptNum,
        Exception ex,
        CancellationToken ct)
    {
        if (errorLogger is null) return;
        var httpStatus = ex is ChatHttpStatusException hse ? hse.StatusCode : (int?)null;
        await errorLogger.LogAsync(new GenerationErrorRecord
        {
            Stage = stage,
            Model = model,
            ErrorType = httpStatus is not null ? "http_error" : "exception",
            ErrorMessage = ex.Message,
            HttpStatus = httpStatus,
            Attempt = attemptNum,
            Resolved = false,
            RequestSummary = requestSummary,
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Shared "video_job" outcome summary — one row per submit+poll job, logged once the job reaches a
/// terminal state (in <c>PollForVideoUrlAsync</c>, the only point that actually knows the outcome).
/// Kind is always <c>video_job</c>, distinct from the existing granular <c>video</c>/<c>video_extend</c>/
/// <c>video_poll</c> rows — this is the "did the job, as a whole, end up with a usable clip" signal for
/// the feedback loop, shared by GrokVideoClient and GeminiVideoClient rather than duplicated per client.
/// </summary>
public static class VideoJobTelemetry
{
    public static async Task LogOutcomeAsync(
        this ProjectTelemetryService telemetry,
        string? model,
        string requestId,
        VideoJobOutcome outcome,
        long durationMs,
        int polls,
        bool ok,
        string? error,
        CancellationToken ct,
        bool fakes = false)
    {
        try
        {
            await telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video_job",
                Mode = FormatOutcome(outcome),
                Model = model,
                RequestId = requestId,
                DurationMs = durationMs,
                Attempt = polls,
                Ok = ok,
                Error = error,
                Fakes = fakes,
            }, ct).ConfigureAwait(false);
        }
        catch { /* telemetry is best-effort */ }
    }

    public static string FormatOutcome(VideoJobOutcome outcome) => outcome switch
    {
        VideoJobOutcome.Ok => "ok",
        VideoJobOutcome.OkAfterRetry => "ok_after_retry",
        VideoJobOutcome.ProviderFailed => "provider_failed",
        VideoJobOutcome.Expired => "expired",
        VideoJobOutcome.TimedOut => "timed_out",
        VideoJobOutcome.PollFailed => "poll_failed",
        _ => outcome.ToString().ToLowerInvariant(),
    };
}

