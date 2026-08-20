using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using PageToMovie.Api;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The only Files content GET is <see cref="XaiResponsesClient.OpenFileContentStreamAsync"/>
/// (<c>GET /v1/files/{id}/content</c>). Media proxy reaches it through catalog-routed
/// <see cref="IVideoClient.OpenStoredFileStreamAsync"/> — no second download.
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

        using (CatalogApiKey.PushKey(AdapterProviderId(), "xai-from-ticket"))
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
            Assert.Equal(AdapterProviderId(), keys.LastProvider);
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

        using (CatalogApiKey.PushKey(AdapterProviderId(), "scoped-key"))
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

        using (CatalogApiKey.PushKey(AdapterProviderId(), "wrong-key"))
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
        IVideoClient video = new CatalogRoutedFilesClient(new XaiResponsesClient(xaiHttp));
        var urls = new Url404Factory();
        var ctx = new DefaultHttpContext();

        using (CatalogApiKey.PushKey(AdapterProviderId(), "xai-from-ticket"))
        {
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                "https://vidgen.example/expired.mp4",
                "file_1ed4c54f-2edd-485b-8d35-5f31c854132a",
                urls,
                video,
                RequireVideoModel(),
                ctx,
                CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, result is IStatusCodeHttpResult s ? s.StatusCode : 200);
        }

        Assert.Null(urls.LastName);
        Assert.Equal(HttpMethod.Get, files.Method);
        Assert.Equal("/v1/files/file_1ed4c54f-2edd-485b-8d35-5f31c854132a/content", files.Path);
        Assert.Equal("xai-from-ticket", files.Auth?.Parameter);
        Assert.Equal(1, files.SendCount);
    }

    [Fact]
    public async Task MediaProxy_file_content_500_falls_back_to_source_url()
    {
        var files = new StubFilesHandler
        {
            Status = HttpStatusCode.InternalServerError,
            ErrorBody = "{\"error\":\"Failed to retrieve file\"}",
        };
        using var xaiHttp = new HttpClient(files) { BaseAddress = new Uri("https://api.x.ai/v1/") };
        IVideoClient video = new CatalogRoutedFilesClient(new XaiResponsesClient(xaiHttp));
        var urls = new Url200Factory(new byte[] { 0x11, 0x22, 0x33, 0x44 });
        var ctx = new DefaultHttpContext();

        using (CatalogApiKey.PushKey(AdapterProviderId(), "xai-from-ticket"))
        {
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                "https://files.x.ai/p/public.mp4",
                "file_1ed4c54f-2edd-485b-8d35-5f31c854132a",
                urls,
                video,
                RequireVideoModel(),
                ctx,
                CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, result is IStatusCodeHttpResult s ? s.StatusCode : 200);
            var warning = ctx.Response.Headers[MediaProxyHeaders.FileIdError].ToString();
            Assert.Contains("500", warning, StringComparison.Ordinal);
            Assert.Contains("Failed to retrieve file", warning, StringComparison.Ordinal);
        }

        Assert.Equal("media-proxy", urls.LastName);
        Assert.Equal(1, files.SendCount);
        Assert.Equal("/v1/files/file_1ed4c54f-2edd-485b-8d35-5f31c854132a/content", files.Path);
        Assert.Equal(1, urls.SendCount);
    }

    [Fact]
    public async Task MediaProxy_file_content_500_and_url_miss_mentions_both()
    {
        var files = new StubFilesHandler
        {
            Status = HttpStatusCode.InternalServerError,
            ErrorBody = "{\"error\":\"Failed to retrieve file\"}",
        };
        using var xaiHttp = new HttpClient(files) { BaseAddress = new Uri("https://api.x.ai/v1/") };
        IVideoClient video = new CatalogRoutedFilesClient(new XaiResponsesClient(xaiHttp));
        var ctx = new DefaultHttpContext();

        using (CatalogApiKey.PushKey(AdapterProviderId(), "xai-from-ticket"))
        {
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                "https://vidgen.example/expired.mp4",
                "file_dead",
                new Url404Factory(),
                video,
                RequireVideoModel(),
                ctx,
                CancellationToken.None);
            Assert.Equal(StatusCodes.Status502BadGateway, result is IStatusCodeHttpResult s ? s.StatusCode : 0);
            var err = result is IValueHttpResult { Value: { } v }
                ? System.Text.Json.JsonSerializer.Serialize(v)
                : result.GetType().Name;
            Assert.Contains("Provider file download failed", err, StringComparison.Ordinal);
            Assert.Contains("500", err, StringComparison.Ordinal);
            Assert.Contains("Failed to retrieve file", err, StringComparison.Ordinal);
            Assert.Contains(ClipProviderSource.SourceUrlAlsoFailedPrefix, err, StringComparison.Ordinal);
            Assert.DoesNotContain("\"File not found\"", err, StringComparison.Ordinal);
        }

        Assert.Equal("/v1/files/file_dead/content", files.Path);
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
        IVideoClient video = new CatalogRoutedFilesClient(new XaiResponsesClient(xaiHttp));
        var ctx = new DefaultHttpContext();

        using (CatalogApiKey.PushKey(AdapterProviderId(), "xai-from-ticket"))
        {
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                url: null,
                "file_dead",
                new Url404Factory(),
                video,
                RequireVideoModel(),
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

    private static string RequireVideoModel()
    {
        var provider = AdapterProviderId();
        var entry = SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .FirstOrDefault(m =>
                string.Equals(
                    SupportedModelCatalog.NormalizeProviderId(m.ProviderId),
                    provider,
                    StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        return entry.Id;
    }

    /// <summary>
    /// Test stand-in for the catalog facade: stored-file open is the same
    /// <see cref="XaiResponsesClient.OpenFileContentStreamAsync"/> product adapters use.
    /// </summary>
    private sealed class CatalogRoutedFilesClient : IVideoClient
    {
        private readonly XaiResponsesClient _files;
        public CatalogRoutedFilesClient(XaiResponsesClient files) => _files = files;
        public bool IsConfigured => true;
        public string CatalogProviderId => AdapterProviderId();

        public async Task<Stream?> OpenStoredFileStreamAsync(string fileId, string? model, CancellationToken ct) =>
            await _files.OpenFileContentStreamAsync(fileId, model, ct).ConfigureAwait(false);

        public Task<string> SubmitGenerationAsync(
            string prompt, int durationSeconds, string resolution, string model, CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null, string? startFrameImagePath = null,
            string? continueFromVideoPath = null, string? aspectRatio = null, string? extendSourceFileId = null) =>
            throw new NotSupportedException();

        public Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public StoredVideoFileRef TryGetStoredFileReference(string requestId) => StoredVideoFileRef.Empty;
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

    private sealed class Url200Factory : IHttpClientFactory
    {
        private readonly byte[] _body;
        public Url200Factory(byte[] body) => _body = body;
        public string? LastName { get; private set; }
        public int SendCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastName = name;
            return new HttpClient(new CountingHandler(_body, () => SendCount++))
            {
                BaseAddress = new Uri("https://files.x.ai/"),
            };
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            private readonly byte[] _body;
            private readonly Action _onSend;
            public CountingHandler(byte[] body, Action onSend)
            {
                _body = body;
                _onSend = onSend;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                _onSend();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_body),
                });
            }
        }
    }

    private static string AdapterProviderId()
    {
        var id = SupportedModelCatalog.ProviderIdForApiBase(SupportedModelCatalog.XaiApiBase);
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id;
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
