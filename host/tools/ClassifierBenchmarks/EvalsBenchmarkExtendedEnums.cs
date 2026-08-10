using System.Text.Json.Serialization;

namespace ClassifierBenchmarks;

/// <summary>
/// Extended benchmark suite categories for classifier and evaluation runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenchmarkSuiteKindType
{
    ScreenplayAdaptation,
    ClassifierBenchmark,
    SilentBeatEval,
    PerformanceSoak,
    LiveApiBenchmark,
    ModelAccuracyMatrix
}

/// <summary>
/// Extended ground-truth annotation classifications for screenplay elements.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassifierGroundTruthKind
{
    Action,
    Dialogue,
    Transition,
    Parenthetical,
    SceneHeading,
    Unclassified,
    CharacterName,
    ShotDescription
}

/// <summary>
/// Provider categories for model evaluation runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalModelProviderCategory
{
    OpenAI,
    Anthropic,
    Google,
    xAI,
    Local,
    Mock,
    CustomApi
}

/// <summary>
/// Metric types evaluated during benchmark performance scoring.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalMetricTypeKind
{
    Accuracy,
    Precision,
    Recall,
    F1Score,
    LatencyMs,
    TokensPerSecond,
    CostPerRun,
    BLEUScore,
    ROUGEScore
}

/// <summary>
/// Input fixture artifact types used during test runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestFixtureTypeKind
{
    BookSample,
    FountainScript,
    JsonBlueprint,
    MediaAudio,
    MediaVideo,
    PromptTemplate,
    GroundTruthAnnotation
}

/// <summary>
/// Target execution environments for test runner runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestRunnerEnvironmentKind
{
    LocalMachine,
    CiCdPipeline,
    DockerContainer,
    CloudAgent,
    IsolatedSandbox
}

/// <summary>
/// Software build configurations for test execution environments.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildConfigurationKind
{
    Debug,
    Release,
    Benchmark,
    LiveTest,
    CodeCoverage
}

/// <summary>
/// Categorization flags for filtering test suite runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestFilterCategoryKind
{
    Unit,
    Integration,
    LiveApi,
    Performance,
    Regression,
    Smoke,
    E2E
}

/// <summary>
/// Assertion comparison operators for test evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssertionComparisonKind
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    MatchesRegex
}

/// <summary>
/// Behavioral modes for mock services in evaluation runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MockBehaviorModeKind
{
    Strict,
    Loose,
    ReplaySavedJson,
    ThrowException,
    Passthrough
}

/// <summary>
/// Evaluation test result outcomes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestResultOutcomeKind
{
    Passed,
    Failed,
    Skipped,
    Inconclusive,
    Errored,
    TimedOut
}

/// <summary>
/// Supported output report formats for benchmark outputs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportOutputFormatKind
{
    ConsoleTable,
    JsonFile,
    MarkdownSummary,
    CsvExport,
    HtmlDashboard,
    XmlReport
}

/// <summary>
/// Dataset source kinds for ground-truth evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroundTruthDatasetKind
{
    BusterV1,
    JungleBookV2,
    TellTaleHeartV4,
    CustomDataset,
    SyntheticBenchmark
}

/// <summary>
/// Sampling temperature presets for evaluation models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalModelTemperatureKind
{
    ZeroDeterministic,
    LowCreative,
    MediumBalanced,
    HighCreative,
    TopPSampling
}

/// <summary>
/// Prompting strategy kinds for LLM benchmarks.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalPromptStrategyKind
{
    ZeroShot,
    FewShot,
    ChainOfThought,
    RolePrompted,
    SystemStructured,
    SelfConsistency
}

/// <summary>
/// Comparison logic for score threshold validation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScoreThresholdComparisonKind
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal,
    WithinRange
}

/// <summary>
/// Serialization formats for test fixtures.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestFixtureFormatKind
{
    Utf8Text,
    BinaryStream,
    JsonModel,
    XmlPayload,
    YamlConfig
}

