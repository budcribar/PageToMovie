using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PageToMovie.LoadSim;

// Top-level statements

var opts = SimOptions.Parse(args);
var exitCode = 2;

try
{
    exitCode = await RunAsync(opts);
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"FATAL: {ex}");
    exitCode = 2;
}

if (exitCode != 0)
    PauseIfInteractive("LoadSim exited with errors. Check messages above.");

return exitCode;

static Task<int> RunAsync(SimOptions opts) => LoadSimRun.ExecuteAsync(opts);

static void PauseIfInteractive(string message)
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        return;
    if (Console.IsInputRedirected)
        return;
    Console.Error.WriteLine(message);
    Console.WriteLine("Press Enter to close…");
    try { Console.ReadLine(); } catch { /* ignore */ }
}

namespace PageToMovie.LoadSim
{
file sealed class LoadSimRun
{
    public static async Task<int> ExecuteAsync(SimOptions opts)
    {
        await PrintBannerAsync(opts);

        var refused = await RefuseRealProjectIfNeeded(opts);
        if (refused.HasValue)
            return refused.Value;

        var sandbox = await PrepareSandboxOrSkip(opts);
        if (sandbox.HasValue)
            return sandbox.Value;

        using var http = CreateApiHttpClient(opts);

        var useFakes = await WaitForApiHealth(http, opts);
        if (useFakes is null)
            return 2;

        var fakes = await EnsureFakesIfRequired(opts, useFakes.Value);
        if (fakes.HasValue)
            return fakes.Value;

        var project = await EnsureProjectExistsAndActivate(http, opts);
        if (project.HasValue)
            return project.Value;

        await WarmupIfNeeded(opts);

        var session = VuSession.Start(opts);
        PrintVuStartMessage(opts, session.RunId);

        var tasks = StartVirtualUsers(session);

        var barrier = await AwaitReadyBarrier(session, tasks);
        if (barrier.ExitCode.HasValue)
            return barrier.ExitCode.Value;

        using var reportCts = CreateReportCts(session);
        var reportTask = ReportProgressLoopAsync(
            opts, session.Metrics, session.RunId, barrier.Started, reportCts.Token);

        Console.WriteLine("  running… (admin /admin shows live LoadSim charts)");
        await AwaitVirtualUsersAsync(tasks);
        await CancelReporterAsync(reportCts, reportTask);
        session.DisposeStressCts();

        return await PrintSummaryAndGates(opts, session, barrier.Started);
    }

    private static async Task PrintBannerAsync(SimOptions opts)
    {
        await Console.Out.WriteLineAsync($"PageToMovie.LoadSim → {opts.BaseUrl}");
        await Console.Out.WriteLineAsync($"  users={opts.Users} duration={opts.DurationSec}s scenario={opts.Scenario} project={opts.ProjectId}");
        await Console.Out.WriteLineAsync($"  cwd={Directory.GetCurrentDirectory()}");
        await Console.Out.WriteLineAsync($"  waitForApi={opts.WaitForApiSec}s");
    }

    private static async Task<int?> RefuseRealProjectIfNeeded(SimOptions opts)
    {
        if (!opts.AllowRealProject &&
            (string.Equals(opts.ProjectId, "Buster", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(opts.ProjectId, "NickAndMe", StringComparison.OrdinalIgnoreCase)))
        {
            await Console.Error.WriteLineAsync(
                $"Setup: refusing real project '{opts.ProjectId}'. " +
                $"Use '{ProjectSandbox.DefaultSandboxId}' (default) or pass --allowRealProject.");
            return 2;
        }

        return null;
    }

    private static async Task<int?> PrepareSandboxOrSkip(SimOptions opts)
    {
        if (!opts.PrepareSandbox)
        {
            Console.WriteLine($"  project={opts.ProjectId} (checked-in sandbox; no recopy)");
            return null;
        }

        try
        {
            var workspace = ProjectSandbox.FindWorkspaceRoot(opts.WorkspaceRoot);
            if (workspace is null)
            {
                await Console.Error.WriteLineAsync(
                    "Setup: could not find workspace root (folder with projects/). Pass --workspace PATH.");
                return 2;
            }

            opts.WorkspaceRoot = workspace;
            Console.WriteLine($"  workspace={workspace}");
            ProjectSandbox.Ensure(
                workspace,
                opts.SourceProjectId,
                opts.ProjectId,
                refresh: opts.RefreshSandbox);
            return null;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Setup: sandbox prepare failed: {ex.Message}");
            return 2;
        }
    }

    private static HttpClient CreateApiHttpClient(SimOptions opts) => new()
    {
        BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/'),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static async Task<bool?> WaitForApiHealth(HttpClient http, SimOptions opts)
    {
        Console.WriteLine($"  waiting for API {opts.BaseUrl}/health (up to {opts.WaitForApiSec}s)…");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, opts.WaitForApiSec));
        Exception? lastErr = null;
        var attempt = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            attempt++;
            var (ok, useFakes, err) = await TryHealthAttemptAsync(http, attempt);
            if (ok)
            {
                Console.WriteLine($"  health ok after {attempt} attempt(s) · useFakes={useFakes}");
                return useFakes;
            }

            lastErr = err;
            await Task.Delay(1000);
        }

        return await HealthPollFailedAsync(opts, lastErr);
    }

