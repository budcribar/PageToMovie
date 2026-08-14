using System.Text.Json.Serialization;
using PageToMovie.Core.Models;

namespace ClassifierBenchmarks;

public sealed class RunConfig
{
    public string ProjectId { get; set; } = "The_Jungle_Book";
    public List<string> Tasks { get; set; } = new() { "ambient_sfx" };
    public List<string> Models { get; set; } = new()
    {
        SupportedModelCatalog.RequireDefaultModelIdForCapability(ModelCapability.Chat)
    };
    public List<string> Prompts { get; set; } = new() { "v1_product" };
    /// <summary>One or more temperatures to matrix (default 0).</summary>
    public List<double> Temperatures { get; set; } = new() { 0 };
    public string? Note { get; set; }

    /// <summary>Primary/first temperature (compat).</summary>
    public double Temperature => Temperatures.Count > 0 ? Temperatures[0] : 0;
}

public sealed class BenchmarkRun
{
    public string RunId { get; set; } = "";
    public string Utc { get; set; } = "";
    public string Schema { get; set; } = "classifier_benchmark_run.v1";
    public RunConfig Config { get; set; } = new();
    public string RepoRoot { get; set; } = "";
    public List<TaskResult> Results { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class TaskResult
{
    public string Task { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Model { get; set; } = "";
    public string PromptId { get; set; } = "";
    public string PromptLabel { get; set; } = "";
    public string PromptHash { get; set; } = "";
    public double Temperature { get; set; }
    public bool CuratedGold { get; set; }
    public int SampleCount { get; set; }
    public string Metric { get; set; } = "";
    public double BaselineScore { get; set; }
    public double AiScore { get; set; }
    public string Winner { get; set; } = "";
    public long LatencyMs { get; set; }
    public int AiParseHits { get; set; }
    public string? Note { get; set; }
    public List<SampleScore> Samples { get; set; } = new();
}

public sealed class SampleScore
{
    public string Id { get; set; } = "";
    public string Visual { get; set; } = "";
    public string GoldAmbient { get; set; } = "";
    public string GoldSfx { get; set; } = "";
    public string GoldLabel { get; set; } = "";
    public string BaselineAmbient { get; set; } = "";
    public string BaselineSfx { get; set; } = "";
    public string BaselineLabel { get; set; } = "";
    public string AiAmbient { get; set; } = "";
    public string AiSfx { get; set; } = "";
    public string AiLabel { get; set; } = "";
    public double BaselineScore { get; set; }
    public double AiScore { get; set; }
}

public sealed class HistoryIndex
{
    public string Schema { get; set; } = "classifier_benchmark_history.v1";
    public List<HistoryEntry> Runs { get; set; } = new();
}

public sealed class HistoryEntry
{
    public string RunId { get; set; } = "";
    public string Utc { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public List<string> Tasks { get; set; } = new();
    public List<string> Models { get; set; } = new();
    public List<string> Prompts { get; set; } = new();
    public string SummaryRel { get; set; } = "";
    public string? Note { get; set; }
    public List<HistoryScore> Scores { get; set; } = new();
}

public sealed class HistoryScore
{
    public string Task { get; set; } = "";
    public string Model { get; set; } = "";
    public string PromptId { get; set; } = "";
    public double Temperature { get; set; }
    public string Metric { get; set; } = "";
    public double BaselineScore { get; set; }
    public double AiScore { get; set; }
    public string Winner { get; set; } = "";
    public int SampleCount { get; set; }
    public bool CuratedGold { get; set; }
}

public static class JsonDefaults
{
    public static System.Text.Json.JsonSerializerOptions Pretty { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    public static System.Text.Json.JsonSerializerOptions Flexible { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
}
