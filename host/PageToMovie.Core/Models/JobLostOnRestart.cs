namespace PageToMovie.Core.Models;

/// <summary>
/// In-memory jobs vanish on API recycle (OOM / deploy). A client that still shows
/// queued/running must treat 404 / empty / a different current job as death — not
/// hang on the first "Queued batch gen" line.
/// </summary>
public static class JobLostOnRestart
{
    public const string Message = "Job was lost (server restarted). Nothing is generating.";

    public static bool IsInFlight(string? status) =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase);

    public static bool IsFinishedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;
        return status.Equals("done", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("partial", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("idle", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameJobId(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a current-job / job-by-id poll confirmed the local in-flight snapshot
    /// is gone (404), the list is empty, or the server's primary job is a different id.
    /// Network failures must pass <paramref name="byIdNotFound"/> / <paramref name="currentKnown"/>
    /// as false so a blip does not look like a restart.
    /// </summary>
    public static bool ShouldMarkLost(
        JobSnapshot local,
        JobSnapshot? byId,
        bool byIdNotFound,
        JobSnapshot? current,
        bool currentKnown)
    {
        if (!IsInFlight(local.Status) || string.IsNullOrWhiteSpace(local.JobId))
            return false;
        if (byId is not null && SameJobId(byId.JobId, local.JobId))
            return false;
        if (current is not null && SameJobId(current.JobId, local.JobId) && !IsFinishedStatus(current.Status))
            return false;
        if (byIdNotFound)
            return true;
        if (!currentKnown)
            return false;
        if (current is null)
            return true;
        return !SameJobId(current.JobId, local.JobId);
    }

    /// <summary>
    /// Replace a stale in-flight snapshot with the server view, or mark it lost.
    /// Mutates <paramref name="local"/> when marking lost so the UI keeps the same log.
    /// </summary>
    public static JobSnapshot ApplyServerView(
        JobSnapshot local,
        JobSnapshot? byId,
        bool byIdNotFound,
        JobSnapshot? current,
        bool currentKnown)
    {
        if (!IsInFlight(local.Status) || string.IsNullOrWhiteSpace(local.JobId))
            return local;
        if (byId is not null && SameJobId(byId.JobId, local.JobId))
            return byId;
        if (current is not null && SameJobId(current.JobId, local.JobId))
            return current;
        if (ShouldMarkLost(local, byId, byIdNotFound, current, currentKnown))
        {
            MarkProgressLost(local);
            return local;
        }
        return local;
    }

    public static void MarkProgressLost(JobProgress job)
    {
        job.Status = "error";
        job.Message = Message;
        job.Error = Message;
        job.FinishedAt = DateTimeOffset.UtcNow;
        if (job.Log.Count == 0 || !string.Equals(job.Log[^1], Message, StringComparison.Ordinal))
            job.Log.Add(Message);
    }

    /// <summary>
    /// Queued job whose only log line is the enqueue message — <c>ExecuteQueuedJobAsync</c>
    /// never reached lock-wait / worker-slot.
    /// </summary>
    public static bool IsStuckOnInitialEnqueue(JobProgress job, string initialMessage)
    {
        if (!string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(job.Message, initialMessage, StringComparison.Ordinal))
            return false;
        if (job.Log.Count == 0)
            return true;
        return job.Log.Count == 1 &&
               string.Equals(job.Log[0], initialMessage, StringComparison.Ordinal);
    }
}
