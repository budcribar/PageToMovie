using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClassifierBenchmarks;

public static class ReportWriter
{
    private const string LatestMdFile = "LATEST.md";
    public static async Task WriteRunArtifactsAsync(BenchPaths paths, BenchmarkRun run)
    {
        var dir = Path.Combine(paths.Runs, run.RunId);
        Directory.CreateDirectory(dir);
        var summaryPath = Path.Combine(dir, "summary.json");
        var detailsPath = Path.Combine(dir, "details.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(StripSamples(run), JsonDefaults.Pretty));
        await File.WriteAllTextAsync(detailsPath, JsonSerializer.Serialize(run, JsonDefaults.Pretty));
        await File.WriteAllTextAsync(Path.Combine(dir, "report.md"), BuildRunMarkdown(run));
    }

    public static async Task AppendHistoryAsync(BenchPaths paths, BenchmarkRun run)
    {
        HistoryIndex index;
        if (File.Exists(paths.HistoryIndex))
        {
            index = JsonSerializer.Deserialize<HistoryIndex>(
                await File.ReadAllTextAsync(paths.HistoryIndex), JsonDefaults.Flexible) ?? new HistoryIndex();
        }
        else index = new HistoryIndex();

        index.Runs.RemoveAll(r => r.RunId == run.RunId);
        index.Runs.Insert(0, new HistoryEntry
        {
            RunId = run.RunId,
            Utc = run.Utc,
            ProjectId = run.Config.ProjectId,
            Tasks = run.Config.Tasks.ToList(),
            Models = run.Config.Models.ToList(),
            Prompts = run.Config.Prompts.ToList(),
            SummaryRel = Path.Combine("runs", run.RunId, "summary.json").Replace('\\', '/'),
            Note = run.Config.Note,
            Scores = run.Results.Select(r => new HistoryScore
            {
                Task = r.Task,
                Model = r.Model,
                PromptId = r.PromptId,
                Temperature = r.Temperature,
                Metric = r.Metric,
                BaselineScore = r.BaselineScore,
                AiScore = r.AiScore,
                Winner = r.Winner,
                SampleCount = r.SampleCount,
                CuratedGold = r.CuratedGold,
            }).ToList(),
        });

        // keep last 200 runs
        if (index.Runs.Count > 200)
            index.Runs = index.Runs.Take(200).ToList();

        await File.WriteAllTextAsync(paths.HistoryIndex, JsonSerializer.Serialize(index, JsonDefaults.Pretty));
    }

    public static async Task WriteAggregateReportsAsync(BenchPaths paths)
    {
        HistoryIndex index;
        if (!File.Exists(paths.HistoryIndex))
        {
            await File.WriteAllTextAsync(Path.Combine(paths.Reports, LatestMdFile),
                "# Classifier benchmarks\n\nNo runs yet. `dotnet run --project host/tools/ClassifierBenchmarks -- run`\n");
            return;
        }

        index = JsonSerializer.Deserialize<HistoryIndex>(
            await File.ReadAllTextAsync(paths.HistoryIndex), JsonDefaults.Flexible) ?? new HistoryIndex();

        var md = BuildHistoryMarkdown(index);
        var html = BuildHistoryHtml(index);
        await File.WriteAllTextAsync(Path.Combine(paths.Reports, LatestMdFile), md);
        await File.WriteAllTextAsync(Path.Combine(paths.Reports, "history.html"), html);
        await File.WriteAllTextAsync(Path.Combine(paths.Root, LatestMdFile), md);
        Console.WriteLine($"Reports → {Path.Combine(paths.Reports, LatestMdFile)}");
        Console.WriteLine($"Graphs  → {Path.Combine(paths.Reports, "history.html")}");
    }

    static BenchmarkRun StripSamples(BenchmarkRun run)
    {
        return new BenchmarkRun
        {
            RunId = run.RunId,
            Utc = run.Utc,
            Schema = run.Schema,
            Config = run.Config,
            RepoRoot = run.RepoRoot,
            Error = run.Error,
            Results = run.Results.Select(r => new TaskResult
            {
                Task = r.Task,
                ProjectId = r.ProjectId,
                Model = r.Model,
                PromptId = r.PromptId,
                PromptLabel = r.PromptLabel,
                PromptHash = r.PromptHash,
                Temperature = r.Temperature,
                CuratedGold = r.CuratedGold,
                SampleCount = r.SampleCount,
                Metric = r.Metric,
                BaselineScore = r.BaselineScore,
                AiScore = r.AiScore,
                Winner = r.Winner,
                LatencyMs = r.LatencyMs,
                AiParseHits = r.AiParseHits,
                Note = r.Note,
            }).ToList(),
        };
    }

    public static string BuildRunMarkdown(BenchmarkRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Benchmark run `{run.RunId}`");
        sb.AppendLine();
        sb.AppendLine($"- **UTC:** {run.Utc}");
        sb.AppendLine($"- **Project:** `{run.Config.ProjectId}`");
        sb.AppendLine($"- **Models:** {string.Join(", ", run.Config.Models)}");
        sb.AppendLine($"- **Prompts:** {string.Join(", ", run.Config.Prompts)}");
        sb.AppendLine($"- **Tasks:** {string.Join(", ", run.Config.Tasks)}");
        if (!string.IsNullOrWhiteSpace(run.Config.Note))
            sb.AppendLine($"- **Note:** {run.Config.Note}");
        sb.AppendLine();
        sb.AppendLine("| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |");
        sb.AppendLine("|------|-------|--------|------|--------|---|----------|----|--------|---------|------|");
        foreach (var r in run.Results)
        {
            sb.AppendLine(
                $"| {r.Task} | `{r.Model}` | `{r.PromptId}` | {r.Temperature:0.##} | {r.Metric} | {r.SampleCount} | " +
                $"{r.BaselineScore:F3} | {r.AiScore:F3} | **{r.Winner}** | {r.LatencyMs}ms | " +
                $"{(r.CuratedGold ? "curated" : "draft")} |");
        }

        // Prompt / temp comparison table when multiple cells same model/task
        var groups = run.Results.GroupBy(r => (r.Task, r.Model));
        foreach (var g in groups.Where(x => x.Count() > 1))
        {
            sb.AppendLine();
            sb.AppendLine($"## Compare — `{g.Key.Task}` / `{g.Key.Model}`");
            sb.AppendLine();
            sb.AppendLine("| Prompt | Temp | AI score | vs best | Winner vs baseline |");
            sb.AppendLine("|--------|------|----------|---------|--------------------|");
            var best = g.Max(x => x.AiScore);
            foreach (var r in g.OrderByDescending(x => x.AiScore))
            {
                var delta = r.AiScore - best;
                sb.AppendLine(
                    $"| `{r.PromptId}` | {r.Temperature:0.##} | {r.AiScore:F3} | " +
                    $"{(delta >= -0.0005 ? "best" : delta.ToString("+0.000;-0.000", CultureInfo.InvariantCulture))} | {r.Winner} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Per-sample details: `details.json` in this run folder.");
        return sb.ToString();
    }

    public static string BuildHistoryMarkdown(HistoryIndex index)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Classifier benchmarks — history");
        sb.AppendLine();
        sb.AppendLine($"Updated: {DateTimeOffset.UtcNow:u}");
        sb.AppendLine();
        sb.AppendLine("Open **`reports/history.html`** for interactive charts (model / prompt / task over time).");
        sb.AppendLine();
        sb.AppendLine("## Top configuration per task (best AI score in history)");
        sb.AppendLine();
        sb.AppendLine("| Task | Metric | Model | Prompt | Temp | AI | Baseline | Δ vs base | Winner | n | When (UTC) | Run |");
        sb.AppendLine("|------|--------|-------|--------|------|----|----------|-----------|--------|---|------------|-----|");
        foreach (var top in ComputeLeaders(index))
        {
            var delta = top.AiScore - top.BaselineScore;
            sb.AppendLine(
                $"| `{top.Task}` | {top.Metric} | `{top.Model}` | `{top.PromptId}` | {top.Temperature:0.##} | " +
                $"**{top.AiScore:F3}** | {top.BaselineScore:F3} | {delta.ToString("+0.000;-0.000;0", CultureInfo.InvariantCulture)} | " +
                $"**{top.Winner}** | {top.SampleCount} | {top.Utc} | `{top.RunId}` |");
        }

        sb.AppendLine();
        sb.AppendLine("## Latest runs");
        sb.AppendLine();
        sb.AppendLine("| When (UTC) | Run | Task | Model | Prompt | Temp | Metric | Baseline | AI | Winner | n |");
        sb.AppendLine("|------------|-----|------|-------|--------|------|--------|----------|----|--------|---|");
        foreach (var run in index.Runs.Take(40))
        {
            foreach (var s in run.Scores)
            {
                sb.AppendLine(
                    $"| {run.Utc} | `{run.RunId}` | {s.Task} | `{s.Model}` | `{s.PromptId}` | {s.Temperature:0.##} | {s.Metric} | " +
                    $"{s.BaselineScore:F3} | {s.AiScore:F3} | **{s.Winner}** | {s.SampleCount} |");
            }
        }

        // Sparkline-ish trend tables per task
        sb.AppendLine();
        sb.AppendLine("## AI score trend by task (newest first)");
        foreach (var task in index.Runs.SelectMany(r => r.Scores).Select(s => s.Task).Distinct().OrderBy(x => x))
        {
            sb.AppendLine();
            sb.AppendLine($"### `{task}`");
            sb.AppendLine();
            sb.AppendLine("| Run | Model | Prompt | Temp | AI | Baseline |");
            sb.AppendLine("|-----|-------|--------|------|----|----------|");
            foreach (var run in index.Runs)
            {
                foreach (var s in run.Scores.Where(x => x.Task == task))
                    sb.AppendLine($"| `{run.RunId}` | `{s.Model}` | `{s.PromptId}` | {s.Temperature:0.##} | {s.AiScore:F3} | {s.BaselineScore:F3} |");
            }
        }

        return sb.ToString();
    }

    /// <summary>Best AI score per task across all history (ties → newer run).</summary>
    static List<LeaderRow> ComputeLeaders(HistoryIndex index)
    {
        var best = new Dictionary<string, LeaderRow>(StringComparer.OrdinalIgnoreCase);
        // Newest first so first max wins ties on equal score when we only replace if strictly greater
        foreach (var run in index.Runs) // index is newest-first
        {
            foreach (var s in run.Scores)
            {
                if (string.IsNullOrWhiteSpace(s.Task) || s.Metric == "error") continue;
                if (!best.TryGetValue(s.Task, out var cur) || s.AiScore > cur.AiScore + 1e-9)
                {
                    best[s.Task] = new LeaderRow
                    {
                        Task = s.Task,
                        Metric = s.Metric,
                        Model = s.Model,
                        PromptId = s.PromptId,
                        Temperature = s.Temperature,
                        AiScore = s.AiScore,
                        BaselineScore = s.BaselineScore,
                        Winner = s.Winner,
                        SampleCount = s.SampleCount,
                        Utc = run.Utc,
                        RunId = run.RunId,
                    };
                }
            }
        }
        return best.Values.OrderBy(x => x.Task, StringComparer.OrdinalIgnoreCase).ToList();
    }

    sealed class LeaderRow
    {
        public string Task { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Model { get; set; } = "";
        public string PromptId { get; set; } = "";
        public double Temperature { get; set; }
        public double AiScore { get; set; }
        public double BaselineScore { get; set; }
        public string Winner { get; set; } = "";
        public int SampleCount { get; set; }
        public string Utc { get; set; } = "";
        public string RunId { get; set; } = "";
    }

    public static string BuildHistoryHtml(HistoryIndex index)
    {
        var seriesMap = CollectSeriesMap(index);
        var tasks = index.Runs.SelectMany(r => r.Scores).Select(s => s.Task)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Default: one classifier on (prefer ambient_sfx, else first). All others off.
        var defaultTask = tasks.FirstOrDefault(t => t.Equals("ambient_sfx", StringComparison.OrdinalIgnoreCase))
                          ?? tasks.FirstOrDefault()
                          ?? "ambient_sfx";

        var labels = index.Runs.AsEnumerable().Reverse().Select(r => r.Utc).Distinct().ToList();
        var datasets = BuildChartDatasets(seriesMap, labels, defaultTask);
        var chartPayload = JsonSerializer.Serialize(new { labels, datasets }, JsonDefaults.Pretty);
        var tasksJson = JsonSerializer.Serialize(tasks);
        var defaultTaskJson = JsonSerializer.Serialize(defaultTask);
        var filterButtons = BuildFilterButtons(tasks, defaultTask);
        var promptRows = BuildLatestPromptRows(index.Runs.FirstOrDefault(), defaultTask);
        var histRows = BuildHistoryRows(index, defaultTask);
        var leaderRows = BuildLeaderRows(index);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Classifier benchmarks</title>
  <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js"></script>
  <style>
    :root { font-family: ui-sans-serif, system-ui, Segoe UI, sans-serif; color: #0f172a; }
    body { margin: 24px; max-width: 1200px; }
    h1 { font-size: 1.5rem; }
    h2 { font-size: 1.15rem; margin-top: 2rem; }
    .muted { color: #64748b; font-size: 0.9rem; }
    table { border-collapse: collapse; width: 100%; font-size: 0.9rem; }
    th, td { border-bottom: 1px solid #e2e8f0; padding: 6px 8px; text-align: left; }
    th { background: #f8fafc; }
    td.num { font-variant-numeric: tabular-nums; }
    td.ai { color: #1d4ed8; }
    code { font-size: 0.85em; }
    .chart-wrap { background: #fff; border: 1px solid #e2e8f0; border-radius: 8px; padding: 12px; }
    .swatch { display: inline-block; width: 28px; height: 0; border-top: 3px solid #2563eb; vertical-align: middle; margin-right: 4px; }
    .swatch.dash { border-top-style: dashed; }
    .filter-bar {
      display: flex; flex-wrap: wrap; gap: 8px; align-items: center;
      margin: 16px 0; padding: 12px; background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px;
    }
    .filter-bar .label { font-size: 0.85rem; color: #64748b; margin-right: 4px; }
    .filter-btn {
      border: 1px solid #cbd5e1; background: #fff; color: #334155;
      border-radius: 999px; padding: 6px 12px; font-size: 0.85rem; cursor: pointer;
    }
    .filter-btn:hover { border-color: #94a3b8; background: #f1f5f9; }
    .filter-btn.active {
      background: #2563eb; border-color: #2563eb; color: #fff; font-weight: 600;
    }
    .filter-btn.meta { background: #f1f5f9; }
    .filter-btn.meta.active { background: #0f172a; border-color: #0f172a; color: #fff; }
    tr.hidden-row { display: none; }
    #leaders-table { margin-bottom: 0.5rem; }
    #leaders-table tbody tr:hover { background: #f8fafc; }
    .section-toggle {
      display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
      margin-top: 2rem;
    }
    .section-toggle h2 { margin: 0; font-size: 1.15rem; }
    .toggle-btn {
      border: 1px solid #cbd5e1; background: #fff; color: #334155;
      border-radius: 6px; padding: 4px 10px; font-size: 0.8rem; cursor: pointer;
    }
    .toggle-btn:hover { background: #f1f5f9; }
    .toggle-btn[aria-expanded="true"] { background: #e2e8f0; }
    .collapsible.collapsed { display: none; }
  </style>
</head>
<body>
  <h1>Classifier benchmarks</h1>
  <p class="muted">Generated {{DateTimeOffset.UtcNow:u}} · gold under <code>host/evals/classifier_benchmarks/gold</code></p>

  <h2>Top configuration per task</h2>
  <p class="muted">Best <strong>AI</strong> score in history for each classifier (ties keep the newer run).</p>
  <table id="leaders-table">
    <thead>
      <tr>
        <th>Task</th><th>Metric</th><th>Model</th><th>Prompt</th><th>Temp</th>
        <th>AI</th><th>Baseline</th><th>Δ vs base</th><th>Winner</th><th>n</th><th>When (UTC)</th><th>Run</th>
      </tr>
    </thead>
    <tbody>
{{leaderRows}}
    </tbody>
  </table>

  <h2>AI vs baseline over time</h2>
  <p class="muted">
    Legend: <span class="swatch"></span> solid = AI &nbsp;
    <span class="swatch dash"></span> dashed = baseline (same color as its AI pair)
  </p>
  <div class="filter-bar" id="filter-bar" role="group" aria-label="Classifier filters">
    <span class="label">Show:</span>
{{filterButtons}}
    <button type="button" class="filter-btn meta" data-action="all" aria-pressed="false">All</button>
    <button type="button" class="filter-btn meta" data-action="none" aria-pressed="false">None</button>
  </div>
  <p class="muted" id="filter-hint">Showing <strong id="filter-active">{{Esc(defaultTask)}}</strong> only. Click classifiers to toggle.</p>
  <div class="chart-wrap"><canvas id="trend" height="140"></canvas></div>

  <h2>Latest run scores</h2>
  <table id="latest-table">
    <thead><tr><th>Task</th><th>Model</th><th>Prompt</th><th>Temp</th><th>Metric</th><th>Baseline</th><th>AI</th><th>Winner</th><th>n</th></tr></thead>
    <tbody>
{{promptRows}}
    </tbody>
  </table>

  <div class="section-toggle">
    <h2>History</h2>
    <button type="button" class="toggle-btn" id="history-toggle" aria-expanded="false" aria-controls="history-panel">Show history</button>
  </div>
  <div id="history-panel" class="collapsible collapsed">
    <p class="muted">Full run log (filtered by classifier pills above).</p>
    <table id="history-table">
      <thead><tr><th>UTC</th><th>Run</th><th>Task</th><th>Model</th><th>Prompt</th><th>Temp</th><th>Baseline</th><th>AI</th><th>Winner</th></tr></thead>
      <tbody>
{{histRows}}
      </tbody>
    </table>
  </div>

  <script>
    const payload = {{chartPayload}};
    const allTasks = {{tasksJson}};
    const defaultTask = {{defaultTaskJson}};
    const active = new Set([defaultTask]);

    // Tooltip: only the hovered setup; if other points sit on top of it (pixel overlap), include those too.
    const OVERLAP_PX = 8;
    Chart.Interaction.modes.nearestOverlap = function(chart, e, options, useFinalPosition) {
      const nearest = Chart.Interaction.modes.nearest(
        chart, e, { axis: 'xy', intersect: false }, useFinalPosition);
      if (!nearest.length) return [];
      const primary = nearest[0];
      const pe = primary.element;
      if (!pe || pe.skip) return nearest;

      const hits = [];
      const seen = new Set();
      chart.data.datasets.forEach((ds, datasetIndex) => {
        if (ds.hidden) return;
        const meta = chart.getDatasetMeta(datasetIndex);
        if (!meta || meta.hidden) return;
        meta.data.forEach((el, index) => {
          if (!el || el.skip || el.x == null || el.y == null) return;
          const dist = Math.hypot(el.x - pe.x, el.y - pe.y);
          if (dist <= OVERLAP_PX) {
            const key = datasetIndex + ':' + index;
            if (seen.has(key)) return;
            seen.add(key);
            hits.push({ datasetIndex, index, element: el });
          }
        });
      });
      return hits.length ? hits : nearest;
    };

    const chart = new Chart(document.getElementById('trend'), {
      type: 'line',
      data: payload,
      options: {
        responsive: true,
        interaction: { mode: 'nearestOverlap', intersect: false },
        scales: {
          y: { min: 0, max: 1, title: { display: true, text: 'Score (0–1)' } },
          x: { title: { display: true, text: 'Run time (UTC)' } }
        },
        plugins: {
          legend: {
            position: 'bottom',
            labels: {
              boxWidth: 16,
              usePointStyle: false,
              filter: (item, data) => {
                const ds = data.datasets[item.datasetIndex];
                return !ds.hidden;
              }
            }
          },
          tooltip: {
            mode: 'nearestOverlap',
            intersect: false,
            itemSort: (a, b) => (b.parsed?.y ?? 0) - (a.parsed?.y ?? 0),
            filter: (item) => item.parsed?.y != null && !Number.isNaN(item.parsed.y),
            callbacks: {
              title: (items) => {
                if (!items.length) return '';
                // Same x = same run time label
                return items[0].label || '';
              },
              label: (item) => {
                const y = item.parsed?.y;
                if (y == null || Number.isNaN(y)) return null;
                // dataset.label is already "task · model · prompt · t=… · AI|baseline"
                return `${item.dataset.label}: ${Number(y).toFixed(3)}`;
              },
              footer: (items) => {
                if (items.length <= 1) return '';
                return `(${items.length} overlapping points)`;
              }
            }
          },
          title: { display: false }
        }
      }
    });

    function applyFilter() {
      // Chart series
      chart.data.datasets.forEach(ds => {
        const t = ds.task || '';
        ds.hidden = active.size === 0 ? true : !active.has(t);
      });
      chart.update();

      // Tables
      document.querySelectorAll('tr.score-row').forEach(row => {
        const t = row.getAttribute('data-task') || '';
        const show = active.size === 0 ? false : active.has(t);
        row.classList.toggle('hidden-row', !show);
      });

      // Buttons
      document.querySelectorAll('.filter-btn[data-task]').forEach(btn => {
        const t = btn.getAttribute('data-task');
        const on = active.has(t);
        btn.classList.toggle('active', on);
        btn.setAttribute('aria-pressed', on ? 'true' : 'false');
      });
      const allOn = allTasks.length > 0 && allTasks.every(t => active.has(t));
      const noneOn = active.size === 0;
      document.querySelectorAll('.filter-btn[data-action="all"]').forEach(b => {
        b.classList.toggle('active', allOn);
        b.setAttribute('aria-pressed', allOn ? 'true' : 'false');
      });
      document.querySelectorAll('.filter-btn[data-action="none"]').forEach(b => {
        b.classList.toggle('active', noneOn);
        b.setAttribute('aria-pressed', noneOn ? 'true' : 'false');
      });

      const hint = document.getElementById('filter-active');
      if (hint) {
        if (active.size === 0) hint.textContent = 'nothing';
        else if (allOn) hint.textContent = 'all classifiers';
        else hint.textContent = [...active].sort().join(', ');
      }
    }

    document.getElementById('filter-bar').addEventListener('click', (e) => {
      const btn = e.target.closest('.filter-btn');
      if (!btn) return;
      const action = btn.getAttribute('data-action');
      if (action === 'all') {
        active.clear();
        allTasks.forEach(t => active.add(t));
      } else if (action === 'none') {
        active.clear();
      } else {
        const t = btn.getAttribute('data-task');
        if (!t) return;
        if (active.has(t)) active.delete(t);
        else active.add(t);
      }
      applyFilter();
    });

    const historyToggle = document.getElementById('history-toggle');
    const historyPanel = document.getElementById('history-panel');
    if (historyToggle && historyPanel) {
      historyToggle.addEventListener('click', () => {
        historyPanel.classList.toggle('collapsed');
        const isOpen = !historyPanel.classList.contains('collapsed');
        historyToggle.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
        historyToggle.textContent = isOpen ? 'Hide history' : 'Show history';
      });
    }

    applyFilter();
  </script>
</body>
</html>
""";
    }

    static Dictionary<string, List<(string Utc, double Ai, double Base, string Task)>> CollectSeriesMap(
        HistoryIndex index)
    {
        var seriesMap = new Dictionary<string, List<(string Utc, double Ai, double Base, string Task)>>();
        foreach (var run in index.Runs.AsEnumerable().Reverse())
        {
            foreach (var s in run.Scores)
            {
                var key = SeriesKey(s);
                if (!seriesMap.TryGetValue(key, out var list))
                {
                    list = new List<(string, double, double, string)>();
                    seriesMap[key] = list;
                }
                list.Add((run.Utc, s.AiScore, s.BaselineScore, s.Task));
            }
        }
        return seriesMap;
    }

    static List<object> BuildChartDatasets(
        Dictionary<string, List<(string Utc, double Ai, double Base, string Task)>> seriesMap,
        List<string> labels,
        string defaultTask)
    {
        var colors = new[]
        {
            "#2563eb", "#dc2626", "#16a34a", "#ca8a04", "#9333ea", "#0891b2", "#ea580c", "#4f46e5",
            "#0d9488", "#be185d", "#65a30d", "#7c3aed"
        };
        var datasets = new List<object>();
        var i = 0;
        foreach (var (key, pts) in seriesMap.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var color = colors[i % colors.Length];
            i++;
            var task = pts.FirstOrDefault().Task ?? key.Split('·')[0].Trim();
            var hidden = !task.Equals(defaultTask, StringComparison.OrdinalIgnoreCase);
            datasets.Add(AiDataset(key, task, AlignSeriesValues(labels, pts, ai: true), color, hidden));
            datasets.Add(BaselineDataset(key, task, AlignSeriesValues(labels, pts, ai: false), color, hidden));
        }
        return datasets;
    }

    static List<double?> AlignSeriesValues(
        List<string> labels,
        List<(string Utc, double Ai, double Base, string Task)> pts,
        bool ai)
    {
        var values = new List<double?>();
        foreach (var lab in labels)
        {
            var hit = pts.LastOrDefault(p => p.Utc == lab);
            values.Add(hit.Utc == null ? null : (ai ? hit.Ai : hit.Base));
        }
        return values;
    }

    static Dictionary<string, object?> AiDataset(
        string key, string task, List<double?> data, string color, bool hidden) =>
        new()
        {
            ["label"] = key + " · AI",
            ["task"] = task,
            ["data"] = data,
            ["borderColor"] = color,
            ["backgroundColor"] = color + "33",
            ["borderWidth"] = 2.5,
            ["spanGaps"] = true,
            ["tension"] = 0.2,
            ["pointRadius"] = 3,
            ["pointHoverRadius"] = 5,
            ["hidden"] = hidden,
        };

    static Dictionary<string, object?> BaselineDataset(
        string key, string task, List<double?> data, string color, bool hidden) =>
        new()
        {
            ["label"] = key + " · baseline",
            ["task"] = task,
            ["data"] = data,
            ["borderColor"] = color,
            ["backgroundColor"] = "transparent",
            ["borderWidth"] = 2,
            ["borderDash"] = new[] { 7, 4 },
            ["spanGaps"] = true,
            ["tension"] = 0,
            ["pointRadius"] = 2,
            ["pointHoverRadius"] = 4,
            ["pointStyle"] = "rectRot",
            ["hidden"] = hidden,
        };

    static StringBuilder BuildFilterButtons(List<string> tasks, string defaultTask)
    {
        var filterButtons = new StringBuilder();
        foreach (var t in tasks)
        {
            var on = t.Equals(defaultTask, StringComparison.OrdinalIgnoreCase);
            filterButtons.Append(
                $"<button type=\"button\" class=\"filter-btn{(on ? " active" : "")}\" data-task=\"{Esc(t)}\" " +
                $"aria-pressed=\"{(on ? "true" : "false")}\">{Esc(t)}</button>\n");
        }
        return filterButtons;
    }

    static StringBuilder BuildLatestPromptRows(HistoryEntry? latest, string defaultTask)
    {
        var promptRows = new StringBuilder();
        if (latest == null)
            return promptRows;
        foreach (var s in latest.Scores.OrderBy(x => x.Task).ThenBy(x => x.Model)
                     .ThenBy(x => x.PromptId).ThenBy(x => x.Temperature).ThenByDescending(x => x.AiScore))
        {
            var show = s.Task.Equals(defaultTask, StringComparison.OrdinalIgnoreCase) ? "" : " hidden-row";
            promptRows.AppendLine(
                $"<tr class=\"score-row{show}\" data-task=\"{Esc(s.Task)}\">" +
                $"<td>{Esc(s.Task)}</td><td><code>{Esc(s.Model)}</code></td><td><code>{Esc(s.PromptId)}</code></td>" +
                $"<td>{s.Temperature:0.##}</td><td>{s.Metric}</td><td>{s.BaselineScore:F3}</td>" +
                $"<td><strong>{s.AiScore:F3}</strong></td><td>{Esc(s.Winner)}</td><td>{s.SampleCount}</td></tr>");
        }
        return promptRows;
    }

    static StringBuilder BuildHistoryRows(HistoryIndex index, string defaultTask)
    {
        var histRows = new StringBuilder();
        foreach (var run in index.Runs.Take(50))
        {
            foreach (var s in run.Scores)
            {
                var show = s.Task.Equals(defaultTask, StringComparison.OrdinalIgnoreCase) ? "" : " hidden-row";
                histRows.AppendLine(
                    $"<tr class=\"score-row{show}\" data-task=\"{Esc(s.Task)}\">" +
                    $"<td>{Esc(run.Utc)}</td><td><code>{Esc(run.RunId)}</code></td><td>{Esc(s.Task)}</td>" +
                    $"<td><code>{Esc(s.Model)}</code></td><td><code>{Esc(s.PromptId)}</code></td>" +
                    $"<td>{s.Temperature:0.##}</td>" +
                    $"<td>{s.BaselineScore:F3}</td><td>{s.AiScore:F3}</td><td>{Esc(s.Winner)}</td></tr>");
            }
        }
        return histRows;
    }

    static StringBuilder BuildLeaderRows(HistoryIndex index)
    {
        var leaderRows = new StringBuilder();
        foreach (var top in ComputeLeaders(index))
        {
            var delta = top.AiScore - top.BaselineScore;
            leaderRows.AppendLine(
                $"<tr class=\"leader-row\" data-task=\"{Esc(top.Task)}\">" +
                $"<td><code>{Esc(top.Task)}</code></td>" +
                $"<td>{Esc(top.Metric)}</td>" +
                $"<td><code>{Esc(top.Model)}</code></td>" +
                $"<td><code>{Esc(top.PromptId)}</code></td>" +
                $"<td>{top.Temperature:0.##}</td>" +
                $"<td class=\"num ai\"><strong>{top.AiScore:F3}</strong></td>" +
                $"<td class=\"num\">{top.BaselineScore:F3}</td>" +
                $"<td class=\"num\">{delta.ToString("+0.000;-0.000;0", CultureInfo.InvariantCulture)}</td>" +
                $"<td><strong>{Esc(top.Winner)}</strong></td>" +
                $"<td>{top.SampleCount}</td>" +
                $"<td>{Esc(top.Utc)}</td>" +
                $"<td><code>{Esc(top.RunId)}</code></td></tr>");
        }
        return leaderRows;
    }

    static string SeriesKey(HistoryScore s)
    {
        var temp = s.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{s.Task} · {s.Model} · {s.PromptId} · t={temp}";
    }

    static string Esc(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
