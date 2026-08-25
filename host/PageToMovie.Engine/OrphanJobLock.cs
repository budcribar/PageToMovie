using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// A lock whose job id is missing or already finished must not block the next waiter
/// for the full TTL (hours). Steal it.
/// </summary>
public static class OrphanJobLock
{
    public static bool HolderJobIsGone(LockRecord? holder, IJobStore jobs)
    {
        if (holder is null || string.IsNullOrWhiteSpace(holder.JobId))
            return false;
        var job = jobs.Get(holder.JobId);
        return job is null || JobLostOnRestart.IsFinishedStatus(job.Status);
    }

    public static bool TryAcquireOrSteal(
        ILockService locks,
        IJobStore jobs,
        string resource,
        string userId,
        TimeSpan ttl,
        string? reason,
        string? jobId)
    {
        if (locks.TryAcquire(resource, userId, ttl, reason, jobId))
            return true;

        var holder = locks.Get(resource);
        if (holder is not null &&
            string.Equals(holder.UserId, userId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (holder is null || !HolderJobIsGone(holder, jobs))
            return false;

        locks.Release(resource, holder.UserId, force: true);
        return locks.TryAcquire(resource, userId, ttl, reason, jobId);
    }
}
