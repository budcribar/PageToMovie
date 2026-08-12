using System.Text.Json.Serialization;

namespace PageToMovie.LoadSim;

/// <summary>
/// Extended navigation tab options for administration dashboard views.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdminDashboardTabKind
{
    Overview,
    SystemHealth,
    JobsQueue,
    LogsExplorer,
    StorageUsage,
    LoadSimulation,
    Configuration,
    Diagnostics,
    CostAnalytics,
    SecurityAudit
}

/// <summary>
/// Extended virtual user roles for load simulation scenarios.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimVirtualUserRoleKind
{
    AnonymousViewer,
    StandardUser,
    PowerProducer,
    AdminOperator,
    BatchProcessor,
    LoadGeneratorAgent
}

/// <summary>
/// Extended statistical think time distribution patterns for load simulation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimThinkTimeKind
{
    Fixed,
    UniformRandom,
    GaussianRandom,
    PoissonArrival,
    ZeroDelay,
    ExponentialBurst
}

/// <summary>
/// Extended result status indicators for load test execution runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadSimResultStatusKind
{
    NotStarted,
    InProgress,
    Passed,
    WarningThreshold,
    ThresholdExceeded,
    Failed,
    Aborted
}

/// <summary>
/// Time window intervals for aggregating metric data streams.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetricsAggregationWindowKind
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
/// System component operational health states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemHealthStateKind
{
    Healthy,
    Degraded,
    Critical,
    Unhealthy,
    Maintenance,
    Unknown
}

/// <summary>
/// Target destinations for system logging output streams.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogOutputDestinationKind
{
    Console,
    FileSystem,
    Elasticsearch,
    ApplicationInsights,
    OpenTelemetry,
    Datadog,
    MemoryBuffer
}

/// <summary>
/// Queue scheduling strategies for background execution jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobSchedulerStrategyKind
{
    Fifo,
    PriorityQueue,
    RoundRobin,
    WeightedFair,
    CapacityAware,
    BackpressureThrottled
}

/// <summary>
/// Throttling strategies applied during high CPU load.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CpuThrottlingModeKind
{
    Disabled,
    SoftQuota,
    HardLimit,
    DynamicAdaptive,
    ThermalProtection
}

/// <summary>
/// Memory usage pressure alert classification levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemoryPressureLevelKind
{
    Low,
    Normal,
    Elevated,
    High,
    Critical,
    OutOfMemoryDanger
}

/// <summary>
/// Network communications protocol type options.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NetworkProtocolKindType
{
    Http11,
    Http2,
    Http3,
    WebSocket,
    Grpc,
    TcpRaw,
    Udp
}

/// <summary>
/// Supported SSL/TLS protocol version options.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SslTlsProtocolVersionKind
{
    Tls12,
    Tls13,
    AutoNegotiate,
    Tls10Legacy,
    Tls11Legacy
}

/// <summary>
/// Deployment environment target classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentNameTagKind
{
    Development,
    Testing,
    Staging,
    Production,
    LoadTestEnvironment,
    DisasterRecovery
}

/// <summary>
/// Feature toggle flag keys for runtime feature gating.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureToggleNameKind
{
    LivePreview,
    ExperimentalModels,
    BatchExport,
    AdvancedMetrics,
    DarkModeToggle,
    AiVoiceCloning,
    RealtimeStreaming
}

/// <summary>
/// Interval schedule options for automated system backups.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackupScheduleIntervalKind
{
    Hourly,
    Daily,
    Weekly,
    Monthly,
    OnDemand,
    ContinuousRealtime
}

/// <summary>
/// Diagnostic dump types for system troubleshooting and memory inspection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticDumpTypeKind
{
    ProcessHeap,
    ThreadDump,
    GcStats,
    NetworkTrace,
    FullSystemState,
    CoreDump
}

/// <summary>
/// System incident severity levels for operational alerting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentSeverityKind
{
    Sev0Critical,
    Sev1High,
    Sev2Medium,
    Sev3Low,
    Sev4Informational
}

