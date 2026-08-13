using System;
using System.Text.Json;
using PageToMovie.Api;
using Xunit;

namespace PageToMovie.Tests;

public class SpecializedHttpAndMimeEnumsTests
{
    [Theory]
    [InlineData(SpecializedHttpHeader.Authorization, "Authorization")]
    [InlineData(SpecializedHttpHeader.ContentType, "Content-Type")]
    [InlineData(SpecializedHttpHeader.Accept, "Accept")]
    [InlineData(SpecializedHttpHeader.UserAgent, "User-Agent")]
    [InlineData(SpecializedHttpHeader.XApiKey, "X-Api-Key")]
    [InlineData(SpecializedHttpHeader.XCorrelationId, "X-Correlation-Id")]
    [InlineData(SpecializedHttpHeader.XRequestId, "X-Request-Id")]
    [InlineData(SpecializedHttpHeader.XRateLimitLimit, "X-RateLimit-Limit")]
    [InlineData(SpecializedHttpHeader.XRateLimitRemaining, "X-RateLimit-Remaining")]
    [InlineData(SpecializedHttpHeader.XRateLimitReset, "X-RateLimit-Reset")]
    [InlineData(SpecializedHttpHeader.CacheControl, "Cache-Control")]
    [InlineData(SpecializedHttpHeader.ETag, "ETag")]
    [InlineData(SpecializedHttpHeader.IfNoneMatch, "If-None-Match")]
    [InlineData(SpecializedHttpHeader.Location, "Location")]
    [InlineData(SpecializedHttpHeader.RetryAfter, "Retry-After")]
    public void SpecializedHttpHeader_ToHeaderName_And_ToApiString_ReturnsExpectedString(SpecializedHttpHeader header, string expectedName)
    {
        Assert.Equal(expectedName, header.ToHeaderName());
        Assert.Equal(expectedName, header.ToApiString());
    }

