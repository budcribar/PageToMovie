using System.Net;
using System.Reflection;
using System.Text;
using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Job-by-id vs current-job list lookups must expose reachability so Film
/// reconcile can tell a deploy 404 from a network blip.
/// </summary>
public class EngineApiClientJobLookupTests
{
    [Fact]
    public async Task LookupJobAsync_404_is_not_found()
    {
        var engine = NewClient(_ => Json(HttpStatusCode.NotFound, "{}"));
        var result = await engine.LookupJobAsync("batch-1");
        Assert.Equal(JobLookupStatus.NotFound, result.Status);
        Assert.Null(result.Job);
    }

    [Fact]
    public async Task LookupJobAsync_502_is_unreachable()
    {
        var engine = NewClient(_ => Json(HttpStatusCode.BadGateway, "{}"));
        var result = await engine.LookupJobAsync("batch-1");
        Assert.Equal(JobLookupStatus.Unreachable, result.Status);
        Assert.Null(result.Job);
    }

    [Fact]
    public async Task LookupCurrentJobAsync_2xx_empty_is_found_with_no_job()
    {
        var engine = NewClient(_ => Json(HttpStatusCode.OK, """{ "ok": true, "running": false, "jobs": [], "count": 0 }"""));
        var result = await engine.LookupCurrentJobAsync();
        Assert.Equal(JobLookupStatus.Found, result.Status);
        Assert.Null(result.Job);
    }

    [Fact]
    public async Task LookupCurrentJobAsync_2xx_returns_primary_job()
    {
        var engine = NewClient(_ => Json(HttpStatusCode.OK,
            """{ "ok": true, "running": true, "jobs": [ { "jobId": "batch-1", "status": "queued", "kind": "batch" } ], "count": 1 }"""));
        var result = await engine.LookupCurrentJobAsync();
        Assert.Equal(JobLookupStatus.Found, result.Status);
        Assert.Equal("batch-1", result.Job?.JobId);
        Assert.Equal("queued", result.Job?.Status);
    }

    [Fact]
    public async Task LookupCurrentJobAsync_non_success_is_unreachable_not_not_found()
    {
        var engine = NewClient(_ => Json(HttpStatusCode.NotFound, "{}"));
        var result = await engine.LookupCurrentJobAsync();
        Assert.Equal(JobLookupStatus.Unreachable, result.Status);
        Assert.Null(result.Job);
    }

    [Fact]
    public async Task LookupCurrentJobAsync_transport_error_is_unreachable()
    {
        var engine = NewClient(_ => throw new HttpRequestException("blip"));
        var result = await engine.LookupCurrentJobAsync();
        Assert.Equal(JobLookupStatus.Unreachable, result.Status);
        Assert.Null(result.Job);
    }

    [Fact]
    public async Task Reconcile_blip_on_both_polls_keeps_queued_job()
    {
        var engine = NewClient(_ => Json(HttpStatusCode.BadGateway, "{}"));
        var gen = BindQueuedGen(engine);

        await gen.ReconcileJobWithServerAsync();

        Assert.Equal("queued", gen._job!.Status);
        Assert.True(gen.JobRunning);
        Assert.NotEqual(JobLostOnRestart.Message, gen._job.Error);
    }

    [Fact]
    public async Task Reconcile_by_id_404_marks_job_lost()
    {
        var engine = NewClient(req =>
            IsJobById(req)
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, """{ "ok": true, "running": false, "jobs": [], "count": 0 }"""));
        var gen = BindQueuedGen(engine);

        await gen.ReconcileJobWithServerAsync();

        Assert.Equal("error", gen._job!.Status);
        Assert.Equal(JobLostOnRestart.Message, gen._job.Error);
        Assert.False(gen.JobRunning);
    }

    [Fact]
    public async Task Reconcile_empty_current_marks_job_lost_when_by_id_unreachable()
    {
        var engine = NewClient(req =>
            IsJobById(req)
                ? Json(HttpStatusCode.BadGateway, "{}")
                : Json(HttpStatusCode.OK, """{ "ok": true, "running": false, "jobs": [], "count": 0 }"""));
        var gen = BindQueuedGen(engine);

        await gen.ReconcileJobWithServerAsync();

        Assert.Equal("error", gen._job!.Status);
        Assert.Equal(JobLostOnRestart.Message, gen._job.Error);
        Assert.False(gen.JobRunning);
    }

    [Fact]
    public async Task Reconcile_different_current_job_marks_local_lost()
    {
        var engine = NewClient(req =>
            IsJobById(req)
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK,
                    """{ "ok": true, "running": true, "jobs": [ { "jobId": "other", "status": "running", "kind": "scene" } ], "count": 1 }"""));
        var gen = BindQueuedGen(engine);

        await gen.ReconcileJobWithServerAsync();

        Assert.Equal("error", gen._job!.Status);
        Assert.Equal("batch-1", gen._job.JobId);
        Assert.Equal(JobLostOnRestart.Message, gen._job.Error);
    }

    [Fact]
    public async Task Reconcile_live_by_id_keeps_in_flight_job()
    {
        var engine = NewClient(req =>
            IsJobById(req)
                ? Json(HttpStatusCode.OK, """{ "ok": true, "job": { "jobId": "batch-1", "status": "queued", "kind": "batch", "message": "Waiting for resource lock…" } }""")
                : Json(HttpStatusCode.OK,
                    """{ "ok": true, "running": true, "jobs": [ { "jobId": "batch-1", "status": "queued", "kind": "batch" } ], "count": 1 }"""));
        var gen = BindQueuedGen(engine);

        await gen.ReconcileJobWithServerAsync();

        Assert.Equal("queued", gen._job!.Status);
        Assert.Equal("Waiting for resource lock…", gen._job.Message);
        Assert.True(gen.JobRunning);
    }

    private static Scenes.ScenesGeneration BindQueuedGen(EngineApiClient engine)
    {
        var page = new Scenes();
        BindEngine(page, engine);
        var gen = page.Gen;
        gen._job = new JobSnapshot
        {
            JobId = "batch-1",
            Status = "queued",
            Kind = "batch",
            Message = "Queued batch gen (2 clip(s))…",
            Log = new List<string> { "Queued batch gen (2 clip(s))…" },
        };
        gen._showJobModal = true;
        return gen;
    }

    private static void BindEngine(Scenes page, EngineApiClient engine)
    {
        var prop = typeof(Scenes).GetProperty("Engine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scenes.Engine inject property is missing.");
        prop.SetValue(page, engine);
    }

    private static bool IsJobById(HttpRequestMessage req)
    {
        var path = req.RequestUri?.AbsolutePath ?? "";
        return path.StartsWith("/api/jobs/", StringComparison.OrdinalIgnoreCase);
    }

    private static EngineApiClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var http = new HttpClient(new ReplyHandler(reply)) { BaseAddress = new Uri("http://localhost") };
        return new EngineApiClient(http);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ReplyHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_reply(request));
    }
}
