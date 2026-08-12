using System.Text.Json.Serialization;

namespace PageToMovie.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpVerb
{
    Get,
    Post,
    Put,
    Delete,
    Patch,
    Head,
    Options,
    Connect,
    Trace
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpStatusCodeCategory
{
    Informational,
    Success,
    Redirection,
    ClientError,
    ServerError,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorsPolicyKind
{
    AllowAll,
    SameOrigin,
    StrictRestricted,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentEncodingType
{
    Identity,
    Gzip,
    Deflate,
    Brotli,
    Compress
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheControlDirective
{
    NoCache,
    NoStore,
    MaxAge,
    MustRevalidate,
    Private,
    Public,
    Immutable
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthSchemeType
{
    None,
    Bearer,
    ApiKey,
    Basic,
    OAuth2,
    Cookie
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiVersionTag
{
    V1,
    V2,
    Beta,
    Preview,
    Deprecated
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadFormatType
{
    Json,
    Xml,
    FormUrlEncoded,
    MultipartForm,
    BinaryStream,
    TextPlain
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RateLimitAlgorithm
{
    FixedWindow,
    SlidingWindowToken,
    LeakyBucket,
    TokenBucket,
    Concurrency
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientPlatformType
{
    WebBrowser,
    DesktopWindows,
    DesktopMac,
    DesktopLinux,
    MobileAndroid,
    MobileIos,
    CliTool,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiEndpointGroup
{
    Projects,
    Adaptation,
    Characters,
    Scenes,
    Media,
    Models,
    Settings,
    Export,
    Webhooks,
    Audit
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebSocketStateKind
{
    None,
    Connecting,
    Open,
    Closing,
    Closed,
    Aborted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SseEventType
{
    Ping,
    JobProgress,
    JobCompleted,
    JobFailed,
    Notification,
    StateChanged
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthTokenScope
{
    Read,
    Write,
    Admin,
    Export,
    FullAccess
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserSessionState
{
    Anonymous,
    Authenticated,
    Expired,
    Revoked,
    Locked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiDiagnosticFlag
{
    None,
    LogPayloads,
    TraceHttp,
    MeasurePerformance,
    SimulateErrors,
    VerboseLog
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExportDownloadFormat
{
    Mp4,
    Zip,
    Json,
    Fountain,
    Pdf,
    Srt
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebhookEventKind
{
    ProjectCreated,
    ProjectUpdated,
    AdaptationFinished,
    RenderCompleted,
    RenderFailed,
    ExportReady
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebhookDeliveryStatus
{
    Pending,
    Delivered,
    Failed,
    Retrying,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditActionKind
{
    Create,
    Read,
    Update,
    Delete,
    Execute,
    Export,
    Login,
    Logout
}

public static class ProtocolEnumExtensions
{
    public static ApiDiagnosticFlag ParseApiDiagnosticFlag(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "log_payloads" or "logpayloads" => ApiDiagnosticFlag.LogPayloads,
                "trace_http" or "tracehttp" => ApiDiagnosticFlag.TraceHttp,
                "measure_performance" or "measureperformance" => ApiDiagnosticFlag.MeasurePerformance,
                "simulate_errors" or "simulateerrors" => ApiDiagnosticFlag.SimulateErrors,
                "verbose_log" or "verboselog" => ApiDiagnosticFlag.VerboseLog,
                _ => ApiDiagnosticFlag.None
            };

    public static ApiEndpointGroup ParseApiEndpointGroup(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "adaptation" => ApiEndpointGroup.Adaptation,
                "characters" => ApiEndpointGroup.Characters,
                "scenes" => ApiEndpointGroup.Scenes,
                "media" => ApiEndpointGroup.Media,
                "models" => ApiEndpointGroup.Models,
                "settings" => ApiEndpointGroup.Settings,
                "export" => ApiEndpointGroup.Export,
                "webhooks" => ApiEndpointGroup.Webhooks,
                "audit" => ApiEndpointGroup.Audit,
                _ => ApiEndpointGroup.Projects
            };

    public static ApiVersionTag ParseApiVersionTag(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "v2" => ApiVersionTag.V2,
                "beta" => ApiVersionTag.Beta,
                "preview" => ApiVersionTag.Preview,
                "deprecated" => ApiVersionTag.Deprecated,
                _ => ApiVersionTag.V1
            };

    public static AuditActionKind ParseAuditActionKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "read" => AuditActionKind.Read,
                "update" => AuditActionKind.Update,
                "delete" => AuditActionKind.Delete,
                "execute" => AuditActionKind.Execute,
                "export" => AuditActionKind.Export,
                "login" => AuditActionKind.Login,
                "logout" => AuditActionKind.Logout,
                _ => AuditActionKind.Create
            };

    public static AuthSchemeType ParseAuthSchemeType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "bearer" => AuthSchemeType.Bearer,
                "api_key" or "apikey" => AuthSchemeType.ApiKey,
                "basic" => AuthSchemeType.Basic,
                "oauth2" or "oauth" => AuthSchemeType.OAuth2,
                "cookie" => AuthSchemeType.Cookie,
                _ => AuthSchemeType.None
            };

    public static AuthTokenScope ParseAuthTokenScope(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "write" => AuthTokenScope.Write,
                "admin" => AuthTokenScope.Admin,
                "export" => AuthTokenScope.Export,
                "full_access" or "fullaccess" or "full" => AuthTokenScope.FullAccess,
                _ => AuthTokenScope.Read
            };

    public static CacheControlDirective ParseCacheControlDirective(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "no-store" or "nostore" => CacheControlDirective.NoStore,
                "max-age" or "maxage" => CacheControlDirective.MaxAge,
                "must-revalidate" or "mustrevalidate" => CacheControlDirective.MustRevalidate,
                "private" => CacheControlDirective.Private,
                "public" => CacheControlDirective.Public,
                "immutable" => CacheControlDirective.Immutable,
                _ => CacheControlDirective.NoCache
            };

    public static ClientPlatformType ParseClientPlatformType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "web_browser" or "web" => ClientPlatformType.WebBrowser,
                "desktop_windows" or "windows" => ClientPlatformType.DesktopWindows,
                "desktop_mac" or "mac" or "macos" => ClientPlatformType.DesktopMac,
                "desktop_linux" or "linux" => ClientPlatformType.DesktopLinux,
                "mobile_android" or "android" => ClientPlatformType.MobileAndroid,
                "mobile_ios" or "ios" => ClientPlatformType.MobileIos,
                "cli_tool" or "cli" => ClientPlatformType.CliTool,
                _ => ClientPlatformType.Unknown
            };

    public static ContentEncodingType ParseContentEncodingType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "gzip" => ContentEncodingType.Gzip,
                "deflate" => ContentEncodingType.Deflate,
                "br" or "brotli" => ContentEncodingType.Brotli,
                "compress" => ContentEncodingType.Compress,
                _ => ContentEncodingType.Identity
            };

    public static CorsPolicyKind ParseCorsPolicyKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "same_origin" or "sameorigin" => CorsPolicyKind.SameOrigin,
                "strict_restricted" or "strictrestricted" or "strict" => CorsPolicyKind.StrictRestricted,
                "custom" => CorsPolicyKind.Custom,
                _ => CorsPolicyKind.AllowAll
            };

    public static ExportDownloadFormat ParseExportDownloadFormat(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "zip" => ExportDownloadFormat.Zip,
                "json" => ExportDownloadFormat.Json,
                "fountain" => ExportDownloadFormat.Fountain,
                "pdf" => ExportDownloadFormat.Pdf,
                "srt" => ExportDownloadFormat.Srt,
                _ => ExportDownloadFormat.Mp4
            };

    public static HttpStatusCodeCategory ParseHttpStatusCodeCategory(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "informational" or "1xx" => HttpStatusCodeCategory.Informational,
                "success" or "2xx" => HttpStatusCodeCategory.Success,
                "redirection" or "3xx" => HttpStatusCodeCategory.Redirection,
                "client_error" or "clienterror" or "4xx" => HttpStatusCodeCategory.ClientError,
                "server_error" or "servererror" or "5xx" => HttpStatusCodeCategory.ServerError,
                _ => HttpStatusCodeCategory.Unknown
            };

    public static HttpVerb ParseHttpVerb(string? value) =>
            (value ?? "").Trim().ToUpperInvariant() switch
            {
                "POST" => HttpVerb.Post,
                "PUT" => HttpVerb.Put,
                "DELETE" => HttpVerb.Delete,
                "PATCH" => HttpVerb.Patch,
                "HEAD" => HttpVerb.Head,
                "OPTIONS" => HttpVerb.Options,
                "CONNECT" => HttpVerb.Connect,
                "TRACE" => HttpVerb.Trace,
                _ => HttpVerb.Get
            };

    public static PayloadFormatType ParsePayloadFormatType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "xml" => PayloadFormatType.Xml,
                "form_url_encoded" or "urlencoded" => PayloadFormatType.FormUrlEncoded,
                "multipart_form" or "multipart" => PayloadFormatType.MultipartForm,
                "binary_stream" or "binary" => PayloadFormatType.BinaryStream,
                "text_plain" or "text" => PayloadFormatType.TextPlain,
                _ => PayloadFormatType.Json
            };

    public static RateLimitAlgorithm ParseRateLimitAlgorithm(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "sliding_window_token" or "slidingwindow" => RateLimitAlgorithm.SlidingWindowToken,
                "leaky_bucket" or "leakybucket" => RateLimitAlgorithm.LeakyBucket,
                "token_bucket" or "tokenbucket" => RateLimitAlgorithm.TokenBucket,
                "concurrency" => RateLimitAlgorithm.Concurrency,
                _ => RateLimitAlgorithm.FixedWindow
            };

    public static SseEventType ParseSseEventType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "job_progress" or "jobprogress" => SseEventType.JobProgress,
                "job_completed" or "jobcompleted" => SseEventType.JobCompleted,
                "job_failed" or "jobfailed" => SseEventType.JobFailed,
                "notification" => SseEventType.Notification,
                "state_changed" or "statechanged" => SseEventType.StateChanged,
                _ => SseEventType.Ping
            };

    public static UserSessionState ParseUserSessionState(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "authenticated" => UserSessionState.Authenticated,
                "expired" => UserSessionState.Expired,
                "revoked" => UserSessionState.Revoked,
                "locked" => UserSessionState.Locked,
                _ => UserSessionState.Anonymous
            };

    public static WebhookDeliveryStatus ParseWebhookDeliveryStatus(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "delivered" => WebhookDeliveryStatus.Delivered,
                "failed" => WebhookDeliveryStatus.Failed,
                "retrying" => WebhookDeliveryStatus.Retrying,
                "cancelled" => WebhookDeliveryStatus.Cancelled,
                _ => WebhookDeliveryStatus.Pending
            };

    public static WebhookEventKind ParseWebhookEventKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "project_updated" or "projectupdated" => WebhookEventKind.ProjectUpdated,
                "adaptation_finished" or "adaptationfinished" => WebhookEventKind.AdaptationFinished,
                "render_completed" or "rendercompleted" => WebhookEventKind.RenderCompleted,
                "render_failed" or "renderfailed" => WebhookEventKind.RenderFailed,
                "export_ready" or "exportready" => WebhookEventKind.ExportReady,
                _ => WebhookEventKind.ProjectCreated
            };

    public static WebSocketStateKind ParseWebSocketStateKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "connecting" => WebSocketStateKind.Connecting,
                "open" => WebSocketStateKind.Open,
                "closing" => WebSocketStateKind.Closing,
                "closed" => WebSocketStateKind.Closed,
                "aborted" => WebSocketStateKind.Aborted,
                _ => WebSocketStateKind.None
            };

    public static string ToApiString(this HttpVerb verb) => verb switch
        {
            HttpVerb.Get => "GET",
            HttpVerb.Post => "POST",
            HttpVerb.Put => "PUT",
            HttpVerb.Delete => "DELETE",
            HttpVerb.Patch => "PATCH",
            HttpVerb.Head => "HEAD",
            HttpVerb.Options => "OPTIONS",
            HttpVerb.Connect => "CONNECT",
            HttpVerb.Trace => "TRACE",
            _ => "GET"
        };

    public static string ToApiString(this HttpStatusCodeCategory category) => category switch
        {
            HttpStatusCodeCategory.Informational => "informational",
            HttpStatusCodeCategory.Success => "success",
            HttpStatusCodeCategory.Redirection => "redirection",
            HttpStatusCodeCategory.ClientError => "client_error",
            HttpStatusCodeCategory.ServerError => "server_error",
            _ => "unknown"
        };

    public static string ToApiString(this CorsPolicyKind kind) => kind switch
        {
            CorsPolicyKind.AllowAll => "allow_all",
            CorsPolicyKind.SameOrigin => "same_origin",
            CorsPolicyKind.StrictRestricted => "strict_restricted",
            CorsPolicyKind.Custom => "custom",
            _ => "allow_all"
        };

    public static string ToApiString(this ContentEncodingType encoding) => encoding switch
        {
            ContentEncodingType.Gzip => "gzip",
            ContentEncodingType.Deflate => "deflate",
            ContentEncodingType.Brotli => "br",
            ContentEncodingType.Compress => "compress",
            _ => "identity"
        };

    public static string ToApiString(this CacheControlDirective directive) => directive switch
        {
            CacheControlDirective.NoCache => "no-cache",
            CacheControlDirective.NoStore => "no-store",
            CacheControlDirective.MaxAge => "max-age",
            CacheControlDirective.MustRevalidate => "must-revalidate",
            CacheControlDirective.Private => "private",
            CacheControlDirective.Public => "public",
            CacheControlDirective.Immutable => "immutable",
            _ => "no-cache"
        };

    public static string ToApiString(this AuthSchemeType scheme) => scheme switch
        {
            AuthSchemeType.Bearer => "bearer",
            AuthSchemeType.ApiKey => "api_key",
            AuthSchemeType.Basic => "basic",
            AuthSchemeType.OAuth2 => "oauth2",
            AuthSchemeType.Cookie => "cookie",
            _ => "none"
        };

    public static string ToApiString(this ApiVersionTag tag) => tag switch
        {
            ApiVersionTag.V1 => "v1",
            ApiVersionTag.V2 => "v2",
            ApiVersionTag.Beta => "beta",
            ApiVersionTag.Preview => "preview",
            ApiVersionTag.Deprecated => "deprecated",
            _ => "v1"
        };

    public static string ToApiString(this PayloadFormatType format) => format switch
        {
            PayloadFormatType.Xml => "xml",
            PayloadFormatType.FormUrlEncoded => "form_url_encoded",
            PayloadFormatType.MultipartForm => "multipart_form",
            PayloadFormatType.BinaryStream => "binary_stream",
            PayloadFormatType.TextPlain => "text_plain",
            _ => "json"
        };

    public static string ToApiString(this RateLimitAlgorithm algorithm) => algorithm switch
        {
            RateLimitAlgorithm.SlidingWindowToken => "sliding_window_token",
            RateLimitAlgorithm.LeakyBucket => "leaky_bucket",
            RateLimitAlgorithm.TokenBucket => "token_bucket",
            RateLimitAlgorithm.Concurrency => "concurrency",
            _ => "fixed_window"
        };

    public static string ToApiString(this ClientPlatformType platform) => platform switch
        {
            ClientPlatformType.WebBrowser => "web_browser",
            ClientPlatformType.DesktopWindows => "desktop_windows",
            ClientPlatformType.DesktopMac => "desktop_mac",
            ClientPlatformType.DesktopLinux => "desktop_linux",
            ClientPlatformType.MobileAndroid => "mobile_android",
            ClientPlatformType.MobileIos => "mobile_ios",
            ClientPlatformType.CliTool => "cli_tool",
            _ => "unknown"
        };

    public static string ToApiString(this ApiEndpointGroup group) => group switch
        {
            ApiEndpointGroup.Projects => "projects",
            ApiEndpointGroup.Adaptation => "adaptation",
            ApiEndpointGroup.Characters => "characters",
            ApiEndpointGroup.Scenes => "scenes",
            ApiEndpointGroup.Media => "media",
            ApiEndpointGroup.Models => "models",
            ApiEndpointGroup.Settings => "settings",
            ApiEndpointGroup.Export => "export",
            ApiEndpointGroup.Webhooks => "webhooks",
            ApiEndpointGroup.Audit => "audit",
            _ => "projects"
        };

    public static string ToApiString(this WebSocketStateKind state) => state switch
        {
            WebSocketStateKind.Connecting => "connecting",
            WebSocketStateKind.Open => "open",
            WebSocketStateKind.Closing => "closing",
            WebSocketStateKind.Closed => "closed",
            WebSocketStateKind.Aborted => "aborted",
            _ => "none"
        };

    public static string ToApiString(this SseEventType eventType) => eventType switch
        {
            SseEventType.Ping => "ping",
            SseEventType.JobProgress => "job_progress",
            SseEventType.JobCompleted => "job_completed",
            SseEventType.JobFailed => "job_failed",
            SseEventType.Notification => "notification",
            SseEventType.StateChanged => "state_changed",
            _ => "ping"
        };

    public static string ToApiString(this AuthTokenScope scope) => scope switch
        {
            AuthTokenScope.Read => "read",
            AuthTokenScope.Write => "write",
            AuthTokenScope.Admin => "admin",
            AuthTokenScope.Export => "export",
            AuthTokenScope.FullAccess => "full_access",
            _ => "read"
        };

    public static string ToApiString(this UserSessionState state) => state switch
        {
            UserSessionState.Authenticated => "authenticated",
            UserSessionState.Expired => "expired",
            UserSessionState.Revoked => "revoked",
            UserSessionState.Locked => "locked",
            _ => "anonymous"
        };

    public static string ToApiString(this ApiDiagnosticFlag flag) => flag switch
        {
            ApiDiagnosticFlag.LogPayloads => "log_payloads",
            ApiDiagnosticFlag.TraceHttp => "trace_http",
            ApiDiagnosticFlag.MeasurePerformance => "measure_performance",
            ApiDiagnosticFlag.SimulateErrors => "simulate_errors",
            ApiDiagnosticFlag.VerboseLog => "verbose_log",
            _ => "none"
        };

    public static string ToApiString(this ExportDownloadFormat format) => format switch
        {
            ExportDownloadFormat.Mp4 => "mp4",
            ExportDownloadFormat.Zip => "zip",
            ExportDownloadFormat.Json => "json",
            ExportDownloadFormat.Fountain => "fountain",
            ExportDownloadFormat.Pdf => "pdf",
            ExportDownloadFormat.Srt => "srt",
            _ => "mp4"
        };

    public static string ToApiString(this WebhookEventKind kind) => kind switch
        {
            WebhookEventKind.ProjectCreated => "project_created",
            WebhookEventKind.ProjectUpdated => "project_updated",
            WebhookEventKind.AdaptationFinished => "adaptation_finished",
            WebhookEventKind.RenderCompleted => "render_completed",
            WebhookEventKind.RenderFailed => "render_failed",
            WebhookEventKind.ExportReady => "export_ready",
            _ => "project_created"
        };

    public static string ToApiString(this WebhookDeliveryStatus status) => status switch
        {
            WebhookDeliveryStatus.Delivered => "delivered",
            WebhookDeliveryStatus.Failed => "failed",
            WebhookDeliveryStatus.Retrying => "retrying",
            WebhookDeliveryStatus.Cancelled => "cancelled",
            _ => "pending"
        };

    public static string ToApiString(this AuditActionKind action) => action switch
        {
            AuditActionKind.Create => "create",
            AuditActionKind.Read => "read",
            AuditActionKind.Update => "update",
            AuditActionKind.Delete => "delete",
            AuditActionKind.Execute => "execute",
            AuditActionKind.Export => "export",
            AuditActionKind.Login => "login",
            AuditActionKind.Logout => "logout",
            _ => "create"
        };

}
