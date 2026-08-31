using System.Diagnostics.CodeAnalysis;

namespace PageToMovie.Core.Models;

/// <summary>
/// Locations page tracks <c>plan_looks</c> and <c>location_variants</c> for the open project.
/// A real-movie looks run is 20–30 minutes — there is no poll time budget. Watch until the
/// job is terminal or the operator leaves the page.
/// </summary>
public static class LocationJobWatch
{
    public const string PlanLooksKind = "plan_looks";
    public const string LocationVariantsKind = "location_variants";

    /// <summary>
    /// REST backstop while the hub is the primary finish signal. Interval only — not a cap.
    /// </summary>
    public static readonly TimeSpan BackupPollInterval = TimeSpan.FromMilliseconds(1500);

    public enum Finish
    {
        Ignore,
        StillRunning,
        ReloadSuccess,
        Failed,
        Cancelled,
    }

    public static bool IsTrackedKind(string? kind) =>
        string.Equals(kind, PlanLooksKind, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, LocationVariantsKind, StringComparison.OrdinalIgnoreCase);

    public static bool IsTrackedForProject([NotNullWhen(true)] JobSnapshot? job, string? projectId) =>
        job is not null
        && IsTrackedKind(job.Kind)
        && !string.IsNullOrWhiteSpace(projectId)
        && string.Equals(job.ProjectId, projectId, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldWatch(JobSnapshot? job, string? projectId) =>
        IsTrackedForProject(job, projectId) && JobLostOnRestart.IsInFlight(job.Status);

    /// <summary>
    /// Backup poll keeps going for the whole looks run (and the gap after we started a job
    /// but have not seen a snapshot yet). Only a terminal status or leaving the page stops it.
    /// </summary>
    public static bool ShouldContinuePoll(
        JobSnapshot? job,
        string? projectId,
        bool expectingJob,
        bool cancelled) =>
        !cancelled && (expectingJob || ShouldWatch(job, projectId));

    public static Finish Classify(JobSnapshot? job, string? projectId)
    {
        if (!IsTrackedForProject(job, projectId))
            return Finish.Ignore;
        if (JobLostOnRestart.IsInFlight(job.Status))
            return Finish.StillRunning;
        if (job.IsSuccess)
            return Finish.ReloadSuccess;
        if (string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            return Finish.Cancelled;
        if (string.Equals(job.Status, "error", StringComparison.OrdinalIgnoreCase))
            return Finish.Failed;
        return Finish.Ignore;
    }

    public static string SuccessBanner(JobSnapshot job) =>
        string.IsNullOrWhiteSpace(job.Message) ? "Set plates ready." : job.Message;

    /// <summary>
    /// <c>LoadAsync</c> used to null the banner at the start of a reload, wiping the
    /// just-set success/error line. Apply the pending banner after reload.
    /// </summary>
    public static string? BannerAfterReload(string? pendingBanner, string? loadMessage) =>
        !string.IsNullOrWhiteSpace(pendingBanner) ? pendingBanner : loadMessage;
}
