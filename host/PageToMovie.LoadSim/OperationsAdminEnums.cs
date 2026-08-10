using System.Text.Json.Serialization;

namespace PageToMovie.LoadSim;

/// <summary>
/// Navigation tabs in the operational administration dashboard.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdminDashboardTab
{
    Overview,
    SystemHealth,
    JobsQueue,
    LogsExplorer,
    StorageUsage,
    LoadSimulation,
    Configuration,
    Diagnostics
}

/// <summary>
/// Persona role assigned to virtual users during load testing simulations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimVirtualUserRole
{
    AnonymousViewer,
    StandardUser,
    PowerProducer,
    AdminOperator,
    BatchProcessor
}

/// <summary>
/// Statistical model distribution for user think-time pauses between requests.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimThinkTimePattern
{
    Fixed,
    UniformRandom,
    GaussianRandom,
    PoissonArrival,
    ZeroDelay
}

/// <summary>
/// Overall result status of a load simulation run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimResultStatus
{
    NotStarted,
    Running,
    Passed,
    Failed,
    Aborted,
    TimedOut
}

/// <summary>
/// Time window bucket size for metric aggregations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetricsAggregationWindow
{
    OneSecond,
    FiveSeconds,
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    OneHour,
    OneDay
}

/// <summary>
/// Overall operational health state of the application service stack.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemHealthState
{
    Healthy,
    Degraded,
    Critical,
    Unhealthy,
    Maintenance
}

/// <summary>
/// Target sink destination for structured system logs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogOutputDestination
{
    Console,
    LocalFile,
    Elasticsearch,
    Seq,
    OpenTelemetryCollector,
    Null
}

/// <summary>
/// Dispatch strategy for background job execution queues.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobSchedulerStrategy
{
    Fifo,
    Lifo,
    PriorityWeighted,
    RoundRobin,
    FairShare
}

/// <summary>
/// Execution throttling mode applied when CPU limits are reached.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CpuThrottlingMode
{
    Disabled,
    SoftCap,
    HardCap,
    AdaptiveDynamic
}

/// <summary>
/// System memory pressure level for triggering cache evictions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemoryPressureLevel
{
    Normal,
    Moderate,
    High,
    Critical,
    OutOfMemoryRisk
}

/// <summary>
/// Network transport protocol used for endpoint communications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NetworkProtocolKind
{
    Http11,
    Http2,
    Http3,
    WebSocket,
    Grpc
}

/// <summary>
/// SSL/TLS security protocol version configuration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SslTlsProtocolVersion
{
    Tls12,
    Tls13,
    AutoNegotiate
}

/// <summary>
/// Environment classification tag for system deployment.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentNameTag
{
    Development,
    Testing,
    Staging,
    Production,
    LoadTestEnvironment
}

/// <summary>
/// Operational feature toggle names.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureToggleName
{
    LivePreview,
    ExperimentalModels,
    BatchExport,
    AdvancedMetrics,
    DarkModeToggle
}

/// <summary>
/// Recurrence interval for automated database/media backups.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackupScheduleInterval
{
    Hourly,
    Daily,
    Weekly,
    Monthly,
    OnDemand
}

/// <summary>
/// Diagnostic dump capture types for troubleshooting system issues.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticDumpType
{
    ProcessHeap,
    ThreadDump,
    GcStats,
    NetworkTrace,
    FullSystemState
}

/// <summary>
/// Operational incident severity classification level.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentSeverity
{
    Sev0Critical,
    Sev1High,
    Sev2Medium,
    Sev3Low,
    Sev4Informational
}

/// <summary>
/// Notification channels for system monitoring alerts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertNotificationChannel
{
    Email,
    Slack,
    Webhook,
    PagerDuty,
    SmsConsole
}

/// <summary>
/// Idle duration before an operator/user session is invalidated.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserSessionTimeout
{
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    EightHours,
    TwentyFourHours,
    Never
}

/// <summary>
/// Status and execution mode of scheduled system maintenance windows.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceWindowMode
{
    Active,
    Scheduled,
    Completed,
    Cancelled,
    Emergency
}

/// <summary>
/// Extension methods and string parsers for operations admin enums.
/// </summary>
public static class OperationsAdminEnumExtensions
{
    public static string ToApiString(this AdminDashboardTab tab) => tab switch
    {
        AdminDashboardTab.Overview => "overview",
        AdminDashboardTab.SystemHealth => "system_health",
        AdminDashboardTab.JobsQueue => "jobs_queue",
        AdminDashboardTab.LogsExplorer => "logs_explorer",
        AdminDashboardTab.StorageUsage => "storage_usage",
        AdminDashboardTab.LoadSimulation => "load_simulation",
        AdminDashboardTab.Configuration => "configuration",
        AdminDashboardTab.Diagnostics => "diagnostics",
        _ => "overview"
    };