    private static async Task<(bool Ok, bool UseFakes, Exception? Err)> TryHealthAttemptAsync(
        HttpClient http, int attempt)
    {
        try
        {
            using var health = await http.GetAsync("health");
            if (health.IsSuccessStatusCode)
            {
                await using var stream = await health.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var useFakes = doc.RootElement.TryGetProperty("useFakes", out var uf) && uf.GetBoolean();
                return (true, useFakes, null);
            }

            Console.WriteLine($"  … attempt {attempt}: HTTP {(int)health.StatusCode}");
            return (false, false, new Exception($"/health returned {(int)health.StatusCode}"));
        }
        catch (Exception ex)
        {
            if (attempt == 1 || attempt % 5 == 0)
                Console.WriteLine($"  … attempt {attempt}: {ex.Message}");
            return (false, false, ex);
        }
    }

    private static async Task<bool?> HealthPollFailedAsync(SimOptions opts, Exception? lastErr)
    {
        await Console.Error.WriteLineAsync(
            $"Setup: API not reachable at {opts.BaseUrl} after {opts.WaitForApiSec}s. " +
            $"Last error: {lastErr?.Message ?? "unknown"}. " +
            "Start PageToMovie.Api first (profile 'http (fakes)'), or increase --waitForApiSec.");
        return null;
    }

    private static async Task<int?> EnsureFakesIfRequired(SimOptions opts, bool useFakes)
    {
        if (!RequiresFakesGuard(opts, useFakes))
            return null;

        if (GenWeightForScenario(opts) <= 0)
            return null;

        await Console.Error.WriteLineAsync(
            "Setup: API UseFakes=false but scenario includes gen. " +
            "Start Api with profile 'http (fakes)' or set PageToMovie_USE_FAKES=true.");
        return 2;
    }

    private static bool RequiresFakesGuard(SimOptions opts, bool useFakes) =>
        opts.RequireFakes && !useFakes && !opts.IKnowWhatImDoing &&
        opts.Scenario is not (LoadSimScenario.Browse or LoadSimScenario.Play);

    private static double GenWeightForScenario(SimOptions opts)
    {
        if (opts.Scenario == LoadSimScenario.Mixed)
            return opts.GenWeight;
        if (opts.Scenario == LoadSimScenario.Gen)
            return 1.0;
        return 0;
    }

