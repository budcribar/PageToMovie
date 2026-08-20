using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using PageToMovie.Api;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The only Files content GET is <see cref="XaiResponsesClient.OpenFileContentStreamAsync"/>
/// (<c>GET /v1/files/{id}/content</c>). Media proxy must reuse it — no second download.
/// Use the Bearer key that owns the file; never <c>GetKeyAsync(null)</c>.
/// </summary>
[Collection("env-serial")]
public sealed class XaiFileContentDownloadTests
{
    [Fact]
    public async Task OpenFileContent_uses_ApiKeyScope_and_hits_files_content()
    {
        var handler = new StubFilesHandler { Body = new byte[] { 0x00, 0x01, 0x02 } };
        using var http = new HttpClient(handler);
        var client = new XaiResponsesClient(http);

        using (ApiKeyScope.Push("xai-from-ticket"))
        await using (var stream = await client.OpenFileContentStreamAsync("file_1ed4c54f-2edd-485b-8d35-5f31c854132a"))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(new byte[] { 0x00, 0x01, 0x02 }, ms.ToArray());
        }

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/v1/files/file_1ed4c54f-2edd-485b-8d35-5f31c854132a/content", handler.Path);
        Assert.Equal("Bearer", handler.Auth?.Scheme);
        Assert.Equal("xai-from-ticket", handler.Auth?.Parameter);
    }

    [Fact]
    public async Task OpenFileContent_uses_user_provider_key_when_scope_empty()
    {
        var prev = Environment.GetEnvironmentVariable("XAI_API_KEY");
        Environment.SetEnvironmentVariable("XAI_API_KEY", "env-should-not-win");
        try
        {
            var handler = new StubFilesHandler { Body = new byte[] { 9 } };
            using var http = new HttpClient(handler);
            var keys = new StubKeys { UserId = "budcribar", Key = "xai-personal" };
            var client = new XaiResponsesClient(http, keys);

            using (UserApiCallScope.Push("budcribar"))
            await using (var stream = await client.OpenFileContentStreamAsync("file_abc"))
            {
                Assert.True(stream.CanRead);
            }

            Assert.Equal("budcribar", keys.LastUserId);
            Assert.Equal("grok", keys.LastProvider);
            Assert.Equal("xai-personal", handler.Auth?.Parameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", prev);
        }
    }

    [Fact]
    public async Task OpenFileContent_does_not_call_provider_with_null_userId()
    {
        var handler = new StubFilesHandler { Body = new byte[] { 1 } };
        using var http = new HttpClient(handler);
        var keys = new StubKeys { UserId = null, Key = "should-not-be-used" };
        var client = new XaiResponsesClient(http, keys);

        using (ApiKeyScope.Push("scoped-key"))
        {
            await using var _ = await client.OpenFileContentStreamAsync("file_abc");
        }

        Assert.Null(keys.LastUserId);
        Assert.Equal("scoped-key", handler.Auth?.Parameter);
    }

    [Fact]
    public async Task OpenFileContent_failed_status_includes_http_code_and_body()
    {
        var handler = new StubFilesHandler
        {
            Status = HttpStatusCode.Unauthorized,
            ErrorBody = "{\"code\":\"invalid_api_key\"}",
        };
        using var http = new HttpClient(handler);
        var client = new XaiResponsesClient(http);

        using (ApiKeyScope.Push("wrong-key"))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.OpenFileContentStreamAsync("file_abc"));
            Assert.Contains("401", ex.Message, StringComparison.Ordinal);
            Assert.Contains("invalid_api_key", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MediaProxy_reuses_OpenFileContentStreamAsync_on_typed_HttpClient()
    {
        var files = new StubFilesHandler { Body = new byte[] { 0x00, 0x01, 0x02, 0x03 } };
        using var xaiHttp = new HttpClient(files) { BaseAddress = new Uri("https://api.x.ai/v1/") };
        var xai = new XaiResponsesClient(xaiHttp);
        var urls = new Url404Factory();
        var ctx = new DefaultHttpContext();

        using (ApiKeyScope.Push("xai-from-ticket"))
        {
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                "https://vidgen.example/expired.mp4",
                "file_1ed4c54f-2edd-485b-8d35-5f31c854132a",
                urls,
                xai,
                ctx,
                CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, result is IStatusCodeHttpResult s ? s.StatusCode : 200);
        }

        Assert.Equal("media-proxy", urls.LastName);
        Assert.Equal(HttpMethod.Get, files.Method);
        Assert.Equal("/v1/files/file_1ed4c54f-2edd-485b-8d35-5f31c854132a/content", files.Path);
        Assert.Equal("xai-from-ticket", files.Auth?.Parameter);
        Assert.Equal(1, files.SendCount);
    }

    [Fact]
    public async Task MediaProxy_OpenFileContent_failure_is_502_not_file_not_found()
    {
        var files = new StubFilesHandler
        {
            Status = HttpStatusCode.NotFound,
            ErrorBody = "{\"error\":\"file not readable\"}",
        };
        using var xaiHttp = new HttpClient(files) { BaseAddress = new Uri("https://api.x.ai/v1/") };
        var xai = new XaiResponsesClient(xaiHttp);
        var ctx = new DefaultHttpContext();

        using (ApiKeyScope.Push("xai-from-ticket"))
        {
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                url: null,
                "file_dead",
                new Url404Factory(),
                xai,
                ctx,
                CancellationToken.None);
            Assert.Equal(StatusCodes.Status502BadGateway, result is IStatusCodeHttpResult s ? s.StatusCode : 0);
            var err = result is IValueHttpResult { Value: { } v }
                ? System.Text.Json.JsonSerializer.Serialize(v)
                : result.GetType().Name;
            Assert.Contains("Provider file download failed", err, StringComparison.Ordinal);
            Assert.Contains("file not readable", err, StringComparison.Ordinal);
            Assert.DoesNotContain("\"File not found\"", err, StringComparison.Ordinal);
        }

        Assert.Equal("/v1/files/file_dead/content", files.Path);
        Assert.Equal(1, files.SendCount);
    }

    private sealed class Url404Factory : IHttpClientFactory
    {
        public string? LastName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastName = name;
            return new HttpClient(new StubFilesHandler { Status = HttpStatusCode.NotFound })
            {
                BaseAddress = new Uri("https://example.invalid/"),
            };
        }
    }

    private sealed class StubFilesHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? ErrorBody { get; set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public AuthenticationHeaderValue? Auth { get; private set; }
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SendCount++;
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Auth = request.Headers.Authorization;
            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status)
                {
                    Content = new StringContent(ErrorBody ?? ""),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body),
            });
        }
    }

    private sealed class StubKeys : IUserApiKeyProvider
    {
        public string? UserId { get; set; }
        public string? Key { get; set; }
        public string? LastUserId { get; private set; }
        public string? LastProvider { get; private set; }

        public Task<string?> GetKeyAsync(string? userId, string providerId, CancellationToken ct = default)
        {
            LastUserId = userId;
            LastProvider = providerId;
            if (UserId is not null && userId != UserId) return Task.FromResult<string?>(null);
            return Task.FromResult(Key);
        }
    }
}