    [Theory]
    [InlineData("Authorization", SpecializedHttpHeader.Authorization)]
    [InlineData("content-type", SpecializedHttpHeader.ContentType)]
    [InlineData("accept", SpecializedHttpHeader.Accept)]
    [InlineData("user-agent", SpecializedHttpHeader.UserAgent)]
    [InlineData("x-api-key", SpecializedHttpHeader.XApiKey)]
    [InlineData("X-Correlation-Id", SpecializedHttpHeader.XCorrelationId)]
    [InlineData("x-request-id", SpecializedHttpHeader.XRequestId)]
    [InlineData("X-RateLimit-Limit", SpecializedHttpHeader.XRateLimitLimit)]
    [InlineData("x-ratelimit-remaining", SpecializedHttpHeader.XRateLimitRemaining)]
    [InlineData("x-ratelimit-reset", SpecializedHttpHeader.XRateLimitReset)]
    [InlineData("cache-control", SpecializedHttpHeader.CacheControl)]
    [InlineData("etag", SpecializedHttpHeader.ETag)]
    [InlineData("if-none-match", SpecializedHttpHeader.IfNoneMatch)]
    [InlineData("location", SpecializedHttpHeader.Location)]
    [InlineData("retry-after", SpecializedHttpHeader.RetryAfter)]
    public void SpecializedHttpHeader_Parse_ReturnsCorrectEnum(string input, SpecializedHttpHeader expected)
    {
        Assert.Equal(expected, input.ParseSpecializedHttpHeader());
        Assert.True(input.TryParseSpecializedHttpHeader(out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void SpecializedHttpHeader_TryParse_InvalidReturnsFalse()
    {
        Assert.False("invalid-header-xyz".TryParseSpecializedHttpHeader(out _));
        Assert.False(((string?)null).TryParseSpecializedHttpHeader(out _));
        Assert.Equal(SpecializedHttpHeader.Authorization, "invalid-header-xyz".ParseSpecializedHttpHeader());
        Assert.Equal(SpecializedHttpHeader.Authorization, ((string?)null).ParseSpecializedHttpHeader());
    }

    [Theory]
    [InlineData(SpecializedMimeType.ApplicationJson, "application/json")]
    [InlineData(SpecializedMimeType.ApplicationPdf, "application/pdf")]
    [InlineData(SpecializedMimeType.ApplicationZip, "application/zip")]
    [InlineData(SpecializedMimeType.ApplicationOctetStream, "application/octet-stream")]
    [InlineData(SpecializedMimeType.TextPlain, "text/plain")]
    [InlineData(SpecializedMimeType.TextHtml, "text/html")]
    [InlineData(SpecializedMimeType.TextCss, "text/css")]
    [InlineData(SpecializedMimeType.TextFountain, "text/fountain")]
    [InlineData(SpecializedMimeType.VideoMp4, "video/mp4")]
    [InlineData(SpecializedMimeType.VideoWebm, "video/webm")]
    [InlineData(SpecializedMimeType.AudioMpeg, "audio/mpeg")]
    [InlineData(SpecializedMimeType.AudioWav, "audio/wav")]
    [InlineData(SpecializedMimeType.ImagePng, "image/png")]
    [InlineData(SpecializedMimeType.ImageJpeg, "image/jpeg")]
    [InlineData(SpecializedMimeType.ImageWebp, "image/webp")]
    public void SpecializedMimeType_ToMimeTypeString_And_ToApiString_ReturnsExpectedString(SpecializedMimeType mimeType, string expectedMime)
    {
        Assert.Equal(expectedMime, mimeType.ToMimeTypeString());
        Assert.Equal(expectedMime, mimeType.ToApiString());
    }

    [Theory]
    [InlineData("application/json", SpecializedMimeType.ApplicationJson)]
    [InlineData("application/pdf", SpecializedMimeType.ApplicationPdf)]
    [InlineData("application/zip", SpecializedMimeType.ApplicationZip)]
    [InlineData("application/octet-stream", SpecializedMimeType.ApplicationOctetStream)]
    [InlineData("text/plain", SpecializedMimeType.TextPlain)]
    [InlineData("text/html", SpecializedMimeType.TextHtml)]
    [InlineData("text/css", SpecializedMimeType.TextCss)]
    [InlineData("text/fountain", SpecializedMimeType.TextFountain)]
    [InlineData("video/mp4", SpecializedMimeType.VideoMp4)]
    [InlineData("video/webm", SpecializedMimeType.VideoWebm)]
    [InlineData("audio/mpeg", SpecializedMimeType.AudioMpeg)]
    [InlineData("audio/wav", SpecializedMimeType.AudioWav)]
    [InlineData("image/png", SpecializedMimeType.ImagePng)]
    [InlineData("image/jpeg", SpecializedMimeType.ImageJpeg)]
    [InlineData("image/webp", SpecializedMimeType.ImageWebp)]
    public void SpecializedMimeType_Parse_ReturnsCorrectEnum(string input, SpecializedMimeType expected)
    {
        Assert.Equal(expected, input.ParseSpecializedMimeType());
        Assert.True(input.TryParseSpecializedMimeType(out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void SpecializedMimeType_TryParse_InvalidReturnsFalse()
    {
        Assert.False("unknown/mime".TryParseSpecializedMimeType(out _));
        Assert.False(((string?)null).TryParseSpecializedMimeType(out _));
        Assert.Equal(SpecializedMimeType.ApplicationJson, "unknown/mime".ParseSpecializedMimeType());
        Assert.Equal(SpecializedMimeType.ApplicationJson, ((string?)null).ParseSpecializedMimeType());
    }

    [Fact]
    public void SpecializedEnums_JsonSerialization_UsesStringEnumConverter()
    {
        var headerJson = JsonSerializer.Serialize(SpecializedHttpHeader.ContentType);
        Assert.Equal("\"ContentType\"", headerJson);
        Assert.Equal(SpecializedHttpHeader.ContentType, JsonSerializer.Deserialize<SpecializedHttpHeader>(headerJson));

        var mimeJson = JsonSerializer.Serialize(SpecializedMimeType.TextFountain);
        Assert.Equal("\"TextFountain\"", mimeJson);
        Assert.Equal(SpecializedMimeType.TextFountain, JsonSerializer.Deserialize<SpecializedMimeType>(mimeJson));
    }
}
