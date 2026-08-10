using System.Text.Json.Serialization;

namespace PageToMovie.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecializedHttpHeader
{
    Authorization,
    ContentType,
    Accept,
    UserAgent,
    XApiKey,
    XCorrelationId,
    XRequestId,
    XRateLimitLimit,
    XRateLimitRemaining,
    XRateLimitReset,
    CacheControl,
    ETag,
    IfNoneMatch,
    Location,
    RetryAfter
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecializedMimeType
{
    ApplicationJson,
    ApplicationPdf,
    ApplicationZip,
    ApplicationOctetStream,
    TextPlain,
    TextHtml,
    TextCss,
    TextFountain,
    VideoMp4,
    VideoWebm,
    AudioMpeg,
    AudioWav,
    ImagePng,
    ImageJpeg,
    ImageWebp
}

public static class SpecializedHttpAndMimeExtensions
{
    public static string ToApiString(this SpecializedHttpHeader header) => header.ToHeaderName();

    public static string ToHeaderName(this SpecializedHttpHeader header) => header switch
    {
        SpecializedHttpHeader.Authorization => "Authorization",
        SpecializedHttpHeader.ContentType => "Content-Type",
        SpecializedHttpHeader.Accept => "Accept",
        SpecializedHttpHeader.UserAgent => "User-Agent",
        SpecializedHttpHeader.XApiKey => "X-Api-Key",
        SpecializedHttpHeader.XCorrelationId => "X-Correlation-Id",
        SpecializedHttpHeader.XRequestId => "X-Request-Id",
        SpecializedHttpHeader.XRateLimitLimit => "X-RateLimit-Limit",
        SpecializedHttpHeader.XRateLimitRemaining => "X-RateLimit-Remaining",
        SpecializedHttpHeader.XRateLimitReset => "X-RateLimit-Reset",
        SpecializedHttpHeader.CacheControl => "Cache-Control",
        SpecializedHttpHeader.ETag => "ETag",
        SpecializedHttpHeader.IfNoneMatch => "If-None-Match",
        SpecializedHttpHeader.Location => "Location",
        SpecializedHttpHeader.RetryAfter => "Retry-After",
        _ => "Authorization"
    };

    public static SpecializedHttpHeader ParseSpecializedHttpHeader(this string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "authorization" => SpecializedHttpHeader.Authorization,
            "content-type" or "contenttype" => SpecializedHttpHeader.ContentType,
            "accept" => SpecializedHttpHeader.Accept,
            "user-agent" or "useragent" => SpecializedHttpHeader.UserAgent,
            "x-api-key" or "xapikey" or "api-key" => SpecializedHttpHeader.XApiKey,
            "x-correlation-id" or "xcorrelationid" or "correlation-id" => SpecializedHttpHeader.XCorrelationId,
            "x-request-id" or "xrequestid" or "request-id" => SpecializedHttpHeader.XRequestId,
            "x-ratelimit-limit" or "xratelimitlimit" or "ratelimit-limit" => SpecializedHttpHeader.XRateLimitLimit,
            "x-ratelimit-remaining" or "xratelimitremaining" or "ratelimit-remaining" => SpecializedHttpHeader.XRateLimitRemaining,
            "x-ratelimit-reset" or "xratelimitreset" or "ratelimit-reset" => SpecializedHttpHeader.XRateLimitReset,
            "cache-control" or "cachecontrol" => SpecializedHttpHeader.CacheControl,
            "etag" => SpecializedHttpHeader.ETag,
            "if-none-match" or "ifnonematch" => SpecializedHttpHeader.IfNoneMatch,
            "location" => SpecializedHttpHeader.Location,
            "retry-after" or "retryafter" => SpecializedHttpHeader.RetryAfter,
            _ => SpecializedHttpHeader.Authorization
        };

    public static bool TryParseSpecializedHttpHeader(this string? value, out SpecializedHttpHeader result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = SpecializedHttpHeader.Authorization;
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        result = normalized switch
        {
            "authorization" => SpecializedHttpHeader.Authorization,
            "content-type" or "contenttype" => SpecializedHttpHeader.ContentType,
            "accept" => SpecializedHttpHeader.Accept,
            "user-agent" or "useragent" => SpecializedHttpHeader.UserAgent,
            "x-api-key" or "xapikey" or "api-key" => SpecializedHttpHeader.XApiKey,
            "x-correlation-id" or "xcorrelationid" or "correlation-id" => SpecializedHttpHeader.XCorrelationId,
            "x-request-id" or "xrequestid" or "request-id" => SpecializedHttpHeader.XRequestId,
            "x-ratelimit-limit" or "xratelimitlimit" or "ratelimit-limit" => SpecializedHttpHeader.XRateLimitLimit,
            "x-ratelimit-remaining" or "xratelimitremaining" or "ratelimit-remaining" => SpecializedHttpHeader.XRateLimitRemaining,
            "x-ratelimit-reset" or "xratelimitreset" or "ratelimit-reset" => SpecializedHttpHeader.XRateLimitReset,
            "cache-control" or "cachecontrol" => SpecializedHttpHeader.CacheControl,
            "etag" => SpecializedHttpHeader.ETag,
            "if-none-match" or "ifnonematch" => SpecializedHttpHeader.IfNoneMatch,
            "location" => SpecializedHttpHeader.Location,
            "retry-after" or "retryafter" => SpecializedHttpHeader.RetryAfter,
            _ => (SpecializedHttpHeader)(-1)
        };

        if ((int)result == -1)
        {
            result = SpecializedHttpHeader.Authorization;
            return false;
        }

        return true;
    }