    private static async Task<int?> EnsureProjectExistsAndActivate(HttpClient http, SimOptions opts)
    {
        try
        {
            using var projResp = await http.GetAsync("api/projects");
            projResp.EnsureSuccessStatusCode();
            await using var ps = await projResp.Content.ReadAsStreamAsync();
            using var pdoc = await JsonDocument.ParseAsync(ps);
            if (!ProjectListedByApi(pdoc.RootElement, opts.ProjectId))
            {
                await Console.Error.WriteLineAsync(
                    $"Setup: project '{opts.ProjectId}' not listed by API. " +
                    $"Ensure folder projects/{opts.ProjectId} exists under the API workspace. " +
                    "Restart Api after adding the project.");
                return 2;
            }

            using var act = await http.PostAsync(
                $"api/projects/{Uri.EscapeDataString(opts.ProjectId)}/activate",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Console.WriteLine(ActivateStatusMessage(opts.ProjectId, act.IsSuccessStatusCode, (int)act.StatusCode));
            return null;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Setup: project check failed: {ex.Message}");
            return 2;
        }
    }

    private static bool ProjectListedByApi(JsonElement root, string projectId)
    {
        if (!root.TryGetProperty("projects", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return false;

        return arr.EnumerateArray().Any(p =>
            string.Equals(ProjectIdOf(p), projectId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ProjectIdOf(JsonElement p) =>
        p.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

    private static string ActivateStatusMessage(string projectId, bool success, int statusCode) =>
        success
            ? $"  activated project {projectId}"
            : $"  warn: activate {projectId} → {statusCode}";

    private static async Task WarmupIfNeeded(SimOptions opts)
    {
        if (opts.WarmupSec <= 0)
            return;

        Console.WriteLine($"  warmup {opts.WarmupSec}s…");
        await Task.Delay(TimeSpan.FromSeconds(opts.WarmupSec));
    }

    private static void PrintVuStartMessage(SimOptions opts, string runId)
    {
        Console.WriteLine(
            opts.SkipReadyBarrier
                ? $"  starting {opts.Users} VUs (ready barrier skipped)… runId={runId}"
                : $"  starting {opts.Users} VUs — HTTP ready barrier (timeout {opts.ReadyTimeoutSec}s)… runId={runId}");
    }

    private static Task[] StartVirtualUsers(VuSession session)
    {
        var tasks = new Task[session.Opts.Users];
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = StartOneVirtualUser(i, session);
        return tasks;
    }

    private static Task StartOneVirtualUser(int i, VuSession session)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(session.Opts.BaseUrl.TrimEnd('/') + '/'),
            Timeout = TimeSpan.FromMinutes(2),
        };
        var vu = new VirtualUser(i, session.Opts, session.Metrics, client);
        return Task.Run(() => RunVirtualUserAsync(vu, client, session), CancellationToken.None);
    }

    private static async Task RunVirtualUserAsync(VirtualUser vu, HttpClient client, VuSession session)
    {
        try
        {
            if (!session.Opts.SkipReadyBarrier)
            {
                if (!await TryReadyAsync(vu, session))
                    return;
                await session.Go.Task;
            }

            var ct = session.StressToken();
            if (ct.IsCancellationRequested)
                return;
            await vu.RunStressAsync(ct);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task<bool> TryReadyAsync(VirtualUser vu, VuSession session)
    {
        try
        {
            using var readyCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(session.Opts.ReadyTimeoutSec));
            await vu.ReadyAsync(readyCts.Token);
            Interlocked.Increment(ref session.ReadyOk);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref session.ReadyFail);
            session.ReadyErrors.Add($"{vu.UserId}: {ex.Message}");
            // Still wait for go so we don't leave the host hanging; skip stress if failed
            try { await session.Go.Task; } catch { /* ignore */ }
            return false;
        }
    }

    private static async Task<ReadyBarrierOutcome> AwaitReadyBarrier(VuSession session, Task[] tasks)
    {
        if (session.Opts.SkipReadyBarrier)
            return StartStressClock(session);

        await WaitUntilReadyOrTimeout(session);
        return await CompleteReadyBarrierAsync(session, tasks);
    }

    private static async Task WaitUntilReadyOrTimeout(VuSession session)
    {
        var barrierDeadline = DateTimeOffset.UtcNow.AddSeconds(session.Opts.ReadyTimeoutSec);
        while (Volatile.Read(ref session.ReadyOk) + Volatile.Read(ref session.ReadyFail) < session.Opts.Users)
        {
            if (DateTimeOffset.UtcNow >= barrierDeadline)
                break;
            await Task.Delay(50);
        }
    }

    private static async Task<ReadyBarrierOutcome> CompleteReadyBarrierAsync(VuSession session, Task[] tasks)
    {
        var ok = Volatile.Read(ref session.ReadyOk);
        var fail = Volatile.Read(ref session.ReadyFail);
        var pending = session.Opts.Users - ok - fail;
        if (fail > 0 || pending > 0 || ok < session.Opts.Users)
            return await FailReadyBarrierAsync(session, tasks, ok, fail, pending);

        Console.WriteLine($"  ready: {ok}/{session.Opts.Users} VUs HTTP-ready — starting stress clock ({session.Opts.DurationSec}s)");
        return StartStressClock(session);
    }

    private static ReadyBarrierOutcome StartStressClock(VuSession session)
    {
        session.StressCts = new CancellationTokenSource(TimeSpan.FromSeconds(session.Opts.DurationSec));
        var started = DateTimeOffset.UtcNow;
        session.Go.TrySetResult();
        return ReadyBarrierOutcome.Ok(started);
    }

    private static async Task<ReadyBarrierOutcome> FailReadyBarrierAsync(
        VuSession session, Task[] tasks, int ok, int fail, int pending)
    {
        session.Go.TrySetResult(); // unblock any waiters
        await Console.Error.WriteLineAsync(
            $"Setup: ready barrier failed — ready={ok}/{session.Opts.Users} fail={fail} pending={pending} " +
            $"(timeout {session.Opts.ReadyTimeoutSec}s).");
        await WriteReadyErrorsAsync(session.ReadyErrors);
        // Cancel stress for anyone who might still run
        session.StressCts = new CancellationTokenSource();
        await session.StressCts.CancelAsync();
        try { await Task.WhenAll(tasks); } catch { /* ignore */ }
        return ReadyBarrierOutcome.Fail(2);
    }

    private static async Task WriteReadyErrorsAsync(System.Collections.Concurrent.ConcurrentBag<string> readyErrors)
    {
        foreach (var e in readyErrors.Take(10))
            await Console.Error.WriteLineAsync($"  {e}");
        if (readyErrors.Count > 10)
            await Console.Error.WriteLineAsync($"  … +{readyErrors.Count - 10} more");
    }

    private static CancellationTokenSource CreateReportCts(VuSession session) =>
        CancellationTokenSource.CreateLinkedTokenSource(session.StressToken());

    private static async Task AwaitVirtualUsersAsync(Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Run error: {ex.Message}");
        }
    }

    private static async Task CancelReporterAsync(CancellationTokenSource reportCts, Task reportTask)
    {
        await reportCts.CancelAsync();
        try { await reportTask; } catch { /* ignore */ }
    }

    private static async Task<int> PrintSummaryAndGates(SimOptions opts, VuSession session, DateTimeOffset started)
    {
        var elapsed = DateTimeOffset.UtcNow - started;
        var results = session.Metrics.Build(opts, elapsed);
        var passed = GateEvaluator.Evaluate(results, opts);

        // Final snapshot for admin
        await PostProgressAsync(opts, session.Metrics, session.RunId, started, "finished", passed);

        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var outPath = Path.GetFullPath(opts.OutPath);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(results, jsonOpts));

        WriteSummary(results, outPath, passed);

        if (results.Http.Total == 0)
        {
            await Console.Error.WriteLineAsync("WARN: zero actions recorded — VUs never completed requests.");
            return 2;
        }

        return passed ? 0 : 1;
    }

    private static void WriteSummary(LoadSimResults results, string outPath, bool passed)
    {
        Console.WriteLine();
        Console.WriteLine("=== LoadSim summary ===");
        Console.WriteLine($"  elapsed={results.ElapsedSec:0.0}s actions={results.Http.Total}");
        Console.WriteLine($"  errorRate={results.Http.ErrorRate:P2} (excl. 409={results.Http.Intentional409})");
        Console.WriteLine($"  latency p50={results.Http.P50Ms}ms p95={results.Http.P95Ms}ms browseP95={results.Http.BrowseP95Ms}ms");
        Console.WriteLine($"  jobs submitted={results.Jobs.Submitted} rejected={results.Jobs.Rejected} 5xx={results.Jobs.Server5xx}");
        Console.WriteLine($"  health ok={results.Health.Ok} fail={results.Health.Fail}");
        Console.WriteLine($"  peakApiInFlight={results.Server.PeakApiInFlight} cap={results.Server.ConfiguredMaxVideoInFlight}");
        WriteActionLatency(results);
        Console.WriteLine();
        Console.WriteLine("Gates:");
        foreach (var g in results.Gates)
            Console.WriteLine($"  {(g.Pass ? "PASS" : "FAIL")} {g.Name}: {g.Detail}");
        Console.WriteLine();
        Console.WriteLine($"Results → {outPath}");
        Console.WriteLine(passed ? "RESULT: PASS" : "RESULT: FAIL");
    }

    private static void WriteActionLatency(LoadSimResults results)
    {
        if (results.ActionLatency.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("Per-action latency (sorted by p95):");
        Console.WriteLine($"  {"action",-14} {"count",8} {"p50",8} {"p95",8} {"p99",8} {"errs",6}");
        foreach (var a in results.ActionLatency)
        {
            Console.WriteLine(
                $"  {a.Action,-14} {a.Count,8} {a.P50Ms,7}ms {a.P95Ms,7}ms {a.P99Ms,7}ms {a.Errors,6}");
        }
    }

    private static async Task ReportProgressLoopAsync(
        SimOptions opts,
        MetricsCollector metrics,
        string runId,
        DateTimeOffset started,
        CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
                await PostProgressAsync(opts, metrics, runId, started, "running", passed: null);
        }
        catch (OperationCanceledException) { /* done */ }
    }

    private static async Task PostProgressAsync(
        SimOptions opts,
        MetricsCollector metrics,
        string runId,
        DateTimeOffset started,
        string status,
        bool? passed)
    {
        try
        {
            var elapsed = DateTimeOffset.UtcNow - started;
            var snap = metrics.Snapshot(opts, elapsed);
            var dto = new PageToMovie.Core.Models.LoadSimProgressDto
            {
                RunId = runId,
                Status = status,
                Users = opts.Users,
                DurationSec = opts.DurationSec,
                ElapsedSec = elapsed.TotalSeconds,
                Scenario = opts.Scenario.ToString().ToLowerInvariant(),
                ProjectId = opts.ProjectId,
                BaseUrl = opts.BaseUrl,
                ActionsTotal = snap.ActionsTotal,
                ActionsPerSec = snap.ActionsPerSec,
                Errors = snap.Errors,
                ErrorRate = snap.ErrorRate,
                Intentional409 = snap.Intentional409,
                P50Ms = snap.P50Ms,
                P95Ms = snap.P95Ms,
                BrowseP50Ms = snap.BrowseP50Ms,
                BrowseP95Ms = snap.BrowseP95Ms,
                JobsSubmitted = snap.JobsSubmitted,
                JobsRejected = snap.JobsRejected,
                Jobs5xx = snap.Jobs5xx,
                HealthOk = snap.HealthOk,
                HealthFail = snap.HealthFail,
                PeakApiInFlight = snap.PeakApiInFlight,
                ConfiguredMaxVideoInFlight = snap.ConfiguredMaxVideoInFlight,
                ActionsByType = snap.ActionsByType,
                ActionLatency = snap.ActionLatency,
                Passed = passed,
                At = DateTimeOffset.UtcNow,
            };

            using var client = new HttpClient
            {
                BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/'),
                Timeout = TimeSpan.FromSeconds(5),
            };
            using var resp = await client.PostAsJsonAsync("api/loadsim/progress", dto);
            // ignore non-success — admin is best-effort
        }
        catch
        {
            // best-effort telemetry
        }
    }

    private readonly struct ReadyBarrierOutcome
    {
        public DateTimeOffset Started { get; }
        public int? ExitCode { get; }

        private ReadyBarrierOutcome(DateTimeOffset started, int? exitCode)
        {
            Started = started;
            ExitCode = exitCode;
        }

        public static ReadyBarrierOutcome Ok(DateTimeOffset started) => new(started, null);
        public static ReadyBarrierOutcome Fail(int code) => new(default, code);
    }

    private sealed class VuSession
    {
        public required SimOptions Opts { get; init; }
        public MetricsCollector Metrics { get; } = new();
        public string RunId { get; } = Guid.NewGuid().ToString("N")[..12];
        public TaskCompletionSource Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadyOk;
        public int ReadyFail;
        public System.Collections.Concurrent.ConcurrentBag<string> ReadyErrors { get; } = new();
        public CancellationTokenSource? StressCts;

        public static VuSession Start(SimOptions opts) => new() { Opts = opts };

        public CancellationToken StressToken() => StressCts?.Token ?? CancellationToken.None;

        public void DisposeStressCts() => StressCts?.Dispose();
    }
}
}
