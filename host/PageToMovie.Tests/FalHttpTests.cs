using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class FalHttpTests
{
    [Fact]
    public void TryGetObjectUrl_reads_nested_file_url()
    {
        using var doc = JsonDocument.Parse("""{"video":{"url":"https://fal.media/out.mp4"},"audio":{}}""");
        Assert.Equal("https://fal.media/out.mp4", FalHttp.TryGetObjectUrl(doc.RootElement, "video"));
        Assert.Null(FalHttp.TryGetObjectUrl(doc.RootElement, "audio"));
        Assert.Null(FalHttp.TryGetObjectUrl(doc.RootElement, "missing"));
    }

    [Fact]
    public async Task TryPostJsonAsync_sends_key_auth_and_parses_success()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"request_id":"req-1"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fal.run/") };
        using var posted = await FalHttp.TryPostJsonAsync(
            new HttpCall(http, "test-key", NullLogger.Instance), "fal-ai/stable-audio",
            new Dictionary<string, object?> { ["prompt"] = "theme" },
            "audio gen");

        Assert.NotNull(posted);
        Assert.Equal("req-1", posted!.Root.GetProperty("request_id").GetString());
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("fal-ai/stable-audio", handler.LastRelativePath);
        Assert.Equal("Key", handler.LastAuth?.Scheme);
        Assert.Equal("test-key", handler.LastAuth?.Parameter);
        Assert.Contains("theme", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryPostJsonAsync_returns_null_on_http_error()
    {
        using var handler = new StubHandler(HttpStatusCode.BadRequest, "nope");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fal.run/") };
        var posted = await FalHttp.TryPostJsonAsync(
            new HttpCall(http, "k", NullLogger.Instance), "fal-ai/stable-audio",
            new Dictionary<string, object?> { ["prompt"] = "x" },
            "audio gen");
        Assert.Null(posted);
    }

    [Fact]
    public async Task PostJsonOrThrowAsync_throws_prefixed_invalid_operation()
    {
        using var handler = new StubHandler(HttpStatusCode.BadGateway, "gpu down");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fal.run/") };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FalHttp.PostJsonOrThrowAsync(
                new HttpCall(http, "k", NullLogger.Instance), "fal-ai/flux/dev",
                new Dictionary<string, object?> { ["prompt"] = "x" },
                "Flux image gen", "Fal.ai error"));
        Assert.Equal("Fal.ai error BadGateway: gpu down", ex.Message);
    }

    [Fact]
    public async Task GetAsync_sends_key_auth_and_returns_body()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"status":"COMPLETED"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://queue.fal.run/") };
        var raw = await FalHttp.GetAsync(http, "fal-ai/hunyuan-video/requests/abc/status", "k", CancellationToken.None);
        Assert.True(raw.IsSuccess);
        Assert.Contains("COMPLETED", raw.Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("Key", handler.LastAuth?.Scheme);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpMethod? LastMethod { get; private set; }
        public string? LastRelativePath { get; private set; }
        public AuthenticationHeaderValue? LastAuth { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastRelativePath = request.RequestUri is { } uri
                ? uri.IsAbsoluteUri
                    ? uri.AbsolutePath.TrimStart('/')
                    : uri.OriginalString.TrimStart('/')
                : null;
            LastAuth = request.Headers.Authorization;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