    public static AdminDashboardTab ParseAdminDashboardTab(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "overview" => AdminDashboardTab.Overview,
            "system_health" or "health" => AdminDashboardTab.SystemHealth,
            "jobs_queue" or "jobs" => AdminDashboardTab.JobsQueue,
            "logs_explorer" or "logs" => AdminDashboardTab.LogsExplorer,
            "storage_usage" or "storage" => AdminDashboardTab.StorageUsage,
            "load_simulation" or "loadsim" => AdminDashboardTab.LoadSimulation,
            "configuration" or "settings" => AdminDashboardTab.Configuration,
            "diagnostics" => AdminDashboardTab.Diagnostics,
            _ => AdminDashboardTab.Overview
        };

    public static string ToApiString(this LoadSimVirtualUserRole role) => role switch
    {
        LoadSimVirtualUserRole.AnonymousViewer => "anonymous_viewer",
        LoadSimVirtualUserRole.StandardUser => "standard_user",
        LoadSimVirtualUserRole.PowerProducer => "power_producer",
        LoadSimVirtualUserRole.AdminOperator => "admin_operator",
        LoadSimVirtualUserRole.BatchProcessor => "batch_processor",
        _ => "standard_user"
    };

    public static LoadSimVirtualUserRole ParseLoadSimVirtualUserRole(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "anonymous_viewer" or "anonymous" => LoadSimVirtualUserRole.AnonymousViewer,
            "standard_user" or "standard" => LoadSimVirtualUserRole.StandardUser,
            "power_producer" or "power" => LoadSimVirtualUserRole.PowerProducer,
            "admin_operator" or "admin" => LoadSimVirtualUserRole.AdminOperator,
            "batch_processor" or "batch" => LoadSimVirtualUserRole.BatchProcessor,
            _ => LoadSimVirtualUserRole.StandardUser
        };

    public static string ToApiString(this LoadSimThinkTimePattern pattern) => pattern switch
    {
        LoadSimThinkTimePattern.Fixed => "fixed",
        LoadSimThinkTimePattern.UniformRandom => "uniform",
        LoadSimThinkTimePattern.GaussianRandom => "gaussian",
        LoadSimThinkTimePattern.PoissonArrival => "poisson",
        LoadSimThinkTimePattern.ZeroDelay => "zero_delay",
        _ => "fixed"
    };

    public static LoadSimThinkTimePattern ParseLoadSimThinkTimePattern(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "fixed" => LoadSimThinkTimePattern.Fixed,
            "uniform" or "uniform_random" => LoadSimThinkTimePattern.UniformRandom,
            "gaussian" or "gaussian_random" => LoadSimThinkTimePattern.GaussianRandom,
            "poisson" or "poisson_arrival" => LoadSimThinkTimePattern.PoissonArrival,
            "zero_delay" or "zero" => LoadSimThinkTimePattern.ZeroDelay,
            _ => LoadSimThinkTimePattern.Fixed
        };

    public static string ToApiString(this LoadSimResultStatus status) => status switch
    {
        LoadSimResultStatus.NotStarted => "not_started",
        LoadSimResultStatus.Running => "running",
        LoadSimResultStatus.Passed => "passed",
        LoadSimResultStatus.Failed => "failed",
        LoadSimResultStatus.Aborted => "aborted",
        LoadSimResultStatus.TimedOut => "timed_out",
        _ => "not_started"
    };

    public static LoadSimResultStatus ParseLoadSimResultStatus(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "not_started" => LoadSimResultStatus.NotStarted,
            "running" => LoadSimResultStatus.Running,
            "passed" => LoadSimResultStatus.Passed,
            "failed" => LoadSimResultStatus.Failed,
            "aborted" => LoadSimResultStatus.Aborted,
            "timed_out" => LoadSimResultStatus.TimedOut,
            _ => LoadSimResultStatus.NotStarted
        };

    public static string ToApiString(this MetricsAggregationWindow window) => window switch
    {
        MetricsAggregationWindow.OneSecond => "1s",
        MetricsAggregationWindow.FiveSeconds => "5s",
        MetricsAggregationWindow.OneMinute => "1m",
        MetricsAggregationWindow.FiveMinutes => "5m",
        MetricsAggregationWindow.FifteenMinutes => "15m",
        MetricsAggregationWindow.OneHour => "1h",
        MetricsAggregationWindow.OneDay => "1d",
        _ => "1m"
    };