/// <summary>
/// Flag modes governing live paid API endpoint calls in tests.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LiveApiTestFlagKind
{
    Disabled,
    EnabledExplicitOnly,
    AutoIfKeyPresent,
    ForceMocking
}

/// <summary>
/// Execution pass modes for evaluation benchmarks.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenchmarkRunModeKind
{
    SinglePass,
    MultiIterAverage,
    StressMatrix,
    Comparative,
    WarmupAndBenchmark
}

/// <summary>
/// Report summary section categories for evaluation outputs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalReportSummarySectionKind
{
    Overview,
    ModelComparison,
    TaskAccuracyBreakdown,
    LatencyDistribution,
    FailureAnalysis,
    Recommendations,
    CostBreakdown
}

/// <summary>
/// Extension methods for Evals and Benchmark extended enums string formatting and parsing.
/// </summary>
public static class EvalsBenchmarkExtendedEnumExtensions
{
    public static string ToApiString(this BenchmarkSuiteKindType suite) => suite switch
    {
        BenchmarkSuiteKindType.ScreenplayAdaptation => "screenplay_adaptation",
        BenchmarkSuiteKindType.ClassifierBenchmark => "classifier_benchmark",
        BenchmarkSuiteKindType.SilentBeatEval => "silent_beat_eval",
        BenchmarkSuiteKindType.PerformanceSoak => "performance_soak",
        BenchmarkSuiteKindType.LiveApiBenchmark => "live_api_benchmark",
        BenchmarkSuiteKindType.ModelAccuracyMatrix => "accuracy_matrix",
        _ => "classifier_benchmark"
    };

