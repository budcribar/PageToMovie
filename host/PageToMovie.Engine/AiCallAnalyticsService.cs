using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Read side of the AI-call feedback loop: aggregates the <c>user_api_calls</c> SQLite table (every provider
/// call already dual-written there by <see cref="ProjectTelemetryService.LogApiCallAsync"/>) into
/// <see cref="AiCallAnalyticsDto"/> for the admin analytics page. Outcome is the canonical
/// <see cref="AiCallOutcome"/> set once at write time (see <c>ProjectTelemetryService.ClassifyOutcome</c>) —
/// this class no longer re-derives it from raw fields at read time. Read-only — never writes. Formerly
/// scanned every project's telemetry/api_calls.jsonl; that path is superseded now that the DB has the same
/// data with indexes, so we no longer have to walk the filesystem project-by-project.
/// </summary>
public sealed class AiCallAnalyticsService
{
    private readonly UserDatabaseService _userDb;

    public AiCallAnalyticsService(UserDatabaseService userDb)
    {
        _userDb = userDb;
    }

    /// <param name="maxRows">Recent-row cap across all users/projects, newest first (was per-project in the old JSONL scan).</param>
    public async Task<AiCallAnalyticsDto> BuildAsync(int maxRows = 4000, AnalyticsWindow window = AnalyticsWindow.All, CancellationToken ct = default)
    {
        var raw = await _userDb.GetAiCallRawDataAsync(maxRows, ct).ConfigureAwait(false);

        var dto = new AiCallAnalyticsDto
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ProjectsScanned = raw.ProjectsScanned,
            TotalCalls = raw.TotalCalls,
            OkCalls = raw.OkCalls,
            RetriedCalls = raw.RetriedCalls,
            FailedCalls = raw.FailedCalls,
            FakeCalls = raw.FakeCalls,
            TotalCostUsd = Math.Round(raw.TotalCostUsd, 4),
            AvgDurationMs = Math.Round(raw.AvgDurationMs, 0),
            Ops = raw.Ops,
            Models = raw.Models,
            OverrideReasons = raw.OverrideReasons,
        };

        dto.RecentFailures = raw.Failures
            .OrderByDescending(f => f.Ts ?? DateTimeOffset.MinValue)
            .Take(25)
            .Select(f => new AiFailureSample
            {
                Ts = f.Ts,
                Op = string.IsNullOrWhiteSpace(f.Kind) ? "(unknown)" : f.Kind.Trim().ToLowerInvariant(),
                Model = f.Model,
                HttpStatus = f.HttpStatus,
                FailureKind = string.IsNullOrWhiteSpace(f.Outcome) ? "error" : f.Outcome,
                ProjectId = f.ProjectId,
                Error = Trim(f.Error, 240),
            }).ToList();

        dto.Learnings = BuildLearnings(dto);
        var windowLabel = window switch
        {
            AnalyticsWindow.Hour => "Last hour",
            AnalyticsWindow.Day => "Last 24 hours",
            AnalyticsWindow.Week => "Last 7 days",
            AnalyticsWindow.Month => "Last 30 days",
            _ => $"Last {maxRows:N0} calls"
        };
        dto.WindowNote = $"{windowLabel} across {dto.ProjectsScanned} project(s)"
            + (dto.FakeCalls == dto.TotalCalls && dto.TotalCalls > 0 ? " — all fakes-mode calls" : "");
        return dto;
    }

    private static List<string> BuildLearnings(AiCallAnalyticsDto d)
    {
        var outp = new List<string>();
        if (d.TotalCalls == 0) { outp.Add("No AI calls have been logged yet — run the pipeline (or a live generation) to collect data."); return outp; }

        outp.Add($"{d.SuccessRatePct}% of {d.TotalCalls:N0} calls succeeded ({d.RetriedCalls:N0} only after a retry).");

        var worstOp = d.Ops.Where(o => o.Calls >= 3).OrderByDescending(o => o.FailPct).FirstOrDefault();
        if (worstOp is { FailPct: > 0 })
            outp.Add($"Highest failure rate: “{worstOp.Op}” at {worstOp.FailPct}% ({worstOp.Failed}/{worstOp.Calls}).");

        var retryHeavy = d.Ops.Where(o => o.Calls >= 3).OrderByDescending(o => o.RetryPct).FirstOrDefault();
        if (retryHeavy is { RetryPct: > 0 })
            outp.Add($"Most retry-dependent: “{retryHeavy.Op}” needed a retry {retryHeavy.RetryPct}% of the time.");

        var costliest = d.Ops.OrderByDescending(o => o.CostUsd).FirstOrDefault();
        if (costliest is { CostUsd: > 0 })
            outp.Add($"Costliest operation: “{costliest.Op}” at ${costliest.CostUsd:N2}.");

        var slowest = d.Ops.Where(o => o.Calls >= 3).OrderByDescending(o => o.AvgDurationMs).FirstOrDefault();
        if (slowest is { AvgDurationMs: > 0 })
            outp.Add($"Slowest operation: “{slowest.Op}” averaging {slowest.AvgDurationMs:N0} ms.");

        if (d.FakeCalls > 0 && d.FakeCalls < d.TotalCalls)
            outp.Add($"{d.FakeCalls:N0} of {d.TotalCalls:N0} calls were fakes-mode (excluded from real spend).");

        if (d.OverrideReasons.Count > 0)
        {
            var total = d.OverrideReasons.Sum(r => r.Count);
            var breakdown = string.Join(", ", d.OverrideReasons.Select(r => $"{r.Count} {r.Reason}"));
            outp.Add($"{total:N0} style-gate override(s) — {breakdown}.");
        }

        return outp;
    }

    private static string Trim(string? s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