    public static MetricsAggregationWindow ParseMetricsAggregationWindow(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "1s" or "one_second" => MetricsAggregationWindow.OneSecond,
            "5s" or "five_seconds" => MetricsAggregationWindow.FiveSeconds,
            "1m" or "one_minute" => MetricsAggregationWindow.OneMinute,
            "5m" or "five_minutes" => MetricsAggregationWindow.FiveMinutes,
            "15m" or "fifteen_minutes" => MetricsAggregationWindow.FifteenMinutes,
            "1h" or "one_hour" => MetricsAggregationWindow.OneHour,
            "1d" or "one_day" => MetricsAggregationWindow.OneDay,
            _ => MetricsAggregationWindow.OneMinute
        };

    public static string ToApiString(this SystemHealthState state) => state switch
    {
        SystemHealthState.Healthy => "healthy",
        SystemHealthState.Degraded => "degraded",
        SystemHealthState.Critical => "critical",
        SystemHealthState.Unhealthy => "unhealthy",
        SystemHealthState.Maintenance => "maintenance",
        _ => "healthy"
    };

    public static SystemHealthState ParseSystemHealthState(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "healthy" => SystemHealthState.Healthy,
            "degraded" => SystemHealthState.Degraded,
            "critical" => SystemHealthState.Critical,
            "unhealthy" => SystemHealthState.Unhealthy,
            "maintenance" => SystemHealthState.Maintenance,
            _ => SystemHealthState.Healthy
        };

    public static string ToApiString(this LogOutputDestination dest) => dest switch
    {
        LogOutputDestination.Console => "console",
        LogOutputDestination.LocalFile => "local_file",
        LogOutputDestination.Elasticsearch => "elasticsearch",
        LogOutputDestination.Seq => "seq",
        LogOutputDestination.OpenTelemetryCollector => "opentelemetry",
        LogOutputDestination.Null => "null",
        _ => "console"
    };

    public static LogOutputDestination ParseLogOutputDestination(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "console" => LogOutputDestination.Console,
            "local_file" or "file" => LogOutputDestination.LocalFile,
            "elasticsearch" or "elastic" => LogOutputDestination.Elasticsearch,
            "seq" => LogOutputDestination.Seq,
            "opentelemetry" or "otel" => LogOutputDestination.OpenTelemetryCollector,
            "null" => LogOutputDestination.Null,
            _ => LogOutputDestination.Console
        };

    public static string ToApiString(this JobSchedulerStrategy strategy) => strategy switch
    {
        JobSchedulerStrategy.Fifo => "fifo",
        JobSchedulerStrategy.Lifo => "lifo",
        JobSchedulerStrategy.PriorityWeighted => "priority_weighted",
        JobSchedulerStrategy.RoundRobin => "round_robin",
        JobSchedulerStrategy.FairShare => "fair_share",
        _ => "fifo"
    };

    public static JobSchedulerStrategy ParseJobSchedulerStrategy(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "fifo" => JobSchedulerStrategy.Fifo,
            "lifo" => JobSchedulerStrategy.Lifo,
            "priority_weighted" or "priority" => JobSchedulerStrategy.PriorityWeighted,
            "round_robin" => JobSchedulerStrategy.RoundRobin,
            "fair_share" => JobSchedulerStrategy.FairShare,
            _ => JobSchedulerStrategy.Fifo
        };

    public static string ToApiString(this CpuThrottlingMode mode) => mode switch
    {
        CpuThrottlingMode.Disabled => "disabled",
        CpuThrottlingMode.SoftCap => "soft_cap",
        CpuThrottlingMode.HardCap => "hard_cap",
        CpuThrottlingMode.AdaptiveDynamic => "adaptive_dynamic",
        _ => "disabled"
    };

