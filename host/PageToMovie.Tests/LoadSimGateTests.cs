using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.LoadSim;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>E2-style unit tests for LoadSim gate evaluation (no HTTP).</summary>
public class LoadSimGateTests
{
    [Fact]
    public void Gates_pass_on_clean_results()
    {
        var opts = new SimOptions { MaxErrorRate = 0.01, MaxBrowseP95Ms = 500 };
        var r = new LoadSimResults
        {
            Http = new HttpStats
            {
                Total = 100,
                Errors = 0,
                ErrorRate = 0,
                BrowseP95Ms = 40,
            },
            Health = new HealthStats { Ok = 50, Fail = 0 },
            Jobs = new JobStats { Server5xx = 0 },
            Server = new ServerStats { ConfiguredMaxVideoInFlight = 8, PeakApiInFlight = 3 },
        };
        Assert.True(GateEvaluator.Evaluate(r, opts));
        Assert.True(r.Passed);
        Assert.All(r.Gates, g => Assert.True(g.Pass));
    }

    [Fact]
    public void Gates_fail_on_high_error_rate()
    {
        var opts = new SimOptions { MaxErrorRate = 0.01, MaxBrowseP95Ms = 500 };
        var r = new LoadSimResults
        {
            Http = new HttpStats { Total = 100, Errors = 10, ErrorRate = 0.10, BrowseP95Ms = 20 },
            Health = new HealthStats { Ok = 10, Fail = 0 },
            Jobs = new JobStats(),
        };
        Assert.False(GateEvaluator.Evaluate(r, opts));
        Assert.Contains(r.Gates, g => g.Name == "http_error_rate" && !g.Pass);
    }

    [Fact]
    public void Gates_fail_on_browse_p95()
    {
        var opts = new SimOptions { MaxErrorRate = 0.05, MaxBrowseP95Ms = 100 };
        var r = new LoadSimResults
        {
            Http = new HttpStats { Total = 50, Errors = 0, ErrorRate = 0, BrowseP95Ms = 800 },
            Health = new HealthStats { Ok = 5, Fail = 0 },
            Jobs = new JobStats(),
        };
        Assert.False(GateEvaluator.Evaluate(r, opts));
        Assert.Contains(r.Gates, g => g.Name == "browse_p95" && !g.Pass);
    }

    [Fact]
    public void SimOptions_parses_ready_barrier_flags()
    {
        var o = SimOptions.Parse(new[]
        {
            "--readyTimeoutSec", "45",
            "--skipReadyBarrier",
            "--users", "10",
        });
        Assert.Equal(45, o.ReadyTimeoutSec);
        Assert.True(o.SkipReadyBarrier);
        Assert.Equal(10, o.Users);

        var def = SimOptions.Parse(Array.Empty<string>());
        Assert.Equal(60, def.ReadyTimeoutSec);
        Assert.False(def.SkipReadyBarrier);
    }

    [Fact]
    public void MetricsCollector_records_and_builds()
    {
        var m = new MetricsCollector();
        for (var i = 0; i < 20; i++)
            m.Record("browse", 200, 10 + i);
        m.Record("gen", 409, 5, intentionalConflict: true);
        m.Record("gen", 423, 8, intentionalConflict: true);
        m.Record("gen", 202, 100);
        m.NoteServerCapacity(12, 4);

        var r = m.Build(new SimOptions { Users = 5, DurationSec = 30 }, TimeSpan.FromSeconds(30));
        Assert.True(r.Http.Total >= 23);
        Assert.Equal(2, r.Http.Intentional409);
        Assert.Equal(1, r.Jobs.Submitted);
        Assert.Equal(2, r.Jobs.Rejected);
        Assert.Equal(12, r.Server.ConfiguredMaxVideoInFlight);
        Assert.Equal(4, r.Server.PeakApiInFlight);
        Assert.True(r.Http.BrowseP95Ms >= r.Http.BrowseP50Ms);
    }

    [Fact]
    public void MetricsCollector_per_action_latency_sorted_by_p95()
    {
        var m = new MetricsCollector();
        // Fast path
        for (var i = 0; i < 40; i++)
            m.Record("health", 200, 2);
        // Medium
        for (var i = 0; i < 40; i++)
            m.Record("projects", 200, 20 + (i % 5));
        // Slow tail
        for (var i = 0; i < 40; i++)
            m.Record("scenes", 200, 200 + i * 10);
        // One intentional 409 must not count as error on gen
        m.Record("gen", 409, 50, intentionalConflict: true);
        m.Record("gen", 500, 80); // real error

        var r = m.Build(new SimOptions { Users = 2, DurationSec = 10 }, TimeSpan.FromSeconds(10));
        Assert.NotEmpty(r.ActionLatency);

        // Hottest first
        Assert.Equal("scenes", r.ActionLatency[0].Action);
        Assert.True(r.ActionLatency[0].P95Ms > r.ActionLatency.First(a => a.Action == "health").P95Ms);

        var gen = r.ActionLatency.Single(a => a.Action == "gen");
        Assert.Equal(2, gen.Count);
        Assert.Equal(1, gen.Errors); // only the 500, not intentional 409

        var scenes = r.ActionLatency.Single(a => a.Action == "scenes");
        Assert.Equal(40, scenes.Count);
        Assert.True(scenes.P95Ms >= scenes.P50Ms);
        Assert.True(scenes.P99Ms >= scenes.P95Ms);

        // Counts dict still present
        Assert.Equal(40, r.Actions["scenes"]);
    }

    [Fact]
    public void LoadSimBuster_fixture_is_checked_in_with_flat_id_and_scenes()
    {
        var root = FindRepoRoot();
        Assert.False(string.IsNullOrEmpty(root), "Could not find repo root containing projects/LoadSimBuster.");

        var dir = Path.Combine(root, "projects", "LoadSimBuster");
        var metaPath = Path.Combine(dir, "project.json");
        Assert.True(File.Exists(metaPath), metaPath);

        using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        Assert.Equal("LoadSimBuster", meta.RootElement.GetProperty("id").GetString());
        if (meta.RootElement.TryGetProperty("ownerUserId", out var owner))
            Assert.True(string.IsNullOrWhiteSpace(owner.GetString()), "LoadSimBuster must stay a flat id (no owner).");

        var configPath = Path.Combine(dir, "pipeline_config.json");
        Assert.True(File.Exists(configPath), configPath);
        using var cfg = JsonDocument.Parse(File.ReadAllText(configPath));
        var expectedVideo = SupportedModelCatalog.DefaultModelIdForCapability("video");
        Assert.Equal(expectedVideo, cfg.RootElement.GetProperty("model_name").GetString());

        var blueprintPath = Path.Combine(dir, "blueprint.clips.grok.json");
        Assert.True(File.Exists(blueprintPath), blueprintPath);
        using var bp = JsonDocument.Parse(File.ReadAllText(blueprintPath));
        Assert.True(bp.RootElement.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() >= 8);
        Assert.True(
            scenes.EnumerateArray().All(s =>
                s.TryGetProperty("veo_clips", out var clips) &&
                clips.GetArrayLength() > 0 &&
                clips[0].TryGetProperty("clip_number", out _)),
            "Every scene needs at least one numbered clip.");
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "projects", "LoadSimBuster", "project.json")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        return null;
    }
}
