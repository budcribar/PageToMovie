using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Web.Components.Pages;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// After an API recycle the in-memory job is gone. Film must fail the stale
/// "Queued batch gen" snapshot so JobRunning drops and the modal can Close.
/// </summary>
public class JobLostOnRestartTests
{
    private static JobSnapshot QueuedBatch(string id = "batch-1") => new()
    {
        JobId = id,
        Status = "queued",
        Kind = "batch",
        Message = "Queued batch gen (2 clip(s))…",
        Log = new List<string> { "Queued batch gen (2 clip(s))…" },
        QueuedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Current_job_404_clears_queued_batch_and_stops_JobRunning()
    {
        var page = new Scenes();
        var gen = page.Gen;
        gen._job = QueuedBatch();
        gen._showJobModal = true;
        gen._myJobs = new List<JobSnapshot> { gen._job };

        Assert.True(gen.JobRunning);

        gen.ApplyServerJobView(byId: null, byIdNotFound: true, current: null, currentKnown: true);

        Assert.False(gen.JobRunning);
        Assert.Equal("error", gen._job!.Status);
        Assert.Equal(JobLostOnRestart.Message, gen._job.Error);
        Assert.Equal(JobLostOnRestart.Message, gen._job.Message);
        Assert.True(gen._showJobModal);
        Assert.False(JobLostOnRestart.IsInFlight(gen._myJobs[0].Status));
    }

    [Fact]
    public void Empty_current_job_clears_queued_batch()
    {
        var local = QueuedBatch();
        var next = JobLostOnRestart.ApplyServerView(
            local, byId: null, byIdNotFound: false, current: null, currentKnown: true);

        Assert.Equal("error", next.Status);
        Assert.Equal(JobLostOnRestart.Message, next.Error);
        Assert.False(JobLostOnRestart.IsInFlight(next.Status));
    }

    [Fact]
    public void Different_current_job_clears_queued_batch()
    {
        var local = QueuedBatch("old");
        var other = new JobSnapshot { JobId = "new", Status = "running", Kind = "scene" };

        var next = JobLostOnRestart.ApplyServerView(
            local, byId: null, byIdNotFound: true, current: other, currentKnown: true);

        Assert.Equal("error", next.Status);
        Assert.Equal("old", next.JobId);
        Assert.Equal(JobLostOnRestart.Message, next.Error);
    }

    [Fact]
    public void Network_blip_does_not_kill_in_flight_job()
    {
        var local = QueuedBatch();
        var next = JobLostOnRestart.ApplyServerView(
            local, byId: null, byIdNotFound: false, current: null, currentKnown: false);

        Assert.Equal("queued", next.Status);
        Assert.Equal("Queued batch gen (2 clip(s))…", next.Message);
    }

    [Fact]
    public void Live_by_id_keeps_queued_snapshot()
    {
        var local = QueuedBatch();
        var live = QueuedBatch();
        live.Message = "Waiting for resource lock…";

        var next = JobLostOnRestart.ApplyServerView(
            local, byId: live, byIdNotFound: false, current: live, currentKnown: true);

        Assert.Same(live, next);
        Assert.Equal("queued", next.Status);
        Assert.Equal("Waiting for resource lock…", next.Message);
    }

    [Fact]
    public void Hub_reconnect_clears_stale_queued_batch()
    {
        var page = new Scenes();
        var gen = page.Gen;
        gen._job = QueuedBatch();
        gen._showJobModal = true;
        Assert.True(gen.JobRunning);

        var hub = new JobHubClient(Options.Create(new EngineApiOptions()));
        hub.Reconnected += () => gen.ApplyServerJobView(
            byId: null, byIdNotFound: true, current: null, currentKnown: true);

        hub.RaiseReconnected();

        Assert.False(gen.JobRunning);
        Assert.Equal(JobLostOnRestart.Message, gen._job!.Error);
        Assert.True(gen._showJobModal);
    }

    [Fact]
    public async Task Health_recovery_same_as_reconnect()
    {
        var page = new Scenes();
        var gen = page.Gen;
        gen._job = QueuedBatch();
        var health = new ServerHealthState();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.ReportFailure("hub reconnecting");
        health.Recovered += () =>
        {
            gen.ApplyServerJobView(null, true, null, true);
            recovered.TrySetResult();
            return Task.CompletedTask;
        };

        health.ReportSuccess();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(gen.JobRunning);
        Assert.Equal(JobLostOnRestart.Message, gen._job!.Error);
    }

    [Fact]
    public void Stuck_on_initial_enqueue_is_zombie()
    {
        var rec = new JobRecord
        {
            Status = "queued",
            Message = "Queued batch gen (2 clip(s))…",
            Log = new List<string> { "Queued batch gen (2 clip(s))…" },
        };
        Assert.True(JobLostOnRestart.IsStuckOnInitialEnqueue(rec, rec.Message!));

        rec.Message = "Waiting for resource lock…";
        rec.Log.Add(rec.Message);
        Assert.False(JobLostOnRestart.IsStuckOnInitialEnqueue(rec, "Queued batch gen (2 clip(s))…"));
    }
}

public class OrphanJobLockTests
{
    [Fact]
    public void Lock_whose_job_is_gone_is_not_a_permanent_wait()
    {
        var locks = new InMemoryLockService();
        var jobs = new JobStore();
        var key = LockKeys.Scene("Demo", 1);

        Assert.True(locks.TryAcquire(key, "dead-user", TimeSpan.FromHours(2), "batch", jobId: "gone"));
        Assert.False(locks.TryAcquire(key, "u2", TimeSpan.FromMinutes(5), "batch", jobId: "new"));
        Assert.True(OrphanJobLock.HolderJobIsGone(locks.Get(key), jobs));
        Assert.True(OrphanJobLock.TryAcquireOrSteal(
            locks, jobs, key, "u2", TimeSpan.FromMinutes(5), "batch", "new"));

        var held = locks.Get(key);
        Assert.NotNull(held);
        Assert.Equal("u2", held!.UserId);
        Assert.Equal("new", held.JobId);
    }

    [Fact]
    public void Lock_whose_job_finished_is_stolen()
    {
        var locks = new InMemoryLockService();
        var jobs = new JobStore();
        var dead = jobs.Create(new JobRecord { Status = "error", Kind = "batch" });
        var key = LockKeys.Scene("Demo", 2);
        Assert.True(locks.TryAcquire(key, "u1", TimeSpan.FromHours(2), "gen", jobId: dead.JobId));

        Assert.True(OrphanJobLock.HolderJobIsGone(locks.Get(key), jobs));
        Assert.True(OrphanJobLock.TryAcquireOrSteal(
            locks, jobs, key, "u2", TimeSpan.FromMinutes(5), "gen", "alive"));
        Assert.Equal("u2", locks.Get(key)!.UserId);
    }

    [Fact]
    public void Live_queued_job_keeps_the_lock()
    {
        var locks = new InMemoryLockService();
        var jobs = new JobStore();
        var live = jobs.Create(new JobRecord { Status = "queued", Kind = "batch" });
        var key = LockKeys.Scene("Demo", 3);
        Assert.True(locks.TryAcquire(key, "u1", TimeSpan.FromHours(2), "gen", jobId: live.JobId));

        Assert.False(OrphanJobLock.HolderJobIsGone(locks.Get(key), jobs));
        Assert.False(OrphanJobLock.TryAcquireOrSteal(
            locks, jobs, key, "u2", TimeSpan.FromMinutes(5), "gen", "other"));
        Assert.Equal("u1", locks.Get(key)!.UserId);
    }
}