    public static string ToApiString(this SpecializedMimeType mimeType) => mimeType.ToMimeTypeString();

    public static string ToMimeTypeString(this SpecializedMimeType mimeType) => mimeType switch
    {
        SpecializedMimeType.ApplicationJson => "application/json",
        SpecializedMimeType.ApplicationPdf => "application/pdf",
        SpecializedMimeType.ApplicationZip => "application/zip",
        SpecializedMimeType.ApplicationOctetStream => "application/octet-stream",
        SpecializedMimeType.TextPlain => "text/plain",
        SpecializedMimeType.TextHtml => "text/html",
        SpecializedMimeType.TextCss => "text/css",
        SpecializedMimeType.TextFountain => "text/fountain",
        SpecializedMimeType.VideoMp4 => "video/mp4",
        SpecializedMimeType.VideoWebm => "video/webm",
        SpecializedMimeType.AudioMpeg => "audio/mpeg",
        SpecializedMimeType.AudioWav => "audio/wav",
        SpecializedMimeType.ImagePng => "image/png",
        SpecializedMimeType.ImageJpeg => "image/jpeg",
        SpecializedMimeType.ImageWebp => "image/webp",
        _ => "application/json"
    };

    public static SpecializedMimeType ParseSpecializedMimeType(this string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "application/json" or "json" or "applicationjson" => SpecializedMimeType.ApplicationJson,
            "application/pdf" or "pdf" or "applicationpdf" => SpecializedMimeType.ApplicationPdf,
            "application/zip" or "zip" or "applicationzip" => SpecializedMimeType.ApplicationZip,
            "application/octet-stream" or "octet-stream" or "applicationoctetstream" => SpecializedMimeType.ApplicationOctetStream,
            "text/plain" or "plain" or "textplain" or "txt" => SpecializedMimeType.TextPlain,
            "text/html" or "html" or "texthtml" => SpecializedMimeType.TextHtml,
            "text/css" or "css" or "textcss" => SpecializedMimeType.TextCss,
            "text/fountain" or "fountain" or "textfountain" => SpecializedMimeType.TextFountain,
            "video/mp4" or "mp4" or "videomp4" => SpecializedMimeType.VideoMp4,
            "video/webm" or "webm" or "videowebm" => SpecializedMimeType.VideoWebm,
            "audio/mpeg" or "mp3" or "mpeg" or "audiompeg" => SpecializedMimeType.AudioMpeg,
            "audio/wav" or "wav" or "audiowav" => SpecializedMimeType.AudioWav,
            "image/png" or "png" or "imagepng" => SpecializedMimeType.ImagePng,
            "image/jpeg" or "jpg" or "jpeg" or "imagejpeg" => SpecializedMimeType.ImageJpeg,
            "image/webp" or "webp" or "imagewebp" => SpecializedMimeType.ImageWebp,
            _ => SpecializedMimeType.ApplicationJson
        };

    public static bool TryParseSpecializedMimeType(this string? value, out SpecializedMimeType result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = SpecializedMimeType.ApplicationJson;
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        result = normalized switch
        {
            "application/json" or "json" or "applicationjson" => SpecializedMimeType.ApplicationJson,
            "application/pdf" or "pdf" or "applicationpdf" => SpecializedMimeType.ApplicationPdf,
            "application/zip" or "zip" or "applicationzip" => SpecializedMimeType.ApplicationZip,
            "application/octet-stream" or "octet-stream" or "applicationoctetstream" => SpecializedMimeType.ApplicationOctetStream,
            "text/plain" or "plain" or "textplain" or "txt" => SpecializedMimeType.TextPlain,
            "text/html" or "html" or "texthtml" => SpecializedMimeType.TextHtml,
            "text/css" or "css" or "textcss" => SpecializedMimeType.TextCss,
            "text/fountain" or "fountain" or "textfountain" => SpecializedMimeType.TextFountain,
            "video/mp4" or "mp4" or "videomp4" => SpecializedMimeType.VideoMp4,
            "video/webm" or "webm" or "videowebm" => SpecializedMimeType.VideoWebm,
            "audio/mpeg" or "mp3" or "mpeg" or "audiompeg" => SpecializedMimeType.AudioMpeg,
            "audio/wav" or "wav" or "audiowav" => SpecializedMimeType.AudioWav,
            "image/png" or "png" or "imagepng" => SpecializedMimeType.ImagePng,
            "image/jpeg" or "jpg" or "jpeg" or "imagejpeg" => SpecializedMimeType.ImageJpeg,
            "image/webp" or "webp" or "imagewebp" => SpecializedMimeType.ImageWebp,
            _ => (SpecializedMimeType)(-1)
        };

        if ((int)result == -1)
        {
            result = SpecializedMimeType.ApplicationJson;
            return false;
        }

        return true;
    }
}
