using System.Net;
using System.Net.Http.Headers;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Files content download is the durable recovery path when a vidgen public URL 404s:
/// GET https://api.x.ai/v1/files/{file_id}/content with the same Bearer key that owns the file.
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

    private sealed class StubFilesHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? ErrorBody { get; set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public AuthenticationHeaderValue? Auth { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
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
