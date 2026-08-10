global using LoadSimScenario = PageToMovie.LoadSim.LoadSimScenarioName;

using System.Text.Json.Serialization;

namespace PageToMovie.LoadSim;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimScenarioName
{
    Browse,
    Play,
    Gen,
    Remux,
    Mixed,
    Soak,
    Stress
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimVirtualUserState
{
    Idle,
    Initializing,
    Ready,
    Running,
    Thinking,
    Waiting,
    Stopped,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimMetricType
{
    Latency,
    Throughput,
    ErrorRate,
    RequestCount,
    ActiveUsers,
    CpuUsage,
    MemoryUsage
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenchmarkSuiteName
{
    ScreenplayAdaptation,
    ClassifierBenchmark,
    SilentBeatEval,
    PerformanceSoak,
    LiveApiBenchmark
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassifierGroundTruthLabel
{
    Action,
    Dialogue,
    Transition,
    Parenthetical,
    SceneHeading,
    Unclassified
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalScoreMetric
{
    Accuracy,
    Precision,
    Recall,
    F1Score,
    LatencyMs,
    TokenCount,
    Bleurt,
    Rouge,
    ExactMatch
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalModelProvider
{
    OpenAI,
    Anthropic,
    Google,
    xAI,
    Local,
    Mock
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FixtureDocumentType
{
    Fountain,
    Pdf,
    Text,
    Json,
    Audio,
    Image
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestExecutionEnvironment
{
    Local,
    CI,
    Staging,
    Production
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildConfigurationMode
{
    Debug,
    Release,
    Test,
    Benchmark
}

public static class LoadSimLayerEnumExtensions
{
    public static string ToApiString(this LoadSimScenarioName scenario) => scenario switch
    {
        LoadSimScenarioName.Browse => "browse",
        LoadSimScenarioName.Play => "play",
        LoadSimScenarioName.Gen => "gen",
        LoadSimScenarioName.Remux => "remux",
        LoadSimScenarioName.Mixed => "mixed",
        LoadSimScenarioName.Soak => "soak",
        LoadSimScenarioName.Stress => "stress",
        _ => "mixed"
    };

    public static LoadSimScenarioName ParseScenarioName(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "browse" => LoadSimScenarioName.Browse,
            "play" => LoadSimScenarioName.Play,
            "gen" => LoadSimScenarioName.Gen,
            "remux" => LoadSimScenarioName.Remux,
            "soak" => LoadSimScenarioName.Soak,
            "stress" => LoadSimScenarioName.Stress,
            _ => LoadSimScenarioName.Mixed
        };

    public static string ToApiString(this LoadSimVirtualUserState state) => state switch
    {
        LoadSimVirtualUserState.Idle => "idle",
        LoadSimVirtualUserState.Initializing => "initializing",
        LoadSimVirtualUserState.Ready => "ready",
        LoadSimVirtualUserState.Running => "running",
        LoadSimVirtualUserState.Thinking => "thinking",
        LoadSimVirtualUserState.Waiting => "waiting",
        LoadSimVirtualUserState.Stopped => "stopped",
        LoadSimVirtualUserState.Failed => "failed",
        _ => "idle"
    };

    public static string ToMetricName(this LoadSimMetricType metric) => metric switch
    {
        LoadSimMetricType.Latency => "latency_ms",
        LoadSimMetricType.Throughput => "req_per_sec",
        LoadSimMetricType.ErrorRate => "error_rate",
        LoadSimMetricType.RequestCount => "total_requests",
        LoadSimMetricType.ActiveUsers => "active_users",
        LoadSimMetricType.CpuUsage => "cpu_percent",
        LoadSimMetricType.MemoryUsage => "memory_mb",
        _ => "latency_ms"
    };

    public static string ToSuiteName(this BenchmarkSuiteName suite) => suite switch
    {
        BenchmarkSuiteName.ScreenplayAdaptation => "screenplay_adaptation",
        BenchmarkSuiteName.ClassifierBenchmark => "classifier_benchmark",
        BenchmarkSuiteName.SilentBeatEval => "silent_beat_eval",
        BenchmarkSuiteName.PerformanceSoak => "performance_soak",
        BenchmarkSuiteName.LiveApiBenchmark => "live_api_benchmark",
        _ => "classifier_benchmark"
    };

    public static string ToLabelName(this ClassifierGroundTruthLabel label) => label switch
    {
        ClassifierGroundTruthLabel.Action => "action",
        ClassifierGroundTruthLabel.Dialogue => "dialogue",
        ClassifierGroundTruthLabel.Transition => "transition",
        ClassifierGroundTruthLabel.Parenthetical => "parenthetical",
        ClassifierGroundTruthLabel.SceneHeading => "scene_heading",
        ClassifierGroundTruthLabel.Unclassified => "unclassified",
        _ => "unclassified"
    };
}
