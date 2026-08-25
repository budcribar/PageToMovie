using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A finished job must reach the hub exactly once. FinishAsync used to flip the snapshot to its
/// terminal status (one publish) and then call AppendLogAsync for the closing message — a second,
/// identical publish 0.3ms later, recorded as two separate undelivered hub entries for job
/// 1ef66bf3e218 (kind=batch, status=done). SignalRJobProgressSink deliberately does not throttle
/// terminal updates, so the duplicate reached every client and only ClientMediaFolderService's
/// per-path save dedupe absorbed it. The fix folds the closing log line into the same snapshot
/// mutation as the status flip.
/// </summary>
public class JobTerminalPublishTests
{
    private static string FinishAsyncBody()
    {
        var src = EngineSourceLocator.ReadEngineSource("FilmJobService.cs");
        var start = src.IndexOf(
            "private async Task FinishAsync(string status, string message, string? error = null)",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "FinishAsync not found in FilmJobService.cs");

        // Body ends where the next class-level member starts.
        var end = src.IndexOf("\n    private static string? StageEndAutoGitMessage", start, StringComparison.Ordinal);
        Assert.True(end > start, "End of FinishAsync not found in FilmJobService.cs");

        // Comments here name the very calls being asserted on — match code only.
        return CommonRegex.Replace(src[start..end], @"(?m)^\s*//.*$", "");
    }

    [Fact]
    public void FinishAsync_mutates_the_terminal_snapshot_once()
    {
        var body = FinishAsyncBody();

        // Every UpdateAsync publishes to the sink, so one terminal snapshot = one UpdateAsync…
        Assert.Single(CommonRegex.Matches(body, @"\bUpdateAsync\("));
        // …and AppendLogAsync routes through UpdateAsync, which is what published twice.
        Assert.DoesNotContain("AppendLogAsync(", body);
    }

    [Fact]
    public void AppendLogLine_carries_status_message_and_log_in_one_mutation()
    {
        var snap = new JobSnapshot { Status = "running", Total = 4, Index = 2 };

        // What FinishAsync's single UpdateAsync now does.
        snap.Status = "done";
        snap.Index = snap.Total;
        FilmJobService.AppendLogLine(snap, "Batch complete — 3 clip(s)");

        Assert.Equal("done", snap.Status);
        Assert.Equal(4, snap.Index);
        Assert.Equal("Batch complete — 3 clip(s)", snap.Message);
        Assert.Equal("Batch complete — 3 clip(s)", Assert.Single(snap.Log));
    }

    [Fact]
    public void AppendLogLine_does_not_repeat_a_line_the_job_already_logged()
    {
        // The terminal message is usually already the last log line (the stage logged it, then
        // finished with it) — that is why the two publishes were byte-identical.
        var snap = new JobSnapshot();
        FilmJobService.AppendLogLine(snap, "Rendered scene 2");
        FilmJobService.AppendLogLine(snap, "Rendered scene 2");

        Assert.Single(snap.Log);
    }

    [Fact]
    public void AppendLogLine_keeps_the_log_capped_at_the_last_120_lines()
    {
        var snap = new JobSnapshot();
        for (var i = 0; i < 130; i++)
            FilmJobService.AppendLogLine(snap, $"line {i}");

        Assert.Equal(120, snap.Log.Count);
        Assert.Equal("line 10", snap.Log[0]);
        Assert.Equal("line 129", snap.Log[^1]);
    }
}
