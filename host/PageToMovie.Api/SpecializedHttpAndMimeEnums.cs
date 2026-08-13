using System.Text.Json.Serialization;
using PageToMovie.Core.Utils;

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
    private static readonly Dictionary<string, SpecializedHttpHeader> HttpHeaderAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = SpecializedHttpHeader.Authorization,
            ["content-type"] = SpecializedHttpHeader.ContentType,
            ["contenttype"] = SpecializedHttpHeader.ContentType,
            ["accept"] = SpecializedHttpHeader.Accept,
            ["user-agent"] = SpecializedHttpHeader.UserAgent,
            ["useragent"] = SpecializedHttpHeader.UserAgent,
            ["x-api-key"] = SpecializedHttpHeader.XApiKey,
            ["xapikey"] = SpecializedHttpHeader.XApiKey,
            ["api-key"] = SpecializedHttpHeader.XApiKey,
            ["x-correlation-id"] = SpecializedHttpHeader.XCorrelationId,
            ["xcorrelationid"] = SpecializedHttpHeader.XCorrelationId,
            ["correlation-id"] = SpecializedHttpHeader.XCorrelationId,
            ["x-request-id"] = SpecializedHttpHeader.XRequestId,
            ["xrequestid"] = SpecializedHttpHeader.XRequestId,
            ["request-id"] = SpecializedHttpHeader.XRequestId,
            ["x-ratelimit-limit"] = SpecializedHttpHeader.XRateLimitLimit,
            ["xratelimitlimit"] = SpecializedHttpHeader.XRateLimitLimit,
            ["ratelimit-limit"] = SpecializedHttpHeader.XRateLimitLimit,
            ["x-ratelimit-remaining"] = SpecializedHttpHeader.XRateLimitRemaining,
            ["xratelimitremaining"] = SpecializedHttpHeader.XRateLimitRemaining,
            ["ratelimit-remaining"] = SpecializedHttpHeader.XRateLimitRemaining,
            ["x-ratelimit-reset"] = SpecializedHttpHeader.XRateLimitReset,
            ["xratelimitreset"] = SpecializedHttpHeader.XRateLimitReset,
            ["ratelimit-reset"] = SpecializedHttpHeader.XRateLimitReset,
            ["cache-control"] = SpecializedHttpHeader.CacheControl,
            ["cachecontrol"] = SpecializedHttpHeader.CacheControl,
            ["etag"] = SpecializedHttpHeader.ETag,
            ["if-none-match"] = SpecializedHttpHeader.IfNoneMatch,
            ["ifnonematch"] = SpecializedHttpHeader.IfNoneMatch,
            ["location"] = SpecializedHttpHeader.Location,
            ["retry-after"] = SpecializedHttpHeader.RetryAfter,
            ["retryafter"] = SpecializedHttpHeader.RetryAfter,
        };

    private static readonly Dictionary<string, SpecializedMimeType> MimeTypeAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [JsonKeys.ApplicationJson] = SpecializedMimeType.ApplicationJson,
            ["json"] = SpecializedMimeType.ApplicationJson,
            ["applicationjson"] = SpecializedMimeType.ApplicationJson,
            ["application/pdf"] = SpecializedMimeType.ApplicationPdf,
            ["pdf"] = SpecializedMimeType.ApplicationPdf,
            ["applicationpdf"] = SpecializedMimeType.ApplicationPdf,
            ["application/zip"] = SpecializedMimeType.ApplicationZip,
            ["zip"] = SpecializedMimeType.ApplicationZip,
            ["applicationzip"] = SpecializedMimeType.ApplicationZip,
            ["application/octet-stream"] = SpecializedMimeType.ApplicationOctetStream,
            ["octet-stream"] = SpecializedMimeType.ApplicationOctetStream,
            ["applicationoctetstream"] = SpecializedMimeType.ApplicationOctetStream,
            ["text/plain"] = SpecializedMimeType.TextPlain,
            ["plain"] = SpecializedMimeType.TextPlain,
            ["textplain"] = SpecializedMimeType.TextPlain,
            ["txt"] = SpecializedMimeType.TextPlain,
            ["text/html"] = SpecializedMimeType.TextHtml,
            ["html"] = SpecializedMimeType.TextHtml,
            ["texthtml"] = SpecializedMimeType.TextHtml,
            ["text/css"] = SpecializedMimeType.TextCss,
            ["css"] = SpecializedMimeType.TextCss,
            ["textcss"] = SpecializedMimeType.TextCss,
            ["text/fountain"] = SpecializedMimeType.TextFountain,
            ["fountain"] = SpecializedMimeType.TextFountain,
            ["textfountain"] = SpecializedMimeType.TextFountain,
            ["video/mp4"] = SpecializedMimeType.VideoMp4,
            ["mp4"] = SpecializedMimeType.VideoMp4,
            ["videomp4"] = SpecializedMimeType.VideoMp4,
            ["video/webm"] = SpecializedMimeType.VideoWebm,
            ["webm"] = SpecializedMimeType.VideoWebm,
            ["videowebm"] = SpecializedMimeType.VideoWebm,
            ["audio/mpeg"] = SpecializedMimeType.AudioMpeg,
            ["mp3"] = SpecializedMimeType.AudioMpeg,
            ["mpeg"] = SpecializedMimeType.AudioMpeg,
            ["audiompeg"] = SpecializedMimeType.AudioMpeg,
            ["audio/wav"] = SpecializedMimeType.AudioWav,
            ["wav"] = SpecializedMimeType.AudioWav,
            ["audiowav"] = SpecializedMimeType.AudioWav,
            ["image/png"] = SpecializedMimeType.ImagePng,
            ["png"] = SpecializedMimeType.ImagePng,
            ["imagepng"] = SpecializedMimeType.ImagePng,
            ["image/jpeg"] = SpecializedMimeType.ImageJpeg,
            ["jpg"] = SpecializedMimeType.ImageJpeg,
            ["jpeg"] = SpecializedMimeType.ImageJpeg,
            ["imagejpeg"] = SpecializedMimeType.ImageJpeg,
            ["image/webp"] = SpecializedMimeType.ImageWebp,
            ["webp"] = SpecializedMimeType.ImageWebp,
            ["imagewebp"] = SpecializedMimeType.ImageWebp,
        };

    public static string ToApiString(this SpecializedHttpHeader header) => header.ToHeaderName();
    public static string ToApiString(this SpecializedMimeType mimeType) => mimeType.ToMimeTypeString();

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

    public static SpecializedHttpHeader ParseSpecializedHttpHeader(this string? value)
    {
        value.TryParseSpecializedHttpHeader(out var result);
        return result;
    }

    public static bool TryParseSpecializedHttpHeader(this string? value, out SpecializedHttpHeader result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = SpecializedHttpHeader.Authorization;
            return false;
        }

        if (HttpHeaderAliases.TryGetValue(value.Trim(), out result))
            return true;

        result = SpecializedHttpHeader.Authorization;
        return false;
    }

    public static string ToMimeTypeString(this SpecializedMimeType mimeType) => mimeType switch
    {
        SpecializedMimeType.ApplicationJson => JsonKeys.ApplicationJson,
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
        _ => JsonKeys.ApplicationJson
    };

    public static SpecializedMimeType ParseSpecializedMimeType(this string? value)
    {
        value.TryParseSpecializedMimeType(out var result);
        return result;
    }

    public static bool TryParseSpecializedMimeType(this string? value, out SpecializedMimeType result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = SpecializedMimeType.ApplicationJson;
            return false;
        }

        if (MimeTypeAliases.TryGetValue(value.Trim(), out result))
            return true;

        result = SpecializedMimeType.ApplicationJson;
        return false;
    }
}
