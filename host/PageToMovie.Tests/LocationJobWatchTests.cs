using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Locations watches plan_looks / location_variants until terminal — a Nick-and-Me-scale
/// looks run is 20–30 minutes, so a 3-minute poll cap is the normal failure mode.
/// </summary>
public class LocationJobWatchTests
{
    private const string Project = "nick-and-me";

    [Theory]
    [InlineData("plan_looks")]
    [InlineData("location_variants")]
    [InlineData("PLAN_LOOKS")]
    public void Tracks_looks_kinds_for_this_project(string kind)
    {
        var job = Running(kind, Project);
        Assert.True(LocationJobWatch.IsTrackedKind(kind));
        Assert.True(LocationJobWatch.IsTrackedForProject(job, Project));
        Assert.True(LocationJobWatch.ShouldWatch(job, Project));
        Assert.Equal(LocationJobWatch.Finish.StillRunning, LocationJobWatch.Classify(job, Project));
    }

    [Fact]
    public void Ignores_other_kinds_and_other_projects()
    {
        Assert.False(LocationJobWatch.IsTrackedKind("character"));
        Assert.False(LocationJobWatch.IsTrackedForProject(Running("plan_looks", "other"), Project));
        Assert.False(LocationJobWatch.ShouldWatch(Running("stage2", Project), Project));
        Assert.Equal(LocationJobWatch.Finish.Ignore, LocationJobWatch.Classify(Running("cast-extract", Project), Project));
        Assert.Equal(LocationJobWatch.Finish.Ignore, LocationJobWatch.Classify(null, Project));
    }

    [Theory]
    [InlineData("done", LocationJobWatch.Finish.ReloadSuccess)]
    [InlineData("partial", LocationJobWatch.Finish.ReloadSuccess)]
    [InlineData("error", LocationJobWatch.Finish.Failed)]
    [InlineData("cancelled", LocationJobWatch.Finish.Cancelled)]
    public void Terminal_statuses_stop_the_watch(string status, LocationJobWatch.Finish expected)
    {
        var job = new JobSnapshot
        {
            Status = status,
            Kind = "plan_looks",
            ProjectId = Project,
            Message = "16 locations locked.",
            Error = status == "error" ? "failed" : null,
        };
        Assert.False(LocationJobWatch.ShouldWatch(job, Project));
        Assert.Equal(expected, LocationJobWatch.Classify(job, Project));
        Assert.False(LocationJobWatch.ShouldContinuePoll(job, Project, expectingJob: false, cancelled: false));
    }

    [Fact]
    public void Backup_poll_has_no_tick_budget_for_a_30_minute_looks_run()
    {
        var job = Running("plan_looks", Project);
        // 20–30 minutes at 1.5s is 800–1200 ticks. The old loop died at 120 (~3 min).
        for (var tick = 0; tick < 1_200; tick++)
        {
            Assert.True(
                LocationJobWatch.ShouldContinuePoll(job, Project, expectingJob: false, cancelled: false),
                $"poll must still be watching at tick {tick} (job still running)");
        }

        Assert.True(LocationJobWatch.BackupPollInterval > TimeSpan.Zero);
        Assert.True(1_200 * LocationJobWatch.BackupPollInterval > TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Poll_continues_until_first_snapshot_when_expecting_a_started_job()
    {
        Assert.True(LocationJobWatch.ShouldContinuePoll(
            job: null, Project, expectingJob: true, cancelled: false));
        Assert.False(LocationJobWatch.ShouldContinuePoll(
            job: null, Project, expectingJob: false, cancelled: false));
    }

    [Fact]
    public void Leaving_the_page_cancels_the_watch()
    {
        var job = Running("plan_looks", Project);
        Assert.False(LocationJobWatch.ShouldContinuePoll(job, Project, expectingJob: true, cancelled: true));
    }

    [Fact]
    public void Success_banner_is_applied_after_reload_so_LoadAsync_cannot_wipe_it()
    {
        var job = new JobSnapshot
        {
            Status = "done",
            Kind = "plan_looks",
            ProjectId = Project,
            Message = "16 locations locked.",
        };
        var pending = LocationJobWatch.SuccessBanner(job);
        string? wipedByOldLoad = null;
        Assert.Null(wipedByOldLoad);
        Assert.Equal("16 locations locked.", LocationJobWatch.BannerAfterReload(pending, wipedByOldLoad));
        Assert.Equal("Set plates ready.", LocationJobWatch.SuccessBanner(new JobSnapshot { Status = "done" }));
    }

    [Fact]
    public void Locations_page_uses_hub_finish_and_an_unbounded_poll()
    {
        var src = ReadLocationsCodeBehind();
        Assert.DoesNotContain("i < 120", src, StringComparison.Ordinal);
        Assert.DoesNotContain("i < 240", src, StringComparison.Ordinal);
        Assert.Contains("OnJobUpdated", src, StringComparison.Ordinal);
        Assert.Contains("ShouldContinuePoll(", src, StringComparison.Ordinal);
        Assert.Contains("while (LocationJobWatch.ShouldContinuePoll", src, StringComparison.Ordinal);
        Assert.Contains("Hub.JobUpdated += OnJobUpdated", src, StringComparison.Ordinal);
        Assert.Contains("Hub.RaiseJobUpdated(", src, StringComparison.Ordinal);
        Assert.Contains("clearOperatorCopy: false", src, StringComparison.Ordinal);
        Assert.Contains("StopJobPoll()", src, StringComparison.Ordinal);
    }

    private static JobSnapshot Running(string kind, string projectId) =>
        new()
        {
            Status = "running",
            Kind = kind,
            ProjectId = projectId,
            JobId = "job-looks",
        };

    private static string ReadLocationsCodeBehind()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", "Locations.razor.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            d = d.Parent;
        }

        throw new FileNotFoundException("Locations.razor.cs");
    }
}
