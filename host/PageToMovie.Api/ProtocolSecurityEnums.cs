using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpStandardMethod
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
public enum HttpStatusCategory
{
    Informational,
    Success,
    Redirection,
    ClientError,
    ServerError,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorsPolicyType
{
    AllowAll,
    SameOrigin,
    StrictRestricted,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentEncodingMethod
{
    Identity,
    Gzip,
    Deflate,
    Brotli,
    Compress
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheControlHeaderDirective
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
public enum AuthenticationScheme
{
    None,
    Bearer,
    ApiKey,
    Basic,
    OAuth2,
    Cookie
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiMajorVersion
{
    V1,
    V2,
    Beta,
    Preview,
    Deprecated
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadSerializerFormat
{
    Json,
    Xml,
    FormUrlEncoded,
    MultipartForm,
    BinaryStream,
    TextPlain
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RateLimitingAlgorithm
{
    FixedWindow,
    SlidingWindowToken,
    LeakyBucket,
    TokenBucket,
    Concurrency
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientPlatformKind
{
    WebBrowser,
    DesktopWindows,
    DesktopMac,
    DesktopLinux,
    MobileIos,
    MobileAndroid,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiResourceGroup
{
    Projects,
    Adaptation,
    Characters,
    Scenes,
    Generation,
    Configuration,
    Costs,
    Admin,
    System
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebSocketStateMode
{
    Connecting,
    Open,
    Closing,
    Closed,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServerSentEventType
{
    Progress,
    StatusChange,
    LogMessage,
    Error,
    Completed,
    Heartbeat
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PermissionTokenScope
{
    Read,
    Write,
    Admin,
    Execute,
    Export,
    Full
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserAuthenticationState
{
    Anonymous,
    Authenticated,
    Expired,
    Revoked,
    Locked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiLoggingVerbosity
{
    Quiet,
    Minimal,
    Normal,
    Detailed,
    Diagnostic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExportDownloadType
{
    VideoMp4,
    AudioMp3,
    ScreenplayFountain,
    ProjectArchiveZip,
    JsonMetadata
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebhookNotificationKind
{
    JobStarted,
    JobProgress,
    JobCompleted,
    JobFailed,
    SystemAlert
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebhookDeliveryResult
{
    Success,
    TransientFailure,
    PermanentFailure,
    Skipped,
    Pending
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditTrailAction
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

public static class ProtocolSecurityEnumExtensions
{
    private const string AdminToken = "admin";
    private const string ExecuteToken = "execute";
    private const string ExportToken = "export";
    private const string SuccessToken = "success";
    public static ApiLoggingVerbosity ParseApiLoggingVerbosity(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "quiet" => ApiLoggingVerbosity.Quiet,
                "minimal" => ApiLoggingVerbosity.Minimal,
                "detailed" => ApiLoggingVerbosity.Detailed,
                "diagnostic" => ApiLoggingVerbosity.Diagnostic,
                _ => ApiLoggingVerbosity.Normal
            };

    public static ApiMajorVersion ParseApiMajorVersion(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "v2" => ApiMajorVersion.V2,
                "beta" => ApiMajorVersion.Beta,
                "preview" => ApiMajorVersion.Preview,
                "deprecated" => ApiMajorVersion.Deprecated,
                _ => ApiMajorVersion.V1
            };

    public static ApiResourceGroup ParseApiResourceGroup(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "adaptation" => ApiResourceGroup.Adaptation,
                "characters" => ApiResourceGroup.Characters,
                "scenes" => ApiResourceGroup.Scenes,
                "generation" => ApiResourceGroup.Generation,
                "configuration" => ApiResourceGroup.Configuration,
                "costs" => ApiResourceGroup.Costs,
                AdminToken => ApiResourceGroup.Admin,
                "system" => ApiResourceGroup.System,
                _ => ApiResourceGroup.Projects
            };

    public static AuditTrailAction ParseAuditTrailAction(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "create" => AuditTrailAction.Create,
                "update" => AuditTrailAction.Update,
                "delete" => AuditTrailAction.Delete,
                ExecuteToken => AuditTrailAction.Execute,
                ExportToken => AuditTrailAction.Export,
                "login" => AuditTrailAction.Login,
                "logout" => AuditTrailAction.Logout,
                _ => AuditTrailAction.Read
            };

    public static AuthenticationScheme ParseAuthenticationScheme(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "bearer" => AuthenticationScheme.Bearer,
                "api_key" or "apikey" => AuthenticationScheme.ApiKey,
                "basic" => AuthenticationScheme.Basic,
                "oauth2" or "oauth" => AuthenticationScheme.OAuth2,
                "cookie" => AuthenticationScheme.Cookie,
                _ => AuthenticationScheme.None
            };

    public static CacheControlHeaderDirective ParseCacheControlHeaderDirective(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "no-store" or "nostore" => CacheControlHeaderDirective.NoStore,
                "max-age" or "maxage" => CacheControlHeaderDirective.MaxAge,
                "must-revalidate" or "mustrevalidate" => CacheControlHeaderDirective.MustRevalidate,
                "private" => CacheControlHeaderDirective.Private,
                "public" => CacheControlHeaderDirective.Public,
                "immutable" => CacheControlHeaderDirective.Immutable,
                _ => CacheControlHeaderDirective.NoCache
            };

    public static ClientPlatformKind ParseClientPlatformKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "web_browser" or "web" => ClientPlatformKind.WebBrowser,
                "desktop_windows" or "windows" => ClientPlatformKind.DesktopWindows,
                "desktop_mac" or "mac" or "macos" => ClientPlatformKind.DesktopMac,
                "desktop_linux" or "linux" => ClientPlatformKind.DesktopLinux,
                "mobile_ios" or "ios" => ClientPlatformKind.MobileIos,
                "mobile_android" or "android" => ClientPlatformKind.MobileAndroid,
                _ => ClientPlatformKind.Unknown
            };

    public static ContentEncodingMethod ParseContentEncodingMethod(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "gzip" => ContentEncodingMethod.Gzip,
                "deflate" => ContentEncodingMethod.Deflate,
                "brotli" or "br" => ContentEncodingMethod.Brotli,
                "compress" => ContentEncodingMethod.Compress,
                _ => ContentEncodingMethod.Identity
            };

    public static CorsPolicyType ParseCorsPolicyType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "same_origin" or "sameorigin" => CorsPolicyType.SameOrigin,
                "strict_restricted" or "strictrestricted" or "strict" => CorsPolicyType.StrictRestricted,
                "custom" => CorsPolicyType.Custom,
                _ => CorsPolicyType.AllowAll
            };

    public static ExportDownloadType ParseExportDownloadType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "audio_mp3" or "mp3" => ExportDownloadType.AudioMp3,
                "screenplay_fountain" or "fountain" => ExportDownloadType.ScreenplayFountain,
                "project_archive_zip" or "zip" => ExportDownloadType.ProjectArchiveZip,
                "json_metadata" or "json" => ExportDownloadType.JsonMetadata,
                _ => ExportDownloadType.VideoMp4
            };

    public static HttpStandardMethod ParseHttpStandardMethod(string? value) =>
            (value ?? "").Trim().ToUpperInvariant() switch
            {
                "POST" => HttpStandardMethod.Post,
                "PUT" => HttpStandardMethod.Put,
                "DELETE" => HttpStandardMethod.Delete,
                "PATCH" => HttpStandardMethod.Patch,
                "HEAD" => HttpStandardMethod.Head,
                "OPTIONS" => HttpStandardMethod.Options,
                "CONNECT" => HttpStandardMethod.Connect,
                "TRACE" => HttpStandardMethod.Trace,
                _ => HttpStandardMethod.Get
            };

    public static HttpStatusCategory ParseHttpStatusCategory(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "informational" or "1xx" => HttpStatusCategory.Informational,
                SuccessToken or "2xx" => HttpStatusCategory.Success,
                "redirection" or "3xx" => HttpStatusCategory.Redirection,
                "client_error" or "clienterror" or "4xx" => HttpStatusCategory.ClientError,
                "server_error" or "servererror" or "5xx" => HttpStatusCategory.ServerError,
                _ => HttpStatusCategory.Unknown
            };

    public static PayloadSerializerFormat ParsePayloadSerializerFormat(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "xml" => PayloadSerializerFormat.Xml,
                "form_url_encoded" or "formurlencoded" => PayloadSerializerFormat.FormUrlEncoded,
                "multipart_form" or "multipart" => PayloadSerializerFormat.MultipartForm,
                "binary_stream" or "binary" => PayloadSerializerFormat.BinaryStream,
                "text_plain" or "text" => PayloadSerializerFormat.TextPlain,
                _ => PayloadSerializerFormat.Json
            };

    public static PermissionTokenScope ParsePermissionTokenScope(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "write" => PermissionTokenScope.Write,
                AdminToken => PermissionTokenScope.Admin,
                ExecuteToken => PermissionTokenScope.Execute,
                ExportToken => PermissionTokenScope.Export,
                "full" => PermissionTokenScope.Full,
                _ => PermissionTokenScope.Read
            };

    public static RateLimitingAlgorithm ParseRateLimitingAlgorithm(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "sliding_window_token" or "slidingwindow" => RateLimitingAlgorithm.SlidingWindowToken,
                "leaky_bucket" or "leakybucket" => RateLimitingAlgorithm.LeakyBucket,
                "token_bucket" or "tokenbucket" => RateLimitingAlgorithm.TokenBucket,
                "concurrency" => RateLimitingAlgorithm.Concurrency,
                _ => RateLimitingAlgorithm.FixedWindow
            };

    public static ServerSentEventType ParseServerSentEventType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "status_change" or "statuschange" => ServerSentEventType.StatusChange,
                "log_message" or "logmessage" => ServerSentEventType.LogMessage,
                "error" => ServerSentEventType.Error,
                "completed" => ServerSentEventType.Completed,
                "heartbeat" => ServerSentEventType.Heartbeat,
                _ => ServerSentEventType.Progress
            };

    public static UserAuthenticationState ParseUserAuthenticationState(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "authenticated" => UserAuthenticationState.Authenticated,
                "expired" => UserAuthenticationState.Expired,
                "revoked" => UserAuthenticationState.Revoked,
                "locked" => UserAuthenticationState.Locked,
                _ => UserAuthenticationState.Anonymous
            };

    public static WebhookDeliveryResult ParseWebhookDeliveryResult(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                SuccessToken => WebhookDeliveryResult.Success,
                "transient_failure" or "transientfailure" => WebhookDeliveryResult.TransientFailure,
                "permanent_failure" or "permanentfailure" => WebhookDeliveryResult.PermanentFailure,
                "skipped" => WebhookDeliveryResult.Skipped,
                _ => WebhookDeliveryResult.Pending
            };

    public static WebhookNotificationKind ParseWebhookNotificationKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "job_progress" or "progress" => WebhookNotificationKind.JobProgress,
                "job_completed" or "completed" => WebhookNotificationKind.JobCompleted,
                "job_failed" or "failed" => WebhookNotificationKind.JobFailed,
                "system_alert" or "alert" => WebhookNotificationKind.SystemAlert,
                _ => WebhookNotificationKind.JobStarted
            };

    public static WebSocketStateMode ParseWebSocketStateMode(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "connecting" => WebSocketStateMode.Connecting,
                "open" => WebSocketStateMode.Open,
                "closing" => WebSocketStateMode.Closing,
                "closed" => WebSocketStateMode.Closed,
                _ => WebSocketStateMode.None
            };

    public static string ToApiString(this HttpStandardMethod method) => method switch
        {
            HttpStandardMethod.Get => "GET",
            HttpStandardMethod.Post => "POST",
            HttpStandardMethod.Put => "PUT",
            HttpStandardMethod.Delete => "DELETE",
            HttpStandardMethod.Patch => "PATCH",
            HttpStandardMethod.Head => "HEAD",
            HttpStandardMethod.Options => "OPTIONS",
            HttpStandardMethod.Connect => "CONNECT",
            HttpStandardMethod.Trace => "TRACE",
            _ => "GET"
        };

    public static string ToApiString(this HttpStatusCategory category) => category switch
        {
            HttpStatusCategory.Informational => "informational",
            HttpStatusCategory.Success => SuccessToken,
            HttpStatusCategory.Redirection => "redirection",
            HttpStatusCategory.ClientError => "client_error",
            HttpStatusCategory.ServerError => "server_error",
            _ => "unknown"
        };

    public static string ToApiString(this CorsPolicyType policy) => policy switch
        {
            CorsPolicyType.AllowAll => "allow_all",
            CorsPolicyType.SameOrigin => "same_origin",
            CorsPolicyType.StrictRestricted => "strict_restricted",
            CorsPolicyType.Custom => "custom",
            _ => "allow_all"
        };

    public static string ToApiString(this ContentEncodingMethod encoding) => encoding switch
        {
            ContentEncodingMethod.Identity => "identity",
            ContentEncodingMethod.Gzip => "gzip",
            ContentEncodingMethod.Deflate => "deflate",
            ContentEncodingMethod.Brotli => "brotli",
            ContentEncodingMethod.Compress => "compress",
            _ => "identity"
        };

    public static string ToApiString(this CacheControlHeaderDirective directive) => directive switch
        {
            CacheControlHeaderDirective.NoCache => "no-cache",
            CacheControlHeaderDirective.NoStore => "no-store",
            CacheControlHeaderDirective.MaxAge => "max-age",
            CacheControlHeaderDirective.MustRevalidate => "must-revalidate",
            CacheControlHeaderDirective.Private => "private",
            CacheControlHeaderDirective.Public => "public",
            CacheControlHeaderDirective.Immutable => "immutable",
            _ => "no-cache"
        };

    public static string ToApiString(this AuthenticationScheme scheme) => scheme switch
        {
            AuthenticationScheme.None => "none",
            AuthenticationScheme.Bearer => "bearer",
            AuthenticationScheme.ApiKey => "api_key",
            AuthenticationScheme.Basic => "basic",
            AuthenticationScheme.OAuth2 => "oauth2",
            AuthenticationScheme.Cookie => "cookie",
            _ => "none"
        };

    public static string ToApiString(this ApiMajorVersion version) => version switch
        {
            ApiMajorVersion.V1 => "v1",
            ApiMajorVersion.V2 => "v2",
            ApiMajorVersion.Beta => "beta",
            ApiMajorVersion.Preview => "preview",
            ApiMajorVersion.Deprecated => "deprecated",
            _ => "v1"
        };

    public static string ToApiString(this PayloadSerializerFormat format) => format switch
        {
            PayloadSerializerFormat.Json => "json",
            PayloadSerializerFormat.Xml => "xml",
            PayloadSerializerFormat.FormUrlEncoded => "form_url_encoded",
            PayloadSerializerFormat.MultipartForm => "multipart_form",
            PayloadSerializerFormat.BinaryStream => "binary_stream",
            PayloadSerializerFormat.TextPlain => "text_plain",
            _ => "json"
        };

    public static string ToApiString(this RateLimitingAlgorithm algorithm) => algorithm switch
        {
            RateLimitingAlgorithm.FixedWindow => "fixed_window",
            RateLimitingAlgorithm.SlidingWindowToken => "sliding_window_token",
            RateLimitingAlgorithm.LeakyBucket => "leaky_bucket",
            RateLimitingAlgorithm.TokenBucket => "token_bucket",
            RateLimitingAlgorithm.Concurrency => "concurrency",
            _ => "fixed_window"
        };

    public static string ToApiString(this ClientPlatformKind platform) => platform switch
        {
            ClientPlatformKind.WebBrowser => "web_browser",
            ClientPlatformKind.DesktopWindows => "desktop_windows",
            ClientPlatformKind.DesktopMac => "desktop_mac",
            ClientPlatformKind.DesktopLinux => "desktop_linux",
            ClientPlatformKind.MobileIos => "mobile_ios",
            ClientPlatformKind.MobileAndroid => "mobile_android",
            _ => "unknown"
        };

    public static string ToApiString(this ApiResourceGroup group) => group switch
        {
            ApiResourceGroup.Projects => "projects",
            ApiResourceGroup.Adaptation => "adaptation",
            ApiResourceGroup.Characters => "characters",
            ApiResourceGroup.Scenes => "scenes",
            ApiResourceGroup.Generation => "generation",
            ApiResourceGroup.Configuration => "configuration",
            ApiResourceGroup.Costs => "costs",
            ApiResourceGroup.Admin => AdminToken,
            ApiResourceGroup.System => "system",
            _ => "projects"
        };

    public static string ToApiString(this WebSocketStateMode mode) => mode switch
        {
            WebSocketStateMode.Connecting => "connecting",
            WebSocketStateMode.Open => "open",
            WebSocketStateMode.Closing => "closing",
            WebSocketStateMode.Closed => "closed",
            _ => "none"
        };

    public static string ToApiString(this ServerSentEventType type) => type switch
        {
            ServerSentEventType.Progress => "progress",
            ServerSentEventType.StatusChange => "status_change",
            ServerSentEventType.LogMessage => "log_message",
            ServerSentEventType.Error => "error",
            ServerSentEventType.Completed => "completed",
            ServerSentEventType.Heartbeat => "heartbeat",
            _ => "progress"
        };

    public static string ToApiString(this PermissionTokenScope scope) => scope switch
        {
            PermissionTokenScope.Read => "read",
            PermissionTokenScope.Write => "write",
            PermissionTokenScope.Admin => AdminToken,
            PermissionTokenScope.Execute => ExecuteToken,
            PermissionTokenScope.Export => ExportToken,
            PermissionTokenScope.Full => "full",
            _ => "read"
        };

    public static string ToApiString(this UserAuthenticationState state) => state switch
        {
            UserAuthenticationState.Anonymous => "anonymous",
            UserAuthenticationState.Authenticated => "authenticated",
            UserAuthenticationState.Expired => "expired",
            UserAuthenticationState.Revoked => "revoked",
            UserAuthenticationState.Locked => "locked",
            _ => "anonymous"
        };

    public static string ToApiString(this ApiLoggingVerbosity verbosity) => verbosity switch
        {
            ApiLoggingVerbosity.Quiet => "quiet",
            ApiLoggingVerbosity.Minimal => "minimal",
            ApiLoggingVerbosity.Normal => "normal",
            ApiLoggingVerbosity.Detailed => "detailed",
            ApiLoggingVerbosity.Diagnostic => "diagnostic",
            _ => "normal"
        };

    public static string ToApiString(this ExportDownloadType type) => type switch
        {
            ExportDownloadType.VideoMp4 => "video_mp4",
            ExportDownloadType.AudioMp3 => "audio_mp3",
            ExportDownloadType.ScreenplayFountain => "screenplay_fountain",
            ExportDownloadType.ProjectArchiveZip => "project_archive_zip",
            ExportDownloadType.JsonMetadata => "json_metadata",
            _ => "video_mp4"
        };

    public static string ToApiString(this WebhookNotificationKind kind) => kind switch
        {
            WebhookNotificationKind.JobStarted => "job_started",
            WebhookNotificationKind.JobProgress => "job_progress",
            WebhookNotificationKind.JobCompleted => "job_completed",
            WebhookNotificationKind.JobFailed => "job_failed",
            WebhookNotificationKind.SystemAlert => "system_alert",
            _ => "job_started"
        };

    public static string ToApiString(this WebhookDeliveryResult result) => result switch
        {
            WebhookDeliveryResult.Success => SuccessToken,
            WebhookDeliveryResult.TransientFailure => "transient_failure",
            WebhookDeliveryResult.PermanentFailure => "permanent_failure",
            WebhookDeliveryResult.Skipped => "skipped",
            WebhookDeliveryResult.Pending => "pending",
            _ => "pending"
        };

    public static string ToApiString(this AuditTrailAction action) => action switch
        {
            AuditTrailAction.Create => "create",
            AuditTrailAction.Read => "read",
            AuditTrailAction.Update => "update",
            AuditTrailAction.Delete => "delete",
            AuditTrailAction.Execute => ExecuteToken,
            AuditTrailAction.Export => ExportToken,
            AuditTrailAction.Login => "login",
            AuditTrailAction.Logout => "logout",
            _ => "read"
        };

    public static bool TryParseApiLoggingVerbosity(string? value, out ApiLoggingVerbosity result)
        {
            result = ParseApiLoggingVerbosity(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseApiMajorVersion(string? value, out ApiMajorVersion result)
        {
            result = ParseApiMajorVersion(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseApiResourceGroup(string? value, out ApiResourceGroup result)
        {
            result = ParseApiResourceGroup(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseAuditTrailAction(string? value, out AuditTrailAction result)
        {
            result = ParseAuditTrailAction(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseAuthenticationScheme(string? value, out AuthenticationScheme result)
        {
            result = ParseAuthenticationScheme(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseCacheControlHeaderDirective(string? value, out CacheControlHeaderDirective result)
        {
            result = ParseCacheControlHeaderDirective(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseClientPlatformKind(string? value, out ClientPlatformKind result)
        {
            result = ParseClientPlatformKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseContentEncodingMethod(string? value, out ContentEncodingMethod result)
        {
            result = ParseContentEncodingMethod(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseCorsPolicyType(string? value, out CorsPolicyType result)
        {
            result = ParseCorsPolicyType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseExportDownloadType(string? value, out ExportDownloadType result)
        {
            result = ParseExportDownloadType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseHttpStandardMethod(string? value, out HttpStandardMethod result)
        {
            result = ParseHttpStandardMethod(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseHttpStatusCategory(string? value, out HttpStatusCategory result)
        {
            result = ParseHttpStatusCategory(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParsePayloadSerializerFormat(string? value, out PayloadSerializerFormat result)
        {
            result = ParsePayloadSerializerFormat(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParsePermissionTokenScope(string? value, out PermissionTokenScope result)
        {
            result = ParsePermissionTokenScope(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseRateLimitingAlgorithm(string? value, out RateLimitingAlgorithm result)
        {
            result = ParseRateLimitingAlgorithm(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseServerSentEventType(string? value, out ServerSentEventType result)
        {
            result = ParseServerSentEventType(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseUserAuthenticationState(string? value, out UserAuthenticationState result)
        {
            result = ParseUserAuthenticationState(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseWebhookDeliveryResult(string? value, out WebhookDeliveryResult result)
        {
            result = ParseWebhookDeliveryResult(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseWebhookNotificationKind(string? value, out WebhookNotificationKind result)
        {
            result = ParseWebhookNotificationKind(value);
            return !string.IsNullOrWhiteSpace(value);
        }

    public static bool TryParseWebSocketStateMode(string? value, out WebSocketStateMode result)
        {
            result = ParseWebSocketStateMode(value);
            return !string.IsNullOrWhiteSpace(value);
        }

}
