using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin
{
    /// <summary>LoadSim / timing / gen-errors / charts domain for the Admin page.</summary>
    internal sealed class AdminTelemetry
    {
        private readonly Admin S;
        public AdminTelemetry(Admin host) => S = host;

        internal LoadSimLiveStateDto? _loadSim;
        internal List<ProcessSampleDto> _processHistory = new();
        internal EngineApiClient.TimingTelemetryTrendDto? _timingTelemetry;
        /// <summary>Set when a chart upsert throws after we had real data to draw — surfaced in the UI so failures aren't silent.</summary>
        internal string? _chartWarning;

        internal List<EngineApiClient.GenerationErrorRowDto>? _genErrors;
        internal bool _genErrorsBusy;
        internal string _genErrorTypeFilter = "";
        internal string _genErrorProjectFilter = "";
        internal bool _seedingTiming;

        internal async Task RefreshGenerationErrorsAsync()
        {
            _genErrorsBusy = true;
            try
            {
                var dto = await S.Api.GetAdminGenerationErrorsAsync(
                    errorType: string.IsNullOrWhiteSpace(_genErrorTypeFilter) ? null : _genErrorTypeFilter,
                    projectId: string.IsNullOrWhiteSpace(_genErrorProjectFilter) ? null : _genErrorProjectFilter,
                    take: 200);
                _genErrors = dto?.Rows ?? new();
            }
            catch
            {
                /* keep prior list — panel is best-effort visibility, not critical path */
            }
            finally
            {
                _genErrorsBusy = false;
            }
        }

        internal static string GetGenErrorTypeBadgeClass(string errorType) => errorType switch
        {
            "http_error" => "bg-danger",
            "exception" => "bg-danger",
            "structural_gate_failure" => "bg-warning text-dark",
            "empty_response" => "bg-warning text-dark",
            "partial_coverage" => "bg-info text-dark",
            _ => "bg-secondary",
        };

        internal async Task SeedTimingDatabaseAsync()
        {
            _seedingTiming = true;
            S._actionMsg = null;
            try
            {
                var res = await S.Api.PostAdminTimingTelemetrySeedAsync();
                S._actionMsg = res?.Message ?? "Seeded empirical benchmark entries into database.";
            }
            catch (Exception ex)
            {
                S._actionMsg = $"Failed to seed database: {ex.Message}";
            }
            finally
            {
                _seedingTiming = false;
            }
        }

        internal async Task UpdateChartsAsync()
        {
            try
            {
                if (_loadSim?.History is { Count: > 0 } hist)
                {
                    var labels = hist.Select(h =>
                        TimeSpan.FromSeconds(Math.Max(0, h.ElapsedSec)).ToString(@"m\:ss")).ToArray();
                    var actionsPerSec = hist.Select(h => h.ActionsPerSec).ToArray();
                    var actionsTotal = hist.Select(h => (double)h.ActionsTotal).ToArray();
                    var p50 = hist.Select(h => (double)h.P50Ms).ToArray();
                    var browseP95 = hist.Select(h => (double)h.BrowseP95Ms).ToArray();
                    var errPct = hist.Select(h => h.ErrorRate * 100.0).ToArray();

                    await S.Js.InvokeVoidAsync("filmStudioCharts.upsertLine",
                        "chartLoadSimThroughput",
                        labels,
                        new object[]
                        {
                            new { label = "actions/s", data = actionsPerSec, color = "#38bdf8", yAxisID = "y" },
                            new { label = "total actions", data = actionsTotal, color = "#a78bfa", yAxisID = "y1" },
                        },
                        new { dualY = true, yTitle = "actions/s", y2Title = "total" });

                    await S.Js.InvokeVoidAsync("filmStudioCharts.upsertLine",
                        "chartLoadSimLatency",
                        labels,
                        new object[]
                        {
                            new { label = "p50 ms", data = p50, color = "#34d399", yAxisID = "y" },
                            new { label = "browse p95 ms", data = browseP95, color = "#fbbf24", yAxisID = "y" },
                            new { label = "error %", data = errPct, color = "#f87171", yAxisID = "y1" },
                        },
                        new { dualY = true, yTitle = "latency ms", y2Title = "error %" });
                }

                if (_processHistory is { Count: > 0 } mem)
                {
                    var memLabels = mem.Select(s => s.At.ToLocalTime().ToString("HH:mm:ss")).ToArray();
                    var ws = mem.Select(s => s.WorkingSetMb).ToArray();
                    var gc = mem.Select(s => s.GcHeapMb).ToArray();
                    var threads = mem.Select(s => (double)s.ThreadCount).ToArray();

                    await S.Js.InvokeVoidAsync("filmStudioCharts.upsertLine",
                        "chartProcessMemory",
                        memLabels,
                        new object[]
                        {
                            new { label = "Working set (MB)", data = ws, color = "#22d3ee", yAxisID = "y" },
                            new { label = "GC heap (MB)", data = gc, color = "#c084fc", yAxisID = "y" },
                            new { label = "Threads", data = threads, color = "#fb7185", yAxisID = "y1" },
                        },
                        new { dualY = true, yTitle = "MB", y2Title = "threads" });
                }

                _chartWarning = null;
            }
            catch (Exception ex)
            {
                // First render can race Chart.js module init — only surface it to the admin
                // once we've actually seen data to chart (otherwise every idle poll would show a false alarm).
                _chartWarning = (_loadSim?.History is { Count: > 0 } || _processHistory is { Count: > 0 })
                    ? ex.Message
                    : null;
            }
        }

        internal string GetHitRatePolylinePoints()
        {
            var trend = _timingTelemetry?.Trend;
            if (trend is null || trend.Count == 0)
                return "50,100 440,100";

            int count = trend.Count;
            double startX = 50.0;
            double endX = 440.0;
            double stepX = count > 1 ? (endX - startX) / (count - 1) : 0;

            var points = new List<string>();
            for (int i = 0; i < count; i++)
            {
                double x = startX + (i * stepX);
                double hitRate = Math.Clamp(trend[i].HitRatePercent, 0.0, 100.0);
                double y = 100.0 - (hitRate / 100.0 * 80.0);
                points.Add($"{x:F1},{y:F1}");
            }
            return string.Join(" ", points);
        }

        internal string GetMaePolylinePoints()
        {
            var trend = _timingTelemetry?.Trend;
            if (trend is null || trend.Count == 0)
                return "50,100 440,100";

            int count = trend.Count;
            double startX = 50.0;
            double endX = 440.0;
            double stepX = count > 1 ? (endX - startX) / (count - 1) : 0;

            var points = new List<string>();
            for (int i = 0; i < count; i++)
            {
                double x = startX + (i * stepX);
                double mae = Math.Clamp(trend[i].MeanAbsoluteErrorSec, 0.0, 2.0);
                double y = 100.0 - (mae / 2.0 * 80.0);
                points.Add($"{x:F1},{y:F1}");
            }
            return string.Join(" ", points);
        }

        internal static string FormatTrendTimestamp(string ts)
        {
            if (DateTime.TryParse(ts, out var dt))
                return dt.ToString("MM/dd");
            return ts;
        }
    }
}
