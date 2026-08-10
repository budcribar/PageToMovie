using System.Text.Json.Serialization;

namespace ClassifierBenchmarks;

/// <summary>
/// Suite category classification for evaluation benchmark runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenchmarkSuiteKind
{
    ScreenplayAdaptation,
    ClassifierBenchmark,
    SilentBeatEval,
    PerformanceSoak,
    LiveApiBenchmark
}

/// <summary>
/// Ground-truth label tags for classifying Fountain script elements.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassifierGroundTruthTag
{
    Action,
    Dialogue,
    Transition,
    Parenthetical,
    SceneHeading,
    Unclassified
}

/// <summary>
/// Model provider kind used for AI classification or vision evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalModelProviderKind
{
    OpenAI,
    Anthropic,
    Google,
    xAI,
    Local,
    Mock
}

/// <summary>
/// Metric types evaluated during benchmark comparison suites.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalMetricType
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

/// <summary>
/// Document or media asset type loaded as a test fixture.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestFixtureType
{
    FountainScript,
    PdfDocument,
    PlainText,
    JsonSchema,
    AudioSample,
    ImageSample
}

/// <summary>
/// Target execution environment for test runner runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestRunnerEnvironment
{
    LocalDev,
    ContinuousIntegration,
    StagingServer,
    ProductionBenchmark
}

/// <summary>
/// Build compilation configuration mode for benchmark binaries.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildConfigurationType
{
    Debug,
    Release,
    Test,
    Benchmark
}

/// <summary>
/// Categorical filter tags applied to select test suites.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestFilterCategory
{
    Unit,
    Integration,
    LiveApi,
    Performance,
    Regression,
    Smoke
}

/// <summary>
/// Comparison operator types for evaluation assertions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssertionComparisonType
{
    ExactEqual,
    Contains,
    StartsWith,
    EndsWith,
    RegexMatch,
    NumericTolerance,
    SemanticSimilarity
}

/// <summary>
/// Operational behavior mode for mock client providers during tests.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MockBehaviorMode
{
    Disabled,
    RecordAndReplay,
    StrictMock,
    LooseMock,
    PassthroughWithFallback
}

/// <summary>
/// Execution outcome state for individual test cases.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestResultOutcome
{
    Passed,
    Failed,
    Skipped,
    Inconclusive,
    Errored,
    TimedOut
}

/// <summary>
/// File format for benchmark evaluation output reports.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportOutputFormat
{
    ConsoleTable,
    JsonFile,
    MarkdownSummary,
    CsvExport,
    HtmlDashboard
}

/// <summary>
/// Named ground-truth evaluation datasets for classifier benchmarking.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroundTruthDatasetName
{
    BusterV1,
    JungleBookV2,
    TellTaleHeartV4,
    CustomDataset
}

/// <summary>
/// Sampling temperature presets for LLM evaluation requests.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalModelTemperature
{
    ZeroDeterministic,
    LowCreative,
    MediumBalanced,
    HighCreative
}

/// <summary>
/// Prompt engineering strategy used during classifier evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalPromptStrategy
{
    ZeroShot,
    FewShot,
    ChainOfThought,
    RolePrompted,
    SystemStructured
}

/// <summary>
/// Threshold comparison logic for pass/fail evaluation criteria.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScoreThresholdComparison
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal
}

/// <summary>
/// Storage encoding format for test fixture payloads.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestFixtureFormat
{
    Utf8Text,
    BinaryStream,
    JsonModel,
    XmlPayload
}

/// <summary>
/// Execution flag for enabling live network calls to AI provider endpoints.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LiveApiTestFlag
{
    Disabled,
    EnabledExplicitOnly,
    AutoIfKeyPresent
}

/// <summary>
/// Execution mode for running benchmark iteration suites.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenchmarkRunMode
{
    SinglePass,
    MultiIterAverage,
    StressMatrix,
    Comparative
}

