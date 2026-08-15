using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// State machine + backoff contract for the client-side server prognosis
/// (<see cref="ServerHealthState"/>): Up → Down on the first outage report, probes with the
/// documented backoff while down, Down → Recovering → Up on success with <c>Recovered</c>
/// handlers awaited in between, and no probing at all while up.
/// </summary>
public class ServerHealthStateTests
{
    /// <summary>Manual clock + manual delay so the probe loop advances only when the test says so.</summary>
    private sealed class Harness
    {
        public readonly ManualTime Time = new();
        public readonly List<TimeSpan> Delays = new();
        public readonly Queue<bool> ProbeResults = new();
        public int ProbeCalls;
        public readonly ServerHealthState State;
        private TaskCompletionSource? _pendingDelay;

        public Harness()
        {
            State = new ServerHealthState(Time, (d, ct) =>
            {
                Delays.Add(d);
                _pendingDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => _pendingDelay.TrySetCanceled(ct));
                return _pendingDelay.Task;
            });
            State.Probe = _ =>
            {
                ProbeCalls++;
                return Task.FromResult(ProbeResults.Count > 0 && ProbeResults.Dequeue());
            };
        }

        /// <summary>Let the pending probe delay elapse and give the loop a chance to run the probe.</summary>
        public async Task ElapseAsync()
        {
            var d = _pendingDelay;
            Assert.NotNull(d);
            d!.TrySetResult();
            await Task.Yield();
            await Task.Delay(20);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Starts_up_with_no_outage()
    {
        var s = new ServerHealthState();
        Assert.Equal(ServerHealth.Up, s.Health);
        Assert.True(s.IsUp);
        Assert.Null(s.DownSince);
        Assert.Equal(TimeSpan.Zero, s.Elapsed);
        Assert.False(s.IsLongOutage);
    }

    [Fact]
    public void ReportFailure_transitions_to_down_and_stamps_down_since()
    {
        var h = new Harness();
        var changed = 0;
        h.State.Changed += () => changed++;

        h.State.ReportFailure("502 Bad Gateway");

        Assert.Equal(ServerHealth.Down, h.State.Health);
        Assert.Equal(h.Time.Now, h.State.DownSince);
        Assert.Equal("502 Bad Gateway", h.State.LastError);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Repeated_failures_keep_original_down_since()
    {
        var h = new Harness();
        h.State.ReportFailure("first");
        var since = h.State.DownSince;
        h.Time.Now += TimeSpan.FromSeconds(40);
        h.State.ReportFailure("second");
        Assert.Equal(since, h.State.DownSince);
        Assert.Equal("second", h.State.LastError);
        Assert.Equal(TimeSpan.FromSeconds(40), h.State.Elapsed);
    }

    [Fact]
    public void ReportSuccess_while_up_is_a_no_op()
    {
        var h = new Harness();
        var changed = 0;
        h.State.Changed += () => changed++;
        h.State.ReportSuccess();
        Assert.Equal(ServerHealth.Up, h.State.Health);
        Assert.Equal(0, changed);
        Assert.Empty(h.Delays); // nothing polls while up
    }

    [Fact]
    public async Task Success_runs_recovered_handlers_then_returns_to_up()
    {
        var h = new Harness();
        var order = new List<string>();
        h.State.Recovered += async () => { order.Add("a:" + h.State.Health); await Task.Yield(); };
        h.State.Recovered += () => throw new InvalidOperationException("boom"); // must not block others
        h.State.Recovered += () => { order.Add("c:" + h.State.Health); return Task.CompletedTask; };

        h.State.ReportFailure("net");
        h.State.ReportSuccess();
        await Task.Delay(50);

        Assert.Equal(new[] { "a:Recovering", "c:Recovering" }, order);
        Assert.Equal(ServerHealth.Up, h.State.Health);
        Assert.Null(h.State.DownSince);
        Assert.Null(h.State.LastError);
    }

    [Fact]
    public async Task Failure_during_recovery_returns_to_down_and_keeps_down_since()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Recovered += () => gate.Task;

        h.State.ReportFailure("net");
        var since = h.State.DownSince;
        h.State.ReportSuccess();
        Assert.Equal(ServerHealth.Recovering, h.State.Health);

        h.State.ReportFailure("502 again");
        Assert.Equal(ServerHealth.Down, h.State.Health);
        Assert.Equal(since, h.State.DownSince);

        gate.SetResult();
        await Task.Delay(30);
        Assert.Equal(ServerHealth.Down, h.State.Health); // the aborted recovery must not flip us Up
    }

    [Fact]
    public async Task Probe_loop_uses_backoff_schedule_and_recovers_on_first_ok()
    {
        var h = new Harness();
        h.ProbeResults.Enqueue(false);
        h.ProbeResults.Enqueue(false);
        h.ProbeResults.Enqueue(true);

        h.State.ReportFailure("net");
        Assert.Single(h.Delays);
        Assert.Equal(TimeSpan.FromSeconds(3), h.Delays[0]);

        await h.ElapseAsync(); // probe 1 → false
        Assert.Equal(1, h.ProbeCalls);
        Assert.Equal(TimeSpan.FromSeconds(5), h.Delays[1]);

        await h.ElapseAsync(); // probe 2 → false
        Assert.Equal(TimeSpan.FromSeconds(10), h.Delays[2]);

        await h.ElapseAsync(); // probe 3 → true
        Assert.Equal(3, h.ProbeCalls);
        Assert.Equal(ServerHealth.Up, h.State.Health);
        Assert.Equal(3, h.Delays.Count); // loop stopped — no further waits
    }

    [Fact]
    public void Backoff_caps_at_last_entry()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), ServerHealthState.NextDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(30), ServerHealthState.NextDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(30), ServerHealthState.NextDelay(99));
        Assert.Equal(TimeSpan.FromSeconds(3), ServerHealthState.NextDelay(-1));
    }

    [Fact]
    public async Task Real_traffic_success_cancels_the_probe_loop()
    {
        var h = new Harness();
        h.State.ReportFailure("net");
        Assert.Single(h.Delays);

        h.State.ReportSuccess(); // e.g. the presence heartbeat got through
        await Task.Delay(30);

        Assert.Equal(ServerHealth.Up, h.State.Health);
        Assert.Equal(0, h.ProbeCalls);
        Assert.Single(h.Delays);
    }

    [Fact]
    public void Long_outage_flag_flips_after_threshold()
    {
        var h = new Harness();
        h.State.ReportFailure("net");
        Assert.False(h.State.IsLongOutage);
        h.Time.Now += ServerHealthState.LongOutageThreshold;
        Assert.True(h.State.IsLongOutage);
    }

    [Fact]
    public void Without_probe_delegate_state_still_tracks_and_recovers_from_traffic()
    {
        var s = new ServerHealthState();
        s.Probe = null;
        s.ReportFailure("net");
        Assert.Equal(ServerHealth.Down, s.Health);
        s.ReportSuccess();
        Assert.NotEqual(ServerHealth.Down, s.Health);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void Outage_status_classification(HttpStatusCode status, bool outage)
        => Assert.Equal(outage, ServerHealthState.IsOutageStatus(status));

    [Fact]
    public void Outage_exception_classification()
    {
        Assert.True(ServerHealthState.IsOutageException(new HttpRequestException("TypeError: Failed to fetch")));
        Assert.True(ServerHealthState.IsOutageException(new HttpRequestException("gw", null, HttpStatusCode.BadGateway)));
        Assert.False(ServerHealthState.IsOutageException(new HttpRequestException("auth", null, HttpStatusCode.Unauthorized)));
        Assert.True(ServerHealthState.IsOutageException(new TaskCanceledException("client timeout")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(ServerHealthState.IsOutageException(new TaskCanceledException("caller cancelled"), cts.Token));
        Assert.False(ServerHealthState.IsOutageException(new InvalidOperationException("parse")));
        Assert.True(ServerHealthState.IsOutageException(new InvalidOperationException("wrap", new HttpRequestException("net"))));
    }
}
