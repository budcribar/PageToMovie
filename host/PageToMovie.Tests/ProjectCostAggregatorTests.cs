using PageToMovie.Engine;

namespace PageToMovie.Tests;

public class ProjectCostAggregatorTests : IDisposable
{
    private readonly string _root;

    public ProjectCostAggregatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ptm-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task Empty_project_still_returns_both_line_items()
    {
        var id = "empty";
        Directory.CreateDirectory(Path.Combine(_root, id));
        var s = await ProjectCostAggregator.BuildSummaryAsync(id, _root);
        Assert.Equal(0, s.AdaptationEstimateUsd);
        Assert.Equal(0, s.VideoEstimateUsd);
        Assert.Equal(2, s.EstimateLines.Count);
        Assert.Contains(s.EstimateLines, l => l.Category == "adaptation");
        Assert.Contains(s.EstimateLines, l => l.Category == "video");
    }

    [Fact]
    public async Task Scenes_produce_adaptation_and_video_estimates()
    {
        var id = "with-scenes";
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"),
            """{"scenes":[{"key":"s1","clips":[{}]},{"key":"s2","clips":[{},{}]}]}""");
        var s = await ProjectCostAggregator.BuildSummaryAsync(id, _root);
        Assert.True(s.AdaptationEstimateUsd > 0, "adaptation should be > 0 for 2 scenes");
        Assert.True(s.VideoEstimateUsd > 0, "video should be > 0 for clips");
        Assert.Equal(s.AdaptationEstimateUsd + s.VideoEstimateUsd, s.TotalEstimateUsd, precision: 2);
    }

    [Fact]
    public async Task Ledger_splits_actuals_by_category()
    {
        var id = "ledger";
        Directory.CreateDirectory(Path.Combine(_root, id));
        var ledger = new CostLedgerService(_root);
        ledger.Record(id, "adaptation", 1.25, "stage1");
        ledger.Record(id, "llm", 0.75, "also adaptation");
        ledger.Record(id, "video", 3.50, "gen");
        ledger.Record(id, "audio", 0.50, "tts rolls into video");

        var s = await ProjectCostAggregator.BuildSummaryAsync(id, _root, ledger);
        Assert.Equal(2.0, s.AdaptationActualUsd, precision: 2);
        Assert.Equal(4.0, s.VideoActualUsd, precision: 2);
        Assert.Equal(6.0, s.TotalActualUsd, precision: 2);
        Assert.Equal(4, s.LedgerEntries);
    }

    [Fact]
    public async Task Cost_split_lines_always_present_even_with_ledger_only()
    {
        var id = "lines";
        Directory.CreateDirectory(Path.Combine(_root, id));
        var ledger = new CostLedgerService(_root);
        ledger.Record(id, "video", 1.0);
        var s = await ProjectCostAggregator.BuildSummaryAsync(id, _root, ledger);
        Assert.Equal(2, s.EstimateLines.Count);
        Assert.Equal(2, s.ActualLines.Count);
        Assert.Contains(s.ActualLines, l => l.Category == "adaptation" && l.Usd == 0);
        Assert.Contains(s.ActualLines, l => l.Category == "video" && l.Usd == 1.0);
    }
}