    public static CpuThrottlingMode ParseCpuThrottlingMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "disabled" => CpuThrottlingMode.Disabled,
            "soft_cap" => CpuThrottlingMode.SoftCap,
            "hard_cap" => CpuThrottlingMode.HardCap,
            "adaptive_dynamic" or "adaptive" => CpuThrottlingMode.AdaptiveDynamic,
            _ => CpuThrottlingMode.Disabled
        };

    public static string ToApiString(this MemoryPressureLevel level) => level switch
    {
        MemoryPressureLevel.Normal => "normal",
        MemoryPressureLevel.Moderate => "moderate",
        MemoryPressureLevel.High => "high",
        MemoryPressureLevel.Critical => "critical",
        MemoryPressureLevel.OutOfMemoryRisk => "oom_risk",
        _ => "normal"
    };

    public static MemoryPressureLevel ParseMemoryPressureLevel(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "normal" => MemoryPressureLevel.Normal,
            "moderate" => MemoryPressureLevel.Moderate,
            "high" => MemoryPressureLevel.High,
            "critical" => MemoryPressureLevel.Critical,
            "oom_risk" or "oom" => MemoryPressureLevel.OutOfMemoryRisk,
            _ => MemoryPressureLevel.Normal
        };

    public static string ToApiString(this NetworkProtocolKind protocol) => protocol switch
    {
        NetworkProtocolKind.Http11 => "http/1.1",
        NetworkProtocolKind.Http2 => "http/2",
        NetworkProtocolKind.Http3 => "http/3",
        NetworkProtocolKind.WebSocket => "websocket",
        NetworkProtocolKind.Grpc => "grpc",
        _ => "http/1.1"
    };

    public static NetworkProtocolKind ParseNetworkProtocolKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "http/1.1" or "http11" or "http1" => NetworkProtocolKind.Http11,
            "http/2" or "http2" => NetworkProtocolKind.Http2,
            "http/3" or "http3" => NetworkProtocolKind.Http3,
            "websocket" or "ws" => NetworkProtocolKind.WebSocket,
            "grpc" => NetworkProtocolKind.Grpc,
            _ => NetworkProtocolKind.Http11
        };

    public static string ToApiString(this SslTlsProtocolVersion tls) => tls switch
    {
        SslTlsProtocolVersion.Tls12 => "tls_1.2",
        SslTlsProtocolVersion.Tls13 => "tls_1.3",
        SslTlsProtocolVersion.AutoNegotiate => "auto",
        _ => "auto"
    };

    public static SslTlsProtocolVersion ParseSslTlsProtocolVersion(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "tls_1.2" or "tls12" => SslTlsProtocolVersion.Tls12,
            "tls_1.3" or "tls13" => SslTlsProtocolVersion.Tls13,
            "auto" => SslTlsProtocolVersion.AutoNegotiate,
            _ => SslTlsProtocolVersion.AutoNegotiate
        };

    public static string ToApiString(this EnvironmentNameTag env) => env switch
    {
        EnvironmentNameTag.Development => "development",
        EnvironmentNameTag.Testing => "testing",
        EnvironmentNameTag.Staging => "staging",
        EnvironmentNameTag.Production => "production",
        EnvironmentNameTag.LoadTestEnvironment => "load_test",
        _ => "development"
    };

    public static EnvironmentNameTag ParseEnvironmentNameTag(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "development" or "dev" => EnvironmentNameTag.Development,
            "testing" or "test" => EnvironmentNameTag.Testing,
            "staging" or "stage" => EnvironmentNameTag.Staging,
            "production" or "prod" => EnvironmentNameTag.Production,
            "load_test" or "loadtest" => EnvironmentNameTag.LoadTestEnvironment,
            _ => EnvironmentNameTag.Development
        };

    public static string ToApiString(this FeatureToggleName feature) => feature switch
    {
        FeatureToggleName.LivePreview => "live_preview",
        FeatureToggleName.ExperimentalModels => "experimental_models",
        FeatureToggleName.BatchExport => "batch_export",
        FeatureToggleName.AdvancedMetrics => "advanced_metrics",
        FeatureToggleName.DarkModeToggle => "dark_mode_toggle",
        _ => "live_preview"
    };

    public static FeatureToggleName ParseFeatureToggleName(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "live_preview" => FeatureToggleName.LivePreview,
            "experimental_models" => FeatureToggleName.ExperimentalModels,
            "batch_export" => FeatureToggleName.BatchExport,
            "advanced_metrics" => FeatureToggleName.AdvancedMetrics,
            "dark_mode_toggle" => FeatureToggleName.DarkModeToggle,
            _ => FeatureToggleName.LivePreview
        };

    public static string ToApiString(this BackupScheduleInterval interval) => interval switch
    {
        BackupScheduleInterval.Hourly => "hourly",
        BackupScheduleInterval.Daily => "daily",
        BackupScheduleInterval.Weekly => "weekly",
        BackupScheduleInterval.Monthly => "monthly",
        BackupScheduleInterval.OnDemand => "on_demand",
        _ => "daily"
    };

    public static BackupScheduleInterval ParseBackupScheduleInterval(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "hourly" => BackupScheduleInterval.Hourly,
            "daily" => BackupScheduleInterval.Daily,
            "weekly" => BackupScheduleInterval.Weekly,
            "monthly" => BackupScheduleInterval.Monthly,
            "on_demand" or "manual" => BackupScheduleInterval.OnDemand,
            _ => BackupScheduleInterval.Daily
        };

    public static string ToApiString(this DiagnosticDumpType dumpType) => dumpType switch
    {
        DiagnosticDumpType.ProcessHeap => "process_heap",
        DiagnosticDumpType.ThreadDump => "thread_dump",
        DiagnosticDumpType.GcStats => "gc_stats",
        DiagnosticDumpType.NetworkTrace => "network_trace",
        DiagnosticDumpType.FullSystemState => "full_system_state",
        _ => "process_heap"
    };

    public static DiagnosticDumpType ParseDiagnosticDumpType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "process_heap" or "heap" => DiagnosticDumpType.ProcessHeap,
            "thread_dump" or "threads" => DiagnosticDumpType.ThreadDump,
            "gc_stats" or "gc" => DiagnosticDumpType.GcStats,
            "network_trace" or "network" => DiagnosticDumpType.NetworkTrace,
            "full_system_state" or "full" => DiagnosticDumpType.FullSystemState,
            _ => DiagnosticDumpType.ProcessHeap
        };

    public static string ToApiString(this IncidentSeverity sev) => sev switch
    {
        IncidentSeverity.Sev0Critical => "sev0",
        IncidentSeverity.Sev1High => "sev1",
        IncidentSeverity.Sev2Medium => "sev2",
        IncidentSeverity.Sev3Low => "sev3",
        IncidentSeverity.Sev4Informational => "sev4",
        _ => "sev3"
    };

    public static IncidentSeverity ParseIncidentSeverity(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "sev0" or "critical" => IncidentSeverity.Sev0Critical,
            "sev1" or "high" => IncidentSeverity.Sev1High,
            "sev2" or "medium" => IncidentSeverity.Sev2Medium,
            "sev3" or "low" => IncidentSeverity.Sev3Low,
            "sev4" or "info" or "informational" => IncidentSeverity.Sev4Informational,
            _ => IncidentSeverity.Sev3Low
        };

    public static string ToApiString(this AlertNotificationChannel channel) => channel switch
    {
        AlertNotificationChannel.Email => "email",
        AlertNotificationChannel.Slack => "slack",
        AlertNotificationChannel.Webhook => "webhook",
        AlertNotificationChannel.PagerDuty => "pagerduty",
        AlertNotificationChannel.SmsConsole => "sms",
        _ => "email"
    };

    public static AlertNotificationChannel ParseAlertNotificationChannel(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "email" => AlertNotificationChannel.Email,
            "slack" => AlertNotificationChannel.Slack,
            "webhook" => AlertNotificationChannel.Webhook,
            "pagerduty" => AlertNotificationChannel.PagerDuty,
            "sms" or "sms_console" => AlertNotificationChannel.SmsConsole,
            _ => AlertNotificationChannel.Email
        };

    public static string ToApiString(this UserSessionTimeout timeout) => timeout switch
    {
        UserSessionTimeout.FifteenMinutes => "15m",
        UserSessionTimeout.ThirtyMinutes => "30m",
        UserSessionTimeout.OneHour => "1h",
        UserSessionTimeout.EightHours => "8h",
        UserSessionTimeout.TwentyFourHours => "24h",
        UserSessionTimeout.Never => "never",
        _ => "1h"
    };

    public static UserSessionTimeout ParseUserSessionTimeout(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "15m" or "fifteen_minutes" => UserSessionTimeout.FifteenMinutes,
            "30m" or "thirty_minutes" => UserSessionTimeout.ThirtyMinutes,
            "1h" or "one_hour" => UserSessionTimeout.OneHour,
            "8h" or "eight_hours" => UserSessionTimeout.EightHours,
            "24h" or "twenty_four_hours" => UserSessionTimeout.TwentyFourHours,
            "never" => UserSessionTimeout.Never,
            _ => UserSessionTimeout.OneHour
        };

    public static string ToApiString(this MaintenanceWindowMode mode) => mode switch
    {
        MaintenanceWindowMode.Active => "active",
        MaintenanceWindowMode.Scheduled => "scheduled",
        MaintenanceWindowMode.Completed => "completed",
        MaintenanceWindowMode.Cancelled => "cancelled",
        MaintenanceWindowMode.Emergency => "emergency",
        _ => "scheduled"
    };

    public static MaintenanceWindowMode ParseMaintenanceWindowMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "active" => MaintenanceWindowMode.Active,
            "scheduled" => MaintenanceWindowMode.Scheduled,
            "completed" => MaintenanceWindowMode.Completed,
            "cancelled" => MaintenanceWindowMode.Cancelled,
            "emergency" => MaintenanceWindowMode.Emergency,
            _ => MaintenanceWindowMode.Scheduled
        };
}