/// <summary>
/// Section headings in generated evaluation benchmark summary reports.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalReportSummarySection
{
    Overview,
    ModelComparison,
    TaskAccuracyBreakdown,
    LatencyDistribution,
    FailureAnalysis,
    Recommendations
}

/// <summary>
/// Extension methods and string parsers for Evals Benchmark enums.
/// </summary>
public static class EvalsBenchmarkEnumExtensions
{
    public static string ToApiString(this BenchmarkSuiteKind kind) => kind switch
    {
        BenchmarkSuiteKind.ScreenplayAdaptation => "screenplay_adaptation",
        BenchmarkSuiteKind.ClassifierBenchmark => "classifier_benchmark",
        BenchmarkSuiteKind.SilentBeatEval => "silent_beat_eval",
        BenchmarkSuiteKind.PerformanceSoak => "performance_soak",
        BenchmarkSuiteKind.LiveApiBenchmark => "live_api_benchmark",
        _ => "classifier_benchmark"
    };

    public static BenchmarkSuiteKind ParseBenchmarkSuiteKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "screenplay_adaptation" => BenchmarkSuiteKind.ScreenplayAdaptation,
            "classifier_benchmark" or "classifier" => BenchmarkSuiteKind.ClassifierBenchmark,
            "silent_beat_eval" or "silent_beat" => BenchmarkSuiteKind.SilentBeatEval,
            "performance_soak" or "soak" => BenchmarkSuiteKind.PerformanceSoak,
            "live_api_benchmark" or "live_api" => BenchmarkSuiteKind.LiveApiBenchmark,
            _ => BenchmarkSuiteKind.ClassifierBenchmark
        };

    public static string ToApiString(this ClassifierGroundTruthTag tag) => tag switch
    {
        ClassifierGroundTruthTag.Action => "action",
        ClassifierGroundTruthTag.Dialogue => "dialogue",
        ClassifierGroundTruthTag.Transition => "transition",
        ClassifierGroundTruthTag.Parenthetical => "parenthetical",
        ClassifierGroundTruthTag.SceneHeading => "scene_heading",
        ClassifierGroundTruthTag.Unclassified => "unclassified",
        _ => "unclassified"
    };

    public static ClassifierGroundTruthTag ParseClassifierGroundTruthTag(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "action" => ClassifierGroundTruthTag.Action,
            "dialogue" => ClassifierGroundTruthTag.Dialogue,
            "transition" => ClassifierGroundTruthTag.Transition,
            "parenthetical" => ClassifierGroundTruthTag.Parenthetical,
            "scene_heading" or "heading" => ClassifierGroundTruthTag.SceneHeading,
            "unclassified" or "" => ClassifierGroundTruthTag.Unclassified,
            _ => ClassifierGroundTruthTag.Unclassified
        };

    public static string ToApiString(this EvalModelProviderKind provider) => provider switch
    {
        EvalModelProviderKind.OpenAI => "openai",
        EvalModelProviderKind.Anthropic => "anthropic",
        EvalModelProviderKind.Google => "google",
        EvalModelProviderKind.xAI => "xai",
        EvalModelProviderKind.Local => "local",
        EvalModelProviderKind.Mock => "mock",
        _ => "mock"
    };

    public static EvalModelProviderKind ParseEvalModelProviderKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "openai" => EvalModelProviderKind.OpenAI,
            "anthropic" => EvalModelProviderKind.Anthropic,
            "google" => EvalModelProviderKind.Google,
            "xai" => EvalModelProviderKind.xAI,
            "local" => EvalModelProviderKind.Local,
            "mock" => EvalModelProviderKind.Mock,
            _ => EvalModelProviderKind.Mock
        };

    public static string ToApiString(this EvalMetricType metric) => metric switch
    {
        EvalMetricType.Accuracy => "accuracy",
        EvalMetricType.Precision => "precision",
        EvalMetricType.Recall => "recall",
        EvalMetricType.F1Score => "f1_score",
        EvalMetricType.LatencyMs => "latency_ms",
        EvalMetricType.TokenCount => "token_count",
        EvalMetricType.Bleurt => "bleurt",
        EvalMetricType.Rouge => "rouge",
        EvalMetricType.ExactMatch => "exact_match",
        _ => "accuracy"
    };

    public static EvalMetricType ParseEvalMetricType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "accuracy" => EvalMetricType.Accuracy,
            "precision" => EvalMetricType.Precision,
            "recall" => EvalMetricType.Recall,
            "f1_score" or "f1" => EvalMetricType.F1Score,
            "latency_ms" or "latency" => EvalMetricType.LatencyMs,
            "token_count" or "tokens" => EvalMetricType.TokenCount,
            "bleurt" => EvalMetricType.Bleurt,
            "rouge" => EvalMetricType.Rouge,
            "exact_match" or "exact" => EvalMetricType.ExactMatch,
            _ => EvalMetricType.Accuracy
        };

    public static string ToApiString(this TestFixtureType fixture) => fixture switch
    {
        TestFixtureType.FountainScript => "fountain",
        TestFixtureType.PdfDocument => "pdf",
        TestFixtureType.PlainText => "text",
        TestFixtureType.JsonSchema => "json",
        TestFixtureType.AudioSample => "audio",
        TestFixtureType.ImageSample => "image",
        _ => "text"
    };

    public static TestFixtureType ParseTestFixtureType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "fountain" or "fountain_script" => TestFixtureType.FountainScript,
            "pdf" or "pdf_document" => TestFixtureType.PdfDocument,
            "text" or "plain_text" => TestFixtureType.PlainText,
            "json" or "json_schema" => TestFixtureType.JsonSchema,
            "audio" or "audio_sample" => TestFixtureType.AudioSample,
            "image" or "image_sample" => TestFixtureType.ImageSample,
            _ => TestFixtureType.PlainText
        };

    public static string ToApiString(this TestRunnerEnvironment env) => env switch
    {
        TestRunnerEnvironment.LocalDev => "local_dev",
        TestRunnerEnvironment.ContinuousIntegration => "ci",
        TestRunnerEnvironment.StagingServer => "staging",
        TestRunnerEnvironment.ProductionBenchmark => "production",
        _ => "local_dev"
    };

    public static TestRunnerEnvironment ParseTestRunnerEnvironment(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "local_dev" or "local" => TestRunnerEnvironment.LocalDev,
            "ci" or "continuous_integration" => TestRunnerEnvironment.ContinuousIntegration,
            "staging" or "staging_server" => TestRunnerEnvironment.StagingServer,
            "production" or "prod" => TestRunnerEnvironment.ProductionBenchmark,
            _ => TestRunnerEnvironment.LocalDev
        };

    public static string ToApiString(this BuildConfigurationType config) => config switch
    {
        BuildConfigurationType.Debug => "debug",
        BuildConfigurationType.Release => "release",
        BuildConfigurationType.Test => "test",
        BuildConfigurationType.Benchmark => "benchmark",
        _ => "release"
    };

    public static BuildConfigurationType ParseBuildConfigurationType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "debug" => BuildConfigurationType.Debug,
            "release" => BuildConfigurationType.Release,
            "test" => BuildConfigurationType.Test,
            "benchmark" => BuildConfigurationType.Benchmark,
            _ => BuildConfigurationType.Release
        };

    public static string ToApiString(this TestFilterCategory filter) => filter switch
    {
        TestFilterCategory.Unit => "unit",
        TestFilterCategory.Integration => "integration",
        TestFilterCategory.LiveApi => "live_api",
        TestFilterCategory.Performance => "performance",
        TestFilterCategory.Regression => "regression",
        TestFilterCategory.Smoke => "smoke",
        _ => "unit"
    };

    public static TestFilterCategory ParseTestFilterCategory(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "unit" => TestFilterCategory.Unit,
            "integration" => TestFilterCategory.Integration,
            "live_api" or "liveapi" => TestFilterCategory.LiveApi,
            "performance" => TestFilterCategory.Performance,
            "regression" => TestFilterCategory.Regression,
            "smoke" => TestFilterCategory.Smoke,
            _ => TestFilterCategory.Unit
        };

    public static string ToApiString(this AssertionComparisonType op) => op switch
    {
        AssertionComparisonType.ExactEqual => "exact_equal",
        AssertionComparisonType.Contains => "contains",
        AssertionComparisonType.StartsWith => "starts_with",
        AssertionComparisonType.EndsWith => "ends_with",
        AssertionComparisonType.RegexMatch => "regex_match",
        AssertionComparisonType.NumericTolerance => "numeric_tolerance",
        AssertionComparisonType.SemanticSimilarity => "semantic_similarity",
        _ => "exact_equal"
    };

    public static AssertionComparisonType ParseAssertionComparisonType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "exact_equal" or "equal" => AssertionComparisonType.ExactEqual,
            "contains" => AssertionComparisonType.Contains,
            "starts_with" => AssertionComparisonType.StartsWith,
            "ends_with" => AssertionComparisonType.EndsWith,
            "regex_match" or "regex" => AssertionComparisonType.RegexMatch,
            "numeric_tolerance" or "tolerance" => AssertionComparisonType.NumericTolerance,
            "semantic_similarity" or "semantic" => AssertionComparisonType.SemanticSimilarity,
            _ => AssertionComparisonType.ExactEqual
        };

    public static string ToApiString(this MockBehaviorMode mode) => mode switch
    {
        MockBehaviorMode.Disabled => "disabled",
        MockBehaviorMode.RecordAndReplay => "record_replay",
        MockBehaviorMode.StrictMock => "strict",
        MockBehaviorMode.LooseMock => "loose",
        MockBehaviorMode.PassthroughWithFallback => "passthrough",
        _ => "disabled"
    };

    public static MockBehaviorMode ParseMockBehaviorMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "disabled" => MockBehaviorMode.Disabled,
            "record_replay" or "record_and_replay" => MockBehaviorMode.RecordAndReplay,
            "strict" or "strict_mock" => MockBehaviorMode.StrictMock,
            "loose" or "loose_mock" => MockBehaviorMode.LooseMock,
            "passthrough" => MockBehaviorMode.PassthroughWithFallback,
            _ => MockBehaviorMode.Disabled
        };

    public static string ToApiString(this TestResultOutcome outcome) => outcome switch
    {
        TestResultOutcome.Passed => "passed",
        TestResultOutcome.Failed => "failed",
        TestResultOutcome.Skipped => "skipped",
        TestResultOutcome.Inconclusive => "inconclusive",
        TestResultOutcome.Errored => "errored",
        TestResultOutcome.TimedOut => "timed_out",
        _ => "skipped"
    };

    public static TestResultOutcome ParseTestResultOutcome(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "passed" or "pass" => TestResultOutcome.Passed,
            "failed" or "fail" => TestResultOutcome.Failed,
            "skipped" or "skip" => TestResultOutcome.Skipped,
            "inconclusive" => TestResultOutcome.Inconclusive,
            "errored" or "error" => TestResultOutcome.Errored,
            "timed_out" or "timeout" => TestResultOutcome.TimedOut,
            _ => TestResultOutcome.Skipped
        };

    public static string ToApiString(this ReportOutputFormat format) => format switch
    {
        ReportOutputFormat.ConsoleTable => "console",
        ReportOutputFormat.JsonFile => "json",
        ReportOutputFormat.MarkdownSummary => "markdown",
        ReportOutputFormat.CsvExport => "csv",
        ReportOutputFormat.HtmlDashboard => "html",
        _ => "console"
    };

    public static ReportOutputFormat ParseReportOutputFormat(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "console" or "console_table" => ReportOutputFormat.ConsoleTable,
            "json" or "json_file" => ReportOutputFormat.JsonFile,
            "markdown" or "md" => ReportOutputFormat.MarkdownSummary,
            "csv" => ReportOutputFormat.CsvExport,
            "html" => ReportOutputFormat.HtmlDashboard,
            _ => ReportOutputFormat.ConsoleTable
        };

    public static string ToApiString(this GroundTruthDatasetName dataset) => dataset switch
    {
        GroundTruthDatasetName.BusterV1 => "buster_v1",
        GroundTruthDatasetName.JungleBookV2 => "jungle_book_v2",
        GroundTruthDatasetName.TellTaleHeartV4 => "telltale_heart_v4",
        GroundTruthDatasetName.CustomDataset => "custom",
        _ => "custom"
    };

    public static GroundTruthDatasetName ParseGroundTruthDatasetName(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "buster_v1" or "buster" => GroundTruthDatasetName.BusterV1,
            "jungle_book_v2" or "jungle_book" => GroundTruthDatasetName.JungleBookV2,
            "telltale_heart_v4" or "telltale_heart" => GroundTruthDatasetName.TellTaleHeartV4,
            "custom" or "custom_dataset" => GroundTruthDatasetName.CustomDataset,
            _ => GroundTruthDatasetName.CustomDataset
        };

    public static string ToApiString(this EvalModelTemperature temp) => temp switch
    {
        EvalModelTemperature.ZeroDeterministic => "0.0",
        EvalModelTemperature.LowCreative => "0.2",
        EvalModelTemperature.MediumBalanced => "0.7",
        EvalModelTemperature.HighCreative => "1.0",
        _ => "0.0"
    };

    public static EvalModelTemperature ParseEvalModelTemperature(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "0.0" or "zero" or "deterministic" => EvalModelTemperature.ZeroDeterministic,
            "0.2" or "low" => EvalModelTemperature.LowCreative,
            "0.7" or "medium" => EvalModelTemperature.MediumBalanced,
            "1.0" or "high" => EvalModelTemperature.HighCreative,
            _ => EvalModelTemperature.ZeroDeterministic
        };

    public static string ToApiString(this EvalPromptStrategy strategy) => strategy switch
    {
        EvalPromptStrategy.ZeroShot => "zero_shot",
        EvalPromptStrategy.FewShot => "few_shot",
        EvalPromptStrategy.ChainOfThought => "chain_of_thought",
        EvalPromptStrategy.RolePrompted => "role_prompted",
        EvalPromptStrategy.SystemStructured => "system_structured",
        _ => "zero_shot"
    };

    public static EvalPromptStrategy ParseEvalPromptStrategy(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "zero_shot" => EvalPromptStrategy.ZeroShot,
            "few_shot" => EvalPromptStrategy.FewShot,
            "chain_of_thought" or "cot" => EvalPromptStrategy.ChainOfThought,
            "role_prompted" => EvalPromptStrategy.RolePrompted,
            "system_structured" => EvalPromptStrategy.SystemStructured,
            _ => EvalPromptStrategy.ZeroShot
        };

    public static string ToApiString(this ScoreThresholdComparison cmp) => cmp switch
    {
        ScoreThresholdComparison.GreaterThan => "gt",
        ScoreThresholdComparison.GreaterThanOrEqual => "gte",
        ScoreThresholdComparison.LessThan => "lt",
        ScoreThresholdComparison.LessThanOrEqual => "lte",
        ScoreThresholdComparison.Equal => "eq",
        _ => "gte"
    };

    public static ScoreThresholdComparison ParseScoreThresholdComparison(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "gt" or "greater_than" => ScoreThresholdComparison.GreaterThan,
            "gte" or "greater_than_or_equal" => ScoreThresholdComparison.GreaterThanOrEqual,
            "lt" or "less_than" => ScoreThresholdComparison.LessThan,
            "lte" or "less_than_or_equal" => ScoreThresholdComparison.LessThanOrEqual,
            "eq" or "equal" => ScoreThresholdComparison.Equal,
            _ => ScoreThresholdComparison.GreaterThanOrEqual
        };

    public static string ToApiString(this TestFixtureFormat format) => format switch
    {
        TestFixtureFormat.Utf8Text => "utf8_text",
        TestFixtureFormat.BinaryStream => "binary",
        TestFixtureFormat.JsonModel => "json",
        TestFixtureFormat.XmlPayload => "xml",
        _ => "utf8_text"
    };

    public static TestFixtureFormat ParseTestFixtureFormat(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "utf8_text" or "text" => TestFixtureFormat.Utf8Text,
            "binary" or "binary_stream" => TestFixtureFormat.BinaryStream,
            "json" or "json_model" => TestFixtureFormat.JsonModel,
            "xml" or "xml_payload" => TestFixtureFormat.XmlPayload,
            _ => TestFixtureFormat.Utf8Text
        };

    public static string ToApiString(this LiveApiTestFlag flag) => flag switch
    {
        LiveApiTestFlag.Disabled => "disabled",
        LiveApiTestFlag.EnabledExplicitOnly => "enabled_explicit",
        LiveApiTestFlag.AutoIfKeyPresent => "auto_if_key_present",
        _ => "disabled"
    };

    public static LiveApiTestFlag ParseLiveApiTestFlag(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "disabled" => LiveApiTestFlag.Disabled,
            "enabled_explicit" or "explicit" => LiveApiTestFlag.EnabledExplicitOnly,
            "auto_if_key_present" or "auto" => LiveApiTestFlag.AutoIfKeyPresent,
            _ => LiveApiTestFlag.Disabled
        };

    public static string ToApiString(this BenchmarkRunMode mode) => mode switch
    {
        BenchmarkRunMode.SinglePass => "single_pass",
        BenchmarkRunMode.MultiIterAverage => "multi_iter_average",
        BenchmarkRunMode.StressMatrix => "stress_matrix",
        BenchmarkRunMode.Comparative => "comparative",
        _ => "single_pass"
    };

    public static BenchmarkRunMode ParseBenchmarkRunMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "single_pass" => BenchmarkRunMode.SinglePass,
            "multi_iter_average" or "average" => BenchmarkRunMode.MultiIterAverage,
            "stress_matrix" or "matrix" => BenchmarkRunMode.StressMatrix,
            "comparative" => BenchmarkRunMode.Comparative,
            _ => BenchmarkRunMode.SinglePass
        };

    public static string ToApiString(this EvalReportSummarySection section) => section switch
    {
        EvalReportSummarySection.Overview => "overview",
        EvalReportSummarySection.ModelComparison => "model_comparison",
        EvalReportSummarySection.TaskAccuracyBreakdown => "task_accuracy",
        EvalReportSummarySection.LatencyDistribution => "latency_distribution",
        EvalReportSummarySection.FailureAnalysis => "failure_analysis",
        EvalReportSummarySection.Recommendations => "recommendations",
        _ => "overview"
    };

    public static EvalReportSummarySection ParseEvalReportSummarySection(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "overview" => EvalReportSummarySection.Overview,
            "model_comparison" or "models" => EvalReportSummarySection.ModelComparison,
            "task_accuracy" or "accuracy" => EvalReportSummarySection.TaskAccuracyBreakdown,
            "latency_distribution" or "latency" => EvalReportSummarySection.LatencyDistribution,
            "failure_analysis" or "failures" => EvalReportSummarySection.FailureAnalysis,
            "recommendations" => EvalReportSummarySection.Recommendations,
            _ => EvalReportSummarySection.Overview
        };
}
