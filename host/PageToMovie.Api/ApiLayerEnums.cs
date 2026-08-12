using System.Text.Json.Serialization;

namespace PageToMovie.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpAuthLocation
{
    Header,
    Query,
    Cookie,
    Bearer
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpRequestHeaderName
{
    Authorization,
    ContentType,
    Accept,
    UserAgent,
    XCorrelationId,
    XApiKey
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpResponseContentType
{
    Json,
    FormUrlEncoded,
    MultipartFormData,
    TextPlain,
    ApplicationOctetStream,
    EventStream
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiRateLimitScope
{
    Global,
    PerIp,
    PerUser,
    PerApiKey,
    PerEndpoint
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebSocketMessageType
{
    Text,
    Binary,
    Ping,
    Pong,
    Close
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectExportTarget
{
    Mp4,
    ZipArchive,
    JsonManifest,
    FfmpegScript,
    YouTube
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum YouTubePrivacyStatus
{
    Private,
    Unlisted,
    Public
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuthProviderId
{
    Google,
    GitHub,
    Microsoft,
    Apple
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionPersistenceMode
{
    Memory,
    Cookie,
    DistributedCache,
    Database
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorrelationIdHeader
{
    XCorrelationId,
    TraceParent,
    XRequestId
}

public static class ApiLayerEnumExtensions
{
    public static string ToHeaderName(this HttpRequestHeaderName header) => header switch
    {
        HttpRequestHeaderName.Authorization => "Authorization",
        HttpRequestHeaderName.ContentType => "Content-Type",
        HttpRequestHeaderName.Accept => "Accept",
        HttpRequestHeaderName.UserAgent => "User-Agent",
        HttpRequestHeaderName.XCorrelationId => "X-Correlation-Id",
        HttpRequestHeaderName.XApiKey => "X-Api-Key",
        _ => "Authorization"
    };

    public static string ToHeaderName(this CorrelationIdHeader header) => header switch
    {
        CorrelationIdHeader.XCorrelationId => "X-Correlation-Id",
        CorrelationIdHeader.TraceParent => "traceparent",
        CorrelationIdHeader.XRequestId => "X-Request-ID",
        _ => "X-Correlation-Id"
    };

    public static string ToApiString(this YouTubePrivacyStatus status) => status switch
    {
        YouTubePrivacyStatus.Private => "private",
        YouTubePrivacyStatus.Unlisted => "unlisted",
        YouTubePrivacyStatus.Public => "public",
        _ => "unlisted"
    };

    public static string ToApiString(this ProjectExportTarget target) => target switch
    {
        ProjectExportTarget.Mp4 => "mp4",
        ProjectExportTarget.ZipArchive => "zip",
        ProjectExportTarget.JsonManifest => "manifest",
        ProjectExportTarget.FfmpegScript => "ffmpeg",
        ProjectExportTarget.YouTube => "youtube",
        _ => "mp4"
    };

    public static string ToApiString(this OAuthProviderId provider) => provider switch
    {
        OAuthProviderId.Google => "google",
        OAuthProviderId.GitHub => "github",
        OAuthProviderId.Microsoft => "microsoft",
        OAuthProviderId.Apple => "apple",
        _ => "google"
    };

    public static string ToContentTypeString(this HttpResponseContentType contentType) => contentType switch
    {
        HttpResponseContentType.Json => "application/json",
        HttpResponseContentType.FormUrlEncoded => "application/x-www-form-urlencoded",
        HttpResponseContentType.MultipartFormData => "multipart/form-data",
        HttpResponseContentType.TextPlain => "text/plain",
        HttpResponseContentType.ApplicationOctetStream => "application/octet-stream",
        HttpResponseContentType.EventStream => "text/event-stream",
        _ => "application/json"
    };

    public static YouTubePrivacyStatus ParseYouTubePrivacyStatus(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "private" => YouTubePrivacyStatus.Private,
            "public" => YouTubePrivacyStatus.Public,
            _ => YouTubePrivacyStatus.Unlisted
        };
}