/// <summary>
/// Communication notification channels for system alert routing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertNotificationChannelKind
{
    Email,
    Slack,
    Webhook,
    PagerDuty,
    SmsConsole,
    MicrosoftTeams
}

/// <summary>
/// Inactivity timeout thresholds for user web sessions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserSessionTimeoutKind
{
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    EightHours,
    TwentyFourHours,
    Never
}

/// <summary>
/// Operational status modes for scheduled system maintenance windows.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceWindowModeKind
{
    Active,
    Scheduled,
    Completed,
    Cancelled,
    Emergency,
    Postponed
}

/// <summary>
/// Extension methods for operations and admin extended enums string formatting and parsing.
/// </summary>
public static class OperationsAdminExtendedEnumExtensions
{
    public static AdminDashboardTabKind ParseAdminDashboardTabKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "overview" => AdminDashboardTabKind.Overview,
                "system_health" or "health" => AdminDashboardTabKind.SystemHealth,
                "jobs_queue" or "jobs" => AdminDashboardTabKind.JobsQueue,
                "logs_explorer" or "logs" => AdminDashboardTabKind.LogsExplorer,
                "storage_usage" or "storage" => AdminDashboardTabKind.StorageUsage,
                "load_simulation" or "loadsim" => AdminDashboardTabKind.LoadSimulation,
                "configuration" or "config" => AdminDashboardTabKind.Configuration,
                "diagnostics" => AdminDashboardTabKind.Diagnostics,
                "cost_analytics" or "cost" => AdminDashboardTabKind.CostAnalytics,
                "security_audit" or "security" => AdminDashboardTabKind.SecurityAudit,
                _ => AdminDashboardTabKind.Overview
            };

    public static AlertNotificationChannelKind ParseAlertNotificationChannelKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "email" => AlertNotificationChannelKind.Email,
                "slack" => AlertNotificationChannelKind.Slack,
                "webhook" => AlertNotificationChannelKind.Webhook,
                "pagerduty" => AlertNotificationChannelKind.PagerDuty,
                "sms" or "sms_console" => AlertNotificationChannelKind.SmsConsole,
                "teams" or "microsoft_teams" => AlertNotificationChannelKind.MicrosoftTeams,
                _ => AlertNotificationChannelKind.Email
            };

    public static BackupScheduleIntervalKind ParseBackupScheduleIntervalKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "hourly" => BackupScheduleIntervalKind.Hourly,
                "daily" => BackupScheduleIntervalKind.Daily,
                "weekly" => BackupScheduleIntervalKind.Weekly,
                "monthly" => BackupScheduleIntervalKind.Monthly,
                "on_demand" or "manual" => BackupScheduleIntervalKind.OnDemand,
                "continuous" or "continuous_realtime" => BackupScheduleIntervalKind.ContinuousRealtime,
                _ => BackupScheduleIntervalKind.Daily
            };

    public static CpuThrottlingModeKind ParseCpuThrottlingModeKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "disabled" or "off" => CpuThrottlingModeKind.Disabled,
                "soft_quota" or "soft" => CpuThrottlingModeKind.SoftQuota,
                "hard_limit" or "hard" => CpuThrottlingModeKind.HardLimit,
                "dynamic_adaptive" or "adaptive" => CpuThrottlingModeKind.DynamicAdaptive,
                "thermal_protection" or "thermal" => CpuThrottlingModeKind.ThermalProtection,
                _ => CpuThrottlingModeKind.Disabled
            };

    public static DiagnosticDumpTypeKind ParseDiagnosticDumpTypeKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "process_heap" or "heap" => DiagnosticDumpTypeKind.ProcessHeap,
                "thread_dump" or "threads" => DiagnosticDumpTypeKind.ThreadDump,
                "gc_stats" or "gc" => DiagnosticDumpTypeKind.GcStats,
                "network_trace" or "network" => DiagnosticDumpTypeKind.NetworkTrace,
                "full_system_state" or "full" => DiagnosticDumpTypeKind.FullSystemState,
                "core_dump" or "core" => DiagnosticDumpTypeKind.CoreDump,
                _ => DiagnosticDumpTypeKind.ProcessHeap
            };

    public static EnvironmentNameTagKind ParseEnvironmentNameTagKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "development" or "dev" => EnvironmentNameTagKind.Development,
                "testing" or "test" => EnvironmentNameTagKind.Testing,
                "staging" or "stage" => EnvironmentNameTagKind.Staging,
                "production" or "prod" => EnvironmentNameTagKind.Production,
                "load_test" or "loadtest" => EnvironmentNameTagKind.LoadTestEnvironment,
                "dr" or "disaster_recovery" => EnvironmentNameTagKind.DisasterRecovery,
                _ => EnvironmentNameTagKind.Development
            };

    public static FeatureToggleNameKind ParseFeatureToggleNameKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "live_preview" => FeatureToggleNameKind.LivePreview,
                "experimental_models" => FeatureToggleNameKind.ExperimentalModels,
                "batch_export" => FeatureToggleNameKind.BatchExport,
                "advanced_metrics" => FeatureToggleNameKind.AdvancedMetrics,
                "dark_mode_toggle" => FeatureToggleNameKind.DarkModeToggle,
                "ai_voice_cloning" => FeatureToggleNameKind.AiVoiceCloning,
                "realtime_streaming" => FeatureToggleNameKind.RealtimeStreaming,
                _ => FeatureToggleNameKind.LivePreview
            };

    public static IncidentSeverityKind ParseIncidentSeverityKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "sev0" or "critical" => IncidentSeverityKind.Sev0Critical,
                "sev1" or "high" => IncidentSeverityKind.Sev1High,
                "sev2" or "medium" => IncidentSeverityKind.Sev2Medium,
                "sev3" or "low" => IncidentSeverityKind.Sev3Low,
                "sev4" or "info" or "informational" => IncidentSeverityKind.Sev4Informational,
                _ => IncidentSeverityKind.Sev3Low
            };

    public static JobSchedulerStrategyKind ParseJobSchedulerStrategyKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "fifo" => JobSchedulerStrategyKind.Fifo,
                "priority" or "priority_queue" => JobSchedulerStrategyKind.PriorityQueue,
                "round_robin" => JobSchedulerStrategyKind.RoundRobin,
                "weighted_fair" => JobSchedulerStrategyKind.WeightedFair,
                "capacity_aware" or "capacity" => JobSchedulerStrategyKind.CapacityAware,
                "backpressure" or "backpressure_throttled" => JobSchedulerStrategyKind.BackpressureThrottled,
                _ => JobSchedulerStrategyKind.Fifo
            };

    public static LoadSimResultStatusKind ParseLoadSimResultStatusKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "not_started" => LoadSimResultStatusKind.NotStarted,
                "in_progress" or "running" => LoadSimResultStatusKind.InProgress,
                "passed" or "pass" => LoadSimResultStatusKind.Passed,
                "warning" or "warning_threshold" => LoadSimResultStatusKind.WarningThreshold,
                "threshold_exceeded" or "exceeded" => LoadSimResultStatusKind.ThresholdExceeded,
                "failed" or "fail" => LoadSimResultStatusKind.Failed,
                "aborted" or "cancel" => LoadSimResultStatusKind.Aborted,
                _ => LoadSimResultStatusKind.NotStarted
            };

    public static LoadSimThinkTimeKind ParseLoadSimThinkTimeKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "fixed" => LoadSimThinkTimeKind.Fixed,
                "uniform" or "uniform_random" => LoadSimThinkTimeKind.UniformRandom,
                "gaussian" or "gaussian_random" => LoadSimThinkTimeKind.GaussianRandom,
                "poisson" or "poisson_arrival" => LoadSimThinkTimeKind.PoissonArrival,
                "zero_delay" or "zero" => LoadSimThinkTimeKind.ZeroDelay,
                "exponential" or "exponential_burst" => LoadSimThinkTimeKind.ExponentialBurst,
                _ => LoadSimThinkTimeKind.Fixed
            };

    public static LoadSimVirtualUserRoleKind ParseLoadSimVirtualUserRoleKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "anonymous_viewer" or "anonymous" => LoadSimVirtualUserRoleKind.AnonymousViewer,
                "standard_user" or "standard" => LoadSimVirtualUserRoleKind.StandardUser,
                "power_producer" or "power" => LoadSimVirtualUserRoleKind.PowerProducer,
                "admin_operator" or "admin" => LoadSimVirtualUserRoleKind.AdminOperator,
                "batch_processor" or "batch" => LoadSimVirtualUserRoleKind.BatchProcessor,
                "load_generator" or "load_generator_agent" => LoadSimVirtualUserRoleKind.LoadGeneratorAgent,
                _ => LoadSimVirtualUserRoleKind.StandardUser
            };

    public static LogOutputDestinationKind ParseLogOutputDestinationKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "console" => LogOutputDestinationKind.Console,
                "file" or "file_system" => LogOutputDestinationKind.FileSystem,
                "elasticsearch" or "elastic" => LogOutputDestinationKind.Elasticsearch,
                "app_insights" or "application_insights" => LogOutputDestinationKind.ApplicationInsights,
                "opentelemetry" or "otel" => LogOutputDestinationKind.OpenTelemetry,
                "datadog" => LogOutputDestinationKind.Datadog,
                "memory" or "memory_buffer" => LogOutputDestinationKind.MemoryBuffer,
                _ => LogOutputDestinationKind.Console
            };

    public static MaintenanceWindowModeKind ParseMaintenanceWindowModeKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "active" => MaintenanceWindowModeKind.Active,
                "scheduled" => MaintenanceWindowModeKind.Scheduled,
                "completed" => MaintenanceWindowModeKind.Completed,
                "cancelled" => MaintenanceWindowModeKind.Cancelled,
                "emergency" => MaintenanceWindowModeKind.Emergency,
                "postponed" => MaintenanceWindowModeKind.Postponed,
                _ => MaintenanceWindowModeKind.Scheduled
            };

    public static MemoryPressureLevelKind ParseMemoryPressureLevelKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "low" => MemoryPressureLevelKind.Low,
                "normal" => MemoryPressureLevelKind.Normal,
                "elevated" => MemoryPressureLevelKind.Elevated,
                "high" => MemoryPressureLevelKind.High,
                "critical" => MemoryPressureLevelKind.Critical,
                "oom_danger" or "oom" => MemoryPressureLevelKind.OutOfMemoryDanger,
                _ => MemoryPressureLevelKind.Normal
            };

    public static MetricsAggregationWindowKind ParseMetricsAggregationWindowKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "1s" or "one_second" => MetricsAggregationWindowKind.OneSecond,
                "5s" or "five_seconds" => MetricsAggregationWindowKind.FiveSeconds,
                "1m" or "one_minute" => MetricsAggregationWindowKind.OneMinute,
                "5m" or "five_minutes" => MetricsAggregationWindowKind.FiveMinutes,
                "15m" or "fifteen_minutes" => MetricsAggregationWindowKind.FifteenMinutes,
                "1h" or "one_hour" => MetricsAggregationWindowKind.OneHour,
                "1d" or "one_day" => MetricsAggregationWindowKind.OneDay,
                _ => MetricsAggregationWindowKind.OneMinute
            };

    public static NetworkProtocolKindType ParseNetworkProtocolKindType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "http/1.1" or "http11" or "http1" => NetworkProtocolKindType.Http11,
                "http/2" or "http2" => NetworkProtocolKindType.Http2,
                "http/3" or "http3" => NetworkProtocolKindType.Http3,
                "websocket" or "ws" => NetworkProtocolKindType.WebSocket,
                "grpc" => NetworkProtocolKindType.Grpc,
                "tcp" or "tcp_raw" => NetworkProtocolKindType.TcpRaw,
                "udp" => NetworkProtocolKindType.Udp,
                _ => NetworkProtocolKindType.Http11
            };

    public static SslTlsProtocolVersionKind ParseSslTlsProtocolVersionKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "tls_1.2" or "tls12" => SslTlsProtocolVersionKind.Tls12,
                "tls_1.3" or "tls13" => SslTlsProtocolVersionKind.Tls13,
                "auto" or "auto_negotiate" => SslTlsProtocolVersionKind.AutoNegotiate,
                "tls_1.0" or "tls10" => SslTlsProtocolVersionKind.Tls10Legacy,
                "tls_1.1" or "tls11" => SslTlsProtocolVersionKind.Tls11Legacy,
                _ => SslTlsProtocolVersionKind.AutoNegotiate
            };

    public static SystemHealthStateKind ParseSystemHealthStateKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "healthy" => SystemHealthStateKind.Healthy,
                "degraded" => SystemHealthStateKind.Degraded,
                "critical" => SystemHealthStateKind.Critical,
                "unhealthy" => SystemHealthStateKind.Unhealthy,
                "maintenance" => SystemHealthStateKind.Maintenance,
                "unknown" => SystemHealthStateKind.Unknown,
                _ => SystemHealthStateKind.Unknown
            };

    public static UserSessionTimeoutKind ParseUserSessionTimeoutKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "15m" or "fifteen_minutes" => UserSessionTimeoutKind.FifteenMinutes,
                "30m" or "thirty_minutes" => UserSessionTimeoutKind.ThirtyMinutes,
                "1h" or "one_hour" => UserSessionTimeoutKind.OneHour,
                "8h" or "eight_hours" => UserSessionTimeoutKind.EightHours,
                "24h" or "twenty_four_hours" => UserSessionTimeoutKind.TwentyFourHours,
                "never" => UserSessionTimeoutKind.Never,
                _ => UserSessionTimeoutKind.OneHour
            };

    public static AdminDashboardTabKind ToAdminDashboardTabKind(this string? value) => ParseAdminDashboardTabKind(value);

    public static AlertNotificationChannelKind ToAlertNotificationChannelKind(this string? value) => ParseAlertNotificationChannelKind(value);

    public static string ToApiString(this AdminDashboardTabKind tab) => tab switch
        {
            AdminDashboardTabKind.Overview => "overview",
            AdminDashboardTabKind.SystemHealth => "system_health",
            AdminDashboardTabKind.JobsQueue => "jobs_queue",
            AdminDashboardTabKind.LogsExplorer => "logs_explorer",
            AdminDashboardTabKind.StorageUsage => "storage_usage",
            AdminDashboardTabKind.LoadSimulation => "load_simulation",
            AdminDashboardTabKind.Configuration => "configuration",
            AdminDashboardTabKind.Diagnostics => "diagnostics",
            AdminDashboardTabKind.CostAnalytics => "cost_analytics",
            AdminDashboardTabKind.SecurityAudit => "security_audit",
            _ => "overview"
        };

    public static string ToApiString(this LoadSimVirtualUserRoleKind role) => role switch
        {
            LoadSimVirtualUserRoleKind.AnonymousViewer => "anonymous_viewer",
            LoadSimVirtualUserRoleKind.StandardUser => "standard_user",
            LoadSimVirtualUserRoleKind.PowerProducer => "power_producer",
            LoadSimVirtualUserRoleKind.AdminOperator => "admin_operator",
            LoadSimVirtualUserRoleKind.BatchProcessor => "batch_processor",
            LoadSimVirtualUserRoleKind.LoadGeneratorAgent => "load_generator",
            _ => "standard_user"
        };

    public static string ToApiString(this LoadSimThinkTimeKind pattern) => pattern switch
        {
            LoadSimThinkTimeKind.Fixed => "fixed",
            LoadSimThinkTimeKind.UniformRandom => "uniform",
            LoadSimThinkTimeKind.GaussianRandom => "gaussian",
            LoadSimThinkTimeKind.PoissonArrival => "poisson",
            LoadSimThinkTimeKind.ZeroDelay => "zero_delay",
            LoadSimThinkTimeKind.ExponentialBurst => "exponential",
            _ => "fixed"
        };

    public static string ToApiString(this LoadSimResultStatusKind status) => status switch
        {
            LoadSimResultStatusKind.NotStarted => "not_started",
            LoadSimResultStatusKind.InProgress => "in_progress",
            LoadSimResultStatusKind.Passed => "passed",
            LoadSimResultStatusKind.WarningThreshold => "warning",
            LoadSimResultStatusKind.ThresholdExceeded => "threshold_exceeded",
            LoadSimResultStatusKind.Failed => "failed",
            LoadSimResultStatusKind.Aborted => "aborted",
            _ => "not_started"
        };

    public static string ToApiString(this MetricsAggregationWindowKind window) => window switch
        {
            MetricsAggregationWindowKind.OneSecond => "1s",
            MetricsAggregationWindowKind.FiveSeconds => "5s",
            MetricsAggregationWindowKind.OneMinute => "1m",
            MetricsAggregationWindowKind.FiveMinutes => "5m",
            MetricsAggregationWindowKind.FifteenMinutes => "15m",
            MetricsAggregationWindowKind.OneHour => "1h",
            MetricsAggregationWindowKind.OneDay => "1d",
            _ => "1m"
        };

    public static string ToApiString(this SystemHealthStateKind state) => state switch
        {
            SystemHealthStateKind.Healthy => "healthy",
            SystemHealthStateKind.Degraded => "degraded",
            SystemHealthStateKind.Critical => "critical",
            SystemHealthStateKind.Unhealthy => "unhealthy",
            SystemHealthStateKind.Maintenance => "maintenance",
            SystemHealthStateKind.Unknown => "unknown",
            _ => "unknown"
        };

    public static string ToApiString(this LogOutputDestinationKind dest) => dest switch
        {
            LogOutputDestinationKind.Console => "console",
            LogOutputDestinationKind.FileSystem => "file",
            LogOutputDestinationKind.Elasticsearch => "elasticsearch",
            LogOutputDestinationKind.ApplicationInsights => "app_insights",
            LogOutputDestinationKind.OpenTelemetry => "opentelemetry",
            LogOutputDestinationKind.Datadog => "datadog",
            LogOutputDestinationKind.MemoryBuffer => "memory",
            _ => "console"
        };

    public static string ToApiString(this JobSchedulerStrategyKind strategy) => strategy switch
        {
            JobSchedulerStrategyKind.Fifo => "fifo",
            JobSchedulerStrategyKind.PriorityQueue => "priority",
            JobSchedulerStrategyKind.RoundRobin => "round_robin",
            JobSchedulerStrategyKind.WeightedFair => "weighted_fair",
            JobSchedulerStrategyKind.CapacityAware => "capacity_aware",
            JobSchedulerStrategyKind.BackpressureThrottled => "backpressure",
            _ => "fifo"
        };

    public static string ToApiString(this CpuThrottlingModeKind mode) => mode switch
        {
            CpuThrottlingModeKind.Disabled => "disabled",
            CpuThrottlingModeKind.SoftQuota => "soft_quota",
            CpuThrottlingModeKind.HardLimit => "hard_limit",
            CpuThrottlingModeKind.DynamicAdaptive => "dynamic_adaptive",
            CpuThrottlingModeKind.ThermalProtection => "thermal_protection",
            _ => "disabled"
        };

    public static string ToApiString(this MemoryPressureLevelKind level) => level switch
        {
            MemoryPressureLevelKind.Low => "low",
            MemoryPressureLevelKind.Normal => "normal",
            MemoryPressureLevelKind.Elevated => "elevated",
            MemoryPressureLevelKind.High => "high",
            MemoryPressureLevelKind.Critical => "critical",
            MemoryPressureLevelKind.OutOfMemoryDanger => "oom_danger",
            _ => "normal"
        };

    public static string ToApiString(this NetworkProtocolKindType proto) => proto switch
        {
            NetworkProtocolKindType.Http11 => "http/1.1",
            NetworkProtocolKindType.Http2 => "http/2",
            NetworkProtocolKindType.Http3 => "http/3",
            NetworkProtocolKindType.WebSocket => "websocket",
            NetworkProtocolKindType.Grpc => "grpc",
            NetworkProtocolKindType.TcpRaw => "tcp",
            NetworkProtocolKindType.Udp => "udp",
            _ => "http/1.1"
        };

    public static string ToApiString(this SslTlsProtocolVersionKind tls) => tls switch
        {
            SslTlsProtocolVersionKind.Tls12 => "tls_1.2",
            SslTlsProtocolVersionKind.Tls13 => "tls_1.3",
            SslTlsProtocolVersionKind.AutoNegotiate => "auto",
            SslTlsProtocolVersionKind.Tls10Legacy => "tls_1.0",
            SslTlsProtocolVersionKind.Tls11Legacy => "tls_1.1",
            _ => "auto"
        };

    public static string ToApiString(this EnvironmentNameTagKind env) => env switch
        {
            EnvironmentNameTagKind.Development => "development",
            EnvironmentNameTagKind.Testing => "testing",
            EnvironmentNameTagKind.Staging => "staging",
            EnvironmentNameTagKind.Production => "production",
            EnvironmentNameTagKind.LoadTestEnvironment => "load_test",
            EnvironmentNameTagKind.DisasterRecovery => "dr",
            _ => "development"
        };

    public static string ToApiString(this FeatureToggleNameKind feature) => feature switch
        {
            FeatureToggleNameKind.LivePreview => "live_preview",
            FeatureToggleNameKind.ExperimentalModels => "experimental_models",
            FeatureToggleNameKind.BatchExport => "batch_export",
            FeatureToggleNameKind.AdvancedMetrics => "advanced_metrics",
            FeatureToggleNameKind.DarkModeToggle => "dark_mode_toggle",
            FeatureToggleNameKind.AiVoiceCloning => "ai_voice_cloning",
            FeatureToggleNameKind.RealtimeStreaming => "realtime_streaming",
            _ => "live_preview"
        };

    public static string ToApiString(this BackupScheduleIntervalKind interval) => interval switch
        {
            BackupScheduleIntervalKind.Hourly => "hourly",
            BackupScheduleIntervalKind.Daily => "daily",
            BackupScheduleIntervalKind.Weekly => "weekly",
            BackupScheduleIntervalKind.Monthly => "monthly",
            BackupScheduleIntervalKind.OnDemand => "on_demand",
            BackupScheduleIntervalKind.ContinuousRealtime => "continuous",
            _ => "daily"
        };

    public static string ToApiString(this DiagnosticDumpTypeKind dumpType) => dumpType switch
        {
            DiagnosticDumpTypeKind.ProcessHeap => "process_heap",
            DiagnosticDumpTypeKind.ThreadDump => "thread_dump",
            DiagnosticDumpTypeKind.GcStats => "gc_stats",
            DiagnosticDumpTypeKind.NetworkTrace => "network_trace",
            DiagnosticDumpTypeKind.FullSystemState => "full_system_state",
            DiagnosticDumpTypeKind.CoreDump => "core_dump",
            _ => "process_heap"
        };

    public static string ToApiString(this IncidentSeverityKind sev) => sev switch
        {
            IncidentSeverityKind.Sev0Critical => "sev0",
            IncidentSeverityKind.Sev1High => "sev1",
            IncidentSeverityKind.Sev2Medium => "sev2",
            IncidentSeverityKind.Sev3Low => "sev3",
            IncidentSeverityKind.Sev4Informational => "sev4",
            _ => "sev3"
        };

    public static string ToApiString(this AlertNotificationChannelKind channel) => channel switch
        {
            AlertNotificationChannelKind.Email => "email",
            AlertNotificationChannelKind.Slack => "slack",
            AlertNotificationChannelKind.Webhook => "webhook",
            AlertNotificationChannelKind.PagerDuty => "pagerduty",
            AlertNotificationChannelKind.SmsConsole => "sms",
            AlertNotificationChannelKind.MicrosoftTeams => "teams",
            _ => "email"
        };

    public static string ToApiString(this UserSessionTimeoutKind timeout) => timeout switch
        {
            UserSessionTimeoutKind.FifteenMinutes => "15m",
            UserSessionTimeoutKind.ThirtyMinutes => "30m",
            UserSessionTimeoutKind.OneHour => "1h",
            UserSessionTimeoutKind.EightHours => "8h",
            UserSessionTimeoutKind.TwentyFourHours => "24h",
            UserSessionTimeoutKind.Never => "never",
            _ => "1h"
        };

    public static string ToApiString(this MaintenanceWindowModeKind mode) => mode switch
        {
            MaintenanceWindowModeKind.Active => "active",
            MaintenanceWindowModeKind.Scheduled => "scheduled",
            MaintenanceWindowModeKind.Completed => "completed",
            MaintenanceWindowModeKind.Cancelled => "cancelled",
            MaintenanceWindowModeKind.Emergency => "emergency",
            MaintenanceWindowModeKind.Postponed => "postponed",
            _ => "scheduled"
        };

    public static BackupScheduleIntervalKind ToBackupScheduleIntervalKind(this string? value) => ParseBackupScheduleIntervalKind(value);

    public static CpuThrottlingModeKind ToCpuThrottlingModeKind(this string? value) => ParseCpuThrottlingModeKind(value);

    public static DiagnosticDumpTypeKind ToDiagnosticDumpTypeKind(this string? value) => ParseDiagnosticDumpTypeKind(value);

    public static EnvironmentNameTagKind ToEnvironmentNameTagKind(this string? value) => ParseEnvironmentNameTagKind(value);

    public static FeatureToggleNameKind ToFeatureToggleNameKind(this string? value) => ParseFeatureToggleNameKind(value);

    public static IncidentSeverityKind ToIncidentSeverityKind(this string? value) => ParseIncidentSeverityKind(value);

    public static JobSchedulerStrategyKind ToJobSchedulerStrategyKind(this string? value) => ParseJobSchedulerStrategyKind(value);

    public static LoadSimResultStatusKind ToLoadSimResultStatusKind(this string? value) => ParseLoadSimResultStatusKind(value);

    public static LoadSimThinkTimeKind ToLoadSimThinkTimeKind(this string? value) => ParseLoadSimThinkTimeKind(value);

    public static LoadSimVirtualUserRoleKind ToLoadSimVirtualUserRoleKind(this string? value) => ParseLoadSimVirtualUserRoleKind(value);

    public static LogOutputDestinationKind ToLogOutputDestinationKind(this string? value) => ParseLogOutputDestinationKind(value);

    public static MaintenanceWindowModeKind ToMaintenanceWindowModeKind(this string? value) => ParseMaintenanceWindowModeKind(value);

    public static MemoryPressureLevelKind ToMemoryPressureLevelKind(this string? value) => ParseMemoryPressureLevelKind(value);

    public static MetricsAggregationWindowKind ToMetricsAggregationWindowKind(this string? value) => ParseMetricsAggregationWindowKind(value);

    public static NetworkProtocolKindType ToNetworkProtocolKindType(this string? value) => ParseNetworkProtocolKindType(value);

    public static SslTlsProtocolVersionKind ToSslTlsProtocolVersionKind(this string? value) => ParseSslTlsProtocolVersionKind(value);

    public static SystemHealthStateKind ToSystemHealthStateKind(this string? value) => ParseSystemHealthStateKind(value);

    public static UserSessionTimeoutKind ToUserSessionTimeoutKind(this string? value) => ParseUserSessionTimeoutKind(value);

}