    public static BenchmarkSuiteKindType ParseBenchmarkSuiteKindType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "screenplay_adaptation" or "adaptation" => BenchmarkSuiteKindType.ScreenplayAdaptation,
            "classifier_benchmark" or "classifier" => BenchmarkSuiteKindType.ClassifierBenchmark,
            "silent_beat_eval" or "silent_beat" => BenchmarkSuiteKindType.SilentBeatEval,
            "performance_soak" or "soak" => BenchmarkSuiteKindType.PerformanceSoak,
            "live_api_benchmark" or "live_api" => BenchmarkSuiteKindType.LiveApiBenchmark,
            "accuracy_matrix" or "model_accuracy" => BenchmarkSuiteKindType.ModelAccuracyMatrix,
            _ => BenchmarkSuiteKindType.ClassifierBenchmark
        };

    public static BenchmarkSuiteKindType ToBenchmarkSuiteKindType(this string? value) => ParseBenchmarkSuiteKindType(value);

    public static string ToApiString(this ClassifierGroundTruthKind tag) => tag switch
    {
        ClassifierGroundTruthKind.Action => "action",
        ClassifierGroundTruthKind.Dialogue => "dialogue",
        ClassifierGroundTruthKind.Transition => "transition",
        ClassifierGroundTruthKind.Parenthetical => "parenthetical",
        ClassifierGroundTruthKind.SceneHeading => "scene_heading",
        ClassifierGroundTruthKind.Unclassified => "unclassified",
        ClassifierGroundTruthKind.CharacterName => "character_name",
        ClassifierGroundTruthKind.ShotDescription => "shot_description",
        _ => "unclassified"
    };

    public static ClassifierGroundTruthKind ParseClassifierGroundTruthKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "action" => ClassifierGroundTruthKind.Action,
            "dialogue" => ClassifierGroundTruthKind.Dialogue,
            "transition" => ClassifierGroundTruthKind.Transition,
            "parenthetical" => ClassifierGroundTruthKind.Parenthetical,
            "scene_heading" or "heading" => ClassifierGroundTruthKind.SceneHeading,
            "character_name" or "character" => ClassifierGroundTruthKind.CharacterName,
            "shot_description" or "shot" => ClassifierGroundTruthKind.ShotDescription,
            "unclassified" or _ => ClassifierGroundTruthKind.Unclassified
        };

    public static ClassifierGroundTruthKind ToClassifierGroundTruthKind(this string? value) => ParseClassifierGroundTruthKind(value);

    public static string ToApiString(this EvalModelProviderCategory cat) => cat switch
    {
        EvalModelProviderCategory.OpenAI => "openai",
        EvalModelProviderCategory.Anthropic => "anthropic",
        EvalModelProviderCategory.Google => "google",
        EvalModelProviderCategory.xAI => "xai",
        EvalModelProviderCategory.Local => "local",
        EvalModelProviderCategory.Mock => "mock",
        EvalModelProviderCategory.CustomApi => "custom",
        _ => "mock"
    };

    public static EvalModelProviderCategory ParseEvalModelProviderCategory(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "openai" => EvalModelProviderCategory.OpenAI,
            "anthropic" => EvalModelProviderCategory.Anthropic,
            "google" => EvalModelProviderCategory.Google,
            "xai" => EvalModelProviderCategory.xAI,
            "local" => EvalModelProviderCategory.Local,
            "mock" => EvalModelProviderCategory.Mock,
            "custom" or "custom_api" => EvalModelProviderCategory.CustomApi,
            _ => EvalModelProviderCategory.Mock
        };

    public static EvalModelProviderCategory ToEvalModelProviderCategory(this string? value) => ParseEvalModelProviderCategory(value);

    public static string ToApiString(this EvalMetricTypeKind metric) => metric switch
    {
        EvalMetricTypeKind.Accuracy => "accuracy",
        EvalMetricTypeKind.Precision => "precision",
        EvalMetricTypeKind.Recall => "recall",
        EvalMetricTypeKind.F1Score => "f1_score",
        EvalMetricTypeKind.LatencyMs => "latency_ms",
        EvalMetricTypeKind.TokensPerSecond => "tokens_per_sec",
        EvalMetricTypeKind.CostPerRun => "cost_per_run",
        EvalMetricTypeKind.BLEUScore => "bleu_score",
        EvalMetricTypeKind.ROUGEScore => "rouge_score",
        _ => "accuracy"
    };

    public static EvalMetricTypeKind ParseEvalMetricTypeKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "accuracy" => EvalMetricTypeKind.Accuracy,
            "precision" => EvalMetricTypeKind.Precision,
            "recall" => EvalMetricTypeKind.Recall,
            "f1_score" or "f1" => EvalMetricTypeKind.F1Score,
            "latency_ms" or "latency" => EvalMetricTypeKind.LatencyMs,
            "tokens_per_sec" or "tps" => EvalMetricTypeKind.TokensPerSecond,
            "cost_per_run" or "cost" => EvalMetricTypeKind.CostPerRun,
            "bleu_score" or "bleu" => EvalMetricTypeKind.BLEUScore,
            "rouge_score" or "rouge" => EvalMetricTypeKind.ROUGEScore,
            _ => EvalMetricTypeKind.Accuracy
        };

    public static EvalMetricTypeKind ToEvalMetricTypeKind(this string? value) => ParseEvalMetricTypeKind(value);

    public static string ToApiString(this TestFixtureTypeKind fixture) => fixture switch
    {
        TestFixtureTypeKind.BookSample => "book_sample",
        TestFixtureTypeKind.FountainScript => "fountain_script",
        TestFixtureTypeKind.JsonBlueprint => "json_blueprint",
        TestFixtureTypeKind.MediaAudio => "media_audio",
        TestFixtureTypeKind.MediaVideo => "media_video",
        TestFixtureTypeKind.PromptTemplate => "prompt_template",
        TestFixtureTypeKind.GroundTruthAnnotation => "ground_truth",
        _ => "book_sample"
    };

    public static TestFixtureTypeKind ParseTestFixtureTypeKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "book_sample" or "book" => TestFixtureTypeKind.BookSample,
            "fountain_script" or "fountain" => TestFixtureTypeKind.FountainScript,
            "json_blueprint" or "blueprint" => TestFixtureTypeKind.JsonBlueprint,
            "media_audio" or "audio" => TestFixtureTypeKind.MediaAudio,
            "media_video" or "video" => TestFixtureTypeKind.MediaVideo,
            "prompt_template" or "prompt" => TestFixtureTypeKind.PromptTemplate,
            "ground_truth" or "annotation" => TestFixtureTypeKind.GroundTruthAnnotation,
            _ => TestFixtureTypeKind.BookSample
        };

    public static TestFixtureTypeKind ToTestFixtureTypeKind(this string? value) => ParseTestFixtureTypeKind(value);

    public static string ToApiString(this TestRunnerEnvironmentKind env) => env switch
    {
        TestRunnerEnvironmentKind.LocalMachine => "local",
        TestRunnerEnvironmentKind.CiCdPipeline => "ci_cd",
        TestRunnerEnvironmentKind.DockerContainer => "docker",
        TestRunnerEnvironmentKind.CloudAgent => "cloud_agent",
        TestRunnerEnvironmentKind.IsolatedSandbox => "sandbox",
        _ => "local"
    };

    public static TestRunnerEnvironmentKind ParseTestRunnerEnvironmentKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "local" or "local_machine" => TestRunnerEnvironmentKind.LocalMachine,
            "ci_cd" or "cicd" => TestRunnerEnvironmentKind.CiCdPipeline,
            "docker" or "docker_container" => TestRunnerEnvironmentKind.DockerContainer,
            "cloud_agent" or "cloud" => TestRunnerEnvironmentKind.CloudAgent,
            "sandbox" or "isolated_sandbox" => TestRunnerEnvironmentKind.IsolatedSandbox,
            _ => TestRunnerEnvironmentKind.LocalMachine
        };

    public static TestRunnerEnvironmentKind ToTestRunnerEnvironmentKind(this string? value) => ParseTestRunnerEnvironmentKind(value);

    public static string ToApiString(this BuildConfigurationKind config) => config switch
    {
        BuildConfigurationKind.Debug => "debug",
        BuildConfigurationKind.Release => "release",
        BuildConfigurationKind.Benchmark => "benchmark",
        BuildConfigurationKind.LiveTest => "live_test",
        BuildConfigurationKind.CodeCoverage => "coverage",
        _ => "release"
    };

    public static BuildConfigurationKind ParseBuildConfigurationKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "debug" => BuildConfigurationKind.Debug,
            "release" => BuildConfigurationKind.Release,
            "benchmark" => BuildConfigurationKind.Benchmark,
            "live_test" or "livetest" => BuildConfigurationKind.LiveTest,
            "coverage" or "code_coverage" => BuildConfigurationKind.CodeCoverage,
            _ => BuildConfigurationKind.Release
        };

    public static BuildConfigurationKind ToBuildConfigurationKind(this string? value) => ParseBuildConfigurationKind(value);

    public static string ToApiString(this TestFilterCategoryKind category) => category switch
    {
        TestFilterCategoryKind.Unit => "unit",
        TestFilterCategoryKind.Integration => "integration",
        TestFilterCategoryKind.LiveApi => "live_api",
        TestFilterCategoryKind.Performance => "performance",
        TestFilterCategoryKind.Regression => "regression",
        TestFilterCategoryKind.Smoke => "smoke",
        TestFilterCategoryKind.E2E => "e2e",
        _ => "unit"
    };

    public static TestFilterCategoryKind ParseTestFilterCategoryKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "unit" => TestFilterCategoryKind.Unit,
            "integration" => TestFilterCategoryKind.Integration,
            "live_api" or "liveapi" => TestFilterCategoryKind.LiveApi,
            "performance" or "perf" => TestFilterCategoryKind.Performance,
            "regression" => TestFilterCategoryKind.Regression,
            "smoke" => TestFilterCategoryKind.Smoke,
            "e2e" or "end_to_end" => TestFilterCategoryKind.E2E,
            _ => TestFilterCategoryKind.Unit
        };

    public static TestFilterCategoryKind ToTestFilterCategoryKind(this string? value) => ParseTestFilterCategoryKind(value);

    public static string ToApiString(this AssertionComparisonKind cmp) => cmp switch
    {
        AssertionComparisonKind.Equal => "equal",
        AssertionComparisonKind.NotEqual => "not_equal",
        AssertionComparisonKind.GreaterThan => "greater_than",
        AssertionComparisonKind.GreaterThanOrEqual => "greater_than_or_equal",
        AssertionComparisonKind.LessThan => "less_than",
        AssertionComparisonKind.LessThanOrEqual => "less_than_or_equal",
        AssertionComparisonKind.Contains => "contains",
        AssertionComparisonKind.MatchesRegex => "matches_regex",
        _ => "equal"
    };

    public static AssertionComparisonKind ParseAssertionComparisonKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "equal" or "eq" => AssertionComparisonKind.Equal,
            "not_equal" or "neq" => AssertionComparisonKind.NotEqual,
            "greater_than" or "gt" => AssertionComparisonKind.GreaterThan,
            "greater_than_or_equal" or "gte" => AssertionComparisonKind.GreaterThanOrEqual,
            "less_than" or "lt" => AssertionComparisonKind.LessThan,
            "less_than_or_equal" or "lte" => AssertionComparisonKind.LessThanOrEqual,
            "contains" => AssertionComparisonKind.Contains,
            "matches_regex" or "regex" => AssertionComparisonKind.MatchesRegex,
            _ => AssertionComparisonKind.Equal
        };

    public static AssertionComparisonKind ToAssertionComparisonKind(this string? value) => ParseAssertionComparisonKind(value);

    public static string ToApiString(this MockBehaviorModeKind mode) => mode switch
    {
        MockBehaviorModeKind.Strict => "strict",
        MockBehaviorModeKind.Loose => "loose",
        MockBehaviorModeKind.ReplaySavedJson => "replay_json",
        MockBehaviorModeKind.ThrowException => "throw_exception",
        MockBehaviorModeKind.Passthrough => "passthrough",
        _ => "strict"
    };

    public static MockBehaviorModeKind ParseMockBehaviorModeKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "strict" => MockBehaviorModeKind.Strict,
            "loose" => MockBehaviorModeKind.Loose,
            "replay_json" or "replay" => MockBehaviorModeKind.ReplaySavedJson,
            "throw_exception" or "throw" => MockBehaviorModeKind.ThrowException,
            "passthrough" => MockBehaviorModeKind.Passthrough,
            _ => MockBehaviorModeKind.Strict
        };

    public static MockBehaviorModeKind ToMockBehaviorModeKind(this string? value) => ParseMockBehaviorModeKind(value);

    public static string ToApiString(this TestResultOutcomeKind outcome) => outcome switch
    {
        TestResultOutcomeKind.Passed => "passed",
        TestResultOutcomeKind.Failed => "failed",
        TestResultOutcomeKind.Skipped => "skipped",
        TestResultOutcomeKind.Inconclusive => "inconclusive",
        TestResultOutcomeKind.Errored => "errored",
        TestResultOutcomeKind.TimedOut => "timed_out",
        _ => "skipped"
    };

    public static TestResultOutcomeKind ParseTestResultOutcomeKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "passed" or "pass" => TestResultOutcomeKind.Passed,
            "failed" or "fail" => TestResultOutcomeKind.Failed,
            "skipped" or "skip" => TestResultOutcomeKind.Skipped,
            "inconclusive" => TestResultOutcomeKind.Inconclusive,
            "errored" or "error" => TestResultOutcomeKind.Errored,
            "timed_out" or "timeout" => TestResultOutcomeKind.TimedOut,
            _ => TestResultOutcomeKind.Skipped
        };

    public static TestResultOutcomeKind ToTestResultOutcomeKind(this string? value) => ParseTestResultOutcomeKind(value);

    public static string ToApiString(this ReportOutputFormatKind format) => format switch
    {
        ReportOutputFormatKind.ConsoleTable => "console",
        ReportOutputFormatKind.JsonFile => "json",
        ReportOutputFormatKind.MarkdownSummary => "markdown",
        ReportOutputFormatKind.CsvExport => "csv",
        ReportOutputFormatKind.HtmlDashboard => "html",
        ReportOutputFormatKind.XmlReport => "xml",
        _ => "console"
    };

    public static ReportOutputFormatKind ParseReportOutputFormatKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "console" or "console_table" => ReportOutputFormatKind.ConsoleTable,
            "json" or "json_file" => ReportOutputFormatKind.JsonFile,
            "markdown" or "md" => ReportOutputFormatKind.MarkdownSummary,
            "csv" => ReportOutputFormatKind.CsvExport,
            "html" => ReportOutputFormatKind.HtmlDashboard,
            "xml" => ReportOutputFormatKind.XmlReport,
            _ => ReportOutputFormatKind.ConsoleTable
        };

    public static ReportOutputFormatKind ToReportOutputFormatKind(this string? value) => ParseReportOutputFormatKind(value);

    public static string ToApiString(this GroundTruthDatasetKind dataset) => dataset switch
    {
        GroundTruthDatasetKind.BusterV1 => "buster_v1",
        GroundTruthDatasetKind.JungleBookV2 => "jungle_book_v2",
        GroundTruthDatasetKind.TellTaleHeartV4 => "telltale_heart_v4",
        GroundTruthDatasetKind.CustomDataset => "custom",
        GroundTruthDatasetKind.SyntheticBenchmark => "synthetic",
        _ => "custom"
    };

    public static GroundTruthDatasetKind ParseGroundTruthDatasetKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "buster_v1" or "buster" => GroundTruthDatasetKind.BusterV1,
            "jungle_book_v2" or "jungle_book" => GroundTruthDatasetKind.JungleBookV2,
            "telltale_heart_v4" or "telltale_heart" => GroundTruthDatasetKind.TellTaleHeartV4,
            "custom" or "custom_dataset" => GroundTruthDatasetKind.CustomDataset,
            "synthetic" or "synthetic_benchmark" => GroundTruthDatasetKind.SyntheticBenchmark,
            _ => GroundTruthDatasetKind.CustomDataset
        };

    public static GroundTruthDatasetKind ToGroundTruthDatasetKind(this string? value) => ParseGroundTruthDatasetKind(value);

    public static string ToApiString(this EvalModelTemperatureKind temp) => temp switch
    {
        EvalModelTemperatureKind.ZeroDeterministic => "0.0",
        EvalModelTemperatureKind.LowCreative => "0.2",
        EvalModelTemperatureKind.MediumBalanced => "0.7",
        EvalModelTemperatureKind.HighCreative => "1.0",
        EvalModelTemperatureKind.TopPSampling => "top_p",
        _ => "0.0"
    };

    public static EvalModelTemperatureKind ParseEvalModelTemperatureKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "0.0" or "zero" or "deterministic" => EvalModelTemperatureKind.ZeroDeterministic,
            "0.2" or "low" => EvalModelTemperatureKind.LowCreative,
            "0.7" or "medium" => EvalModelTemperatureKind.MediumBalanced,
            "1.0" or "high" => EvalModelTemperatureKind.HighCreative,
            "top_p" or "topp" => EvalModelTemperatureKind.TopPSampling,
            _ => EvalModelTemperatureKind.ZeroDeterministic
        };

    public static EvalModelTemperatureKind ToEvalModelTemperatureKind(this string? value) => ParseEvalModelTemperatureKind(value);

    public static string ToApiString(this EvalPromptStrategyKind strategy) => strategy switch
    {
        EvalPromptStrategyKind.ZeroShot => "zero_shot",
        EvalPromptStrategyKind.FewShot => "few_shot",
        EvalPromptStrategyKind.ChainOfThought => "chain_of_thought",
        EvalPromptStrategyKind.RolePrompted => "role_prompted",
        EvalPromptStrategyKind.SystemStructured => "system_structured",
        EvalPromptStrategyKind.SelfConsistency => "self_consistency",
        _ => "zero_shot"
    };

    public static EvalPromptStrategyKind ParseEvalPromptStrategyKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "zero_shot" => EvalPromptStrategyKind.ZeroShot,
            "few_shot" => EvalPromptStrategyKind.FewShot,
            "chain_of_thought" or "cot" => EvalPromptStrategyKind.ChainOfThought,
            "role_prompted" => EvalPromptStrategyKind.RolePrompted,
            "system_structured" => EvalPromptStrategyKind.SystemStructured,
            "self_consistency" => EvalPromptStrategyKind.SelfConsistency,
            _ => EvalPromptStrategyKind.ZeroShot
        };

    public static EvalPromptStrategyKind ToEvalPromptStrategyKind(this string? value) => ParseEvalPromptStrategyKind(value);

    public static string ToApiString(this ScoreThresholdComparisonKind cmp) => cmp switch
    {
        ScoreThresholdComparisonKind.GreaterThan => "gt",
        ScoreThresholdComparisonKind.GreaterThanOrEqual => "gte",
        ScoreThresholdComparisonKind.LessThan => "lt",
        ScoreThresholdComparisonKind.LessThanOrEqual => "lte",
        ScoreThresholdComparisonKind.Equal => "eq",
        ScoreThresholdComparisonKind.WithinRange => "range",
        _ => "gte"
    };

    public static ScoreThresholdComparisonKind ParseScoreThresholdComparisonKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "gt" or "greater_than" => ScoreThresholdComparisonKind.GreaterThan,
            "gte" or "greater_than_or_equal" => ScoreThresholdComparisonKind.GreaterThanOrEqual,
            "lt" or "less_than" => ScoreThresholdComparisonKind.LessThan,
            "lte" or "less_than_or_equal" => ScoreThresholdComparisonKind.LessThanOrEqual,
            "eq" or "equal" => ScoreThresholdComparisonKind.Equal,
            "range" or "within_range" => ScoreThresholdComparisonKind.WithinRange,
            _ => ScoreThresholdComparisonKind.GreaterThanOrEqual
        };

    public static ScoreThresholdComparisonKind ToScoreThresholdComparisonKind(this string? value) => ParseScoreThresholdComparisonKind(value);

    public static string ToApiString(this TestFixtureFormatKind format) => format switch
    {
        TestFixtureFormatKind.Utf8Text => "utf8_text",
        TestFixtureFormatKind.BinaryStream => "binary",
        TestFixtureFormatKind.JsonModel => "json",
        TestFixtureFormatKind.XmlPayload => "xml",
        TestFixtureFormatKind.YamlConfig => "yaml",
        _ => "utf8_text"
    };

    public static TestFixtureFormatKind ParseTestFixtureFormatKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "utf8_text" or "text" => TestFixtureFormatKind.Utf8Text,
            "binary" or "binary_stream" => TestFixtureFormatKind.BinaryStream,
            "json" or "json_model" => TestFixtureFormatKind.JsonModel,
            "xml" or "xml_payload" => TestFixtureFormatKind.XmlPayload,
            "yaml" or "yaml_config" => TestFixtureFormatKind.YamlConfig,
            _ => TestFixtureFormatKind.Utf8Text
        };

    public static TestFixtureFormatKind ToTestFixtureFormatKind(this string? value) => ParseTestFixtureFormatKind(value);

    public static string ToApiString(this LiveApiTestFlagKind flag) => flag switch
    {
        LiveApiTestFlagKind.Disabled => "disabled",
        LiveApiTestFlagKind.EnabledExplicitOnly => "enabled_explicit",
        LiveApiTestFlagKind.AutoIfKeyPresent => "auto_if_key_present",
        LiveApiTestFlagKind.ForceMocking => "force_mocking",
        _ => "disabled"
    };

    public static LiveApiTestFlagKind ParseLiveApiTestFlagKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "disabled" => LiveApiTestFlagKind.Disabled,
            "enabled_explicit" or "explicit" => LiveApiTestFlagKind.EnabledExplicitOnly,
            "auto_if_key_present" or "auto" => LiveApiTestFlagKind.AutoIfKeyPresent,
            "force_mocking" or "mock" => LiveApiTestFlagKind.ForceMocking,
            _ => LiveApiTestFlagKind.Disabled
        };

    public static LiveApiTestFlagKind ToLiveApiTestFlagKind(this string? value) => ParseLiveApiTestFlagKind(value);

    public static string ToApiString(this BenchmarkRunModeKind mode) => mode switch
    {
        BenchmarkRunModeKind.SinglePass => "single_pass",
        BenchmarkRunModeKind.MultiIterAverage => "multi_iter_average",
        BenchmarkRunModeKind.StressMatrix => "stress_matrix",
        BenchmarkRunModeKind.Comparative => "comparative",
        BenchmarkRunModeKind.WarmupAndBenchmark => "warmup_benchmark",
        _ => "single_pass"
    };

    public static BenchmarkRunModeKind ParseBenchmarkRunModeKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "single_pass" => BenchmarkRunModeKind.SinglePass,
            "multi_iter_average" or "average" => BenchmarkRunModeKind.MultiIterAverage,
            "stress_matrix" or "matrix" => BenchmarkRunModeKind.StressMatrix,
            "comparative" => BenchmarkRunModeKind.Comparative,
            "warmup_benchmark" or "warmup" => BenchmarkRunModeKind.WarmupAndBenchmark,
            _ => BenchmarkRunModeKind.SinglePass
        };

    public static BenchmarkRunModeKind ToBenchmarkRunModeKind(this string? value) => ParseBenchmarkRunModeKind(value);

    public static string ToApiString(this EvalReportSummarySectionKind section) => section switch
    {
        EvalReportSummarySectionKind.Overview => "overview",
        EvalReportSummarySectionKind.ModelComparison => "model_comparison",
        EvalReportSummarySectionKind.TaskAccuracyBreakdown => "task_accuracy",
        EvalReportSummarySectionKind.LatencyDistribution => "latency_distribution",
        EvalReportSummarySectionKind.FailureAnalysis => "failure_analysis",
        EvalReportSummarySectionKind.Recommendations => "recommendations",
        EvalReportSummarySectionKind.CostBreakdown => "cost_breakdown",
        _ => "overview"
    };

    public static EvalReportSummarySectionKind ParseEvalReportSummarySectionKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "overview" => EvalReportSummarySectionKind.Overview,
            "model_comparison" or "models" => EvalReportSummarySectionKind.ModelComparison,
            "task_accuracy" or "accuracy" => EvalReportSummarySectionKind.TaskAccuracyBreakdown,
            "latency_distribution" or "latency" => EvalReportSummarySectionKind.LatencyDistribution,
            "failure_analysis" or "failures" => EvalReportSummarySectionKind.FailureAnalysis,
            "recommendations" => EvalReportSummarySectionKind.Recommendations,
            "cost_breakdown" or "cost" => EvalReportSummarySectionKind.CostBreakdown,
            _ => EvalReportSummarySectionKind.Overview
        };

    public static EvalReportSummarySectionKind ToEvalReportSummarySectionKind(this string? value) => ParseEvalReportSummarySectionKind(value);
}
