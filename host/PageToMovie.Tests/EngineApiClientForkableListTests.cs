using System.Net;
using System.Text;
using PageToMovie.Core.Auth;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class EngineApiClientForkableListTests
{
    private const string CatalogJson = """
        {
          "ok": true,
          "projects": [
            { "id": "Buster", "title": "Buster" },
            { "id": "Mary10", "title": "Mary10" },
            { "id": "original-buster", "title": "original-buster" }
          ]
        }
        """;

    [Fact]
    public async Task ListForkableProjectsAsync_success_returns_titles_from_json()
    {
        string? path = null;
        var handler = new CaptureHandler((req, _) =>
        {
            path = req.RequestUri?.PathAndQuery;
            return CatalogResponse();
        });
        var engine = new EngineApiClient(NewHttp(handler));

        var (projects, error) = await engine.ListForkableProjectsAsync();

        Assert.Null(error);
        Assert.Equal(3, projects.Count);
        Assert.Equal("Buster", projects[0].Title);
        Assert.Equal("Mary10", projects[1].Title);
        Assert.Equal("original-buster", projects[2].Title);
        Assert.Equal("/api/projects/forkable", path);
        Assert.True(await engine.HasEasyStartStoriesAsync());
    }

    [Fact]
    public async Task HasEasyStartStoriesAsync_false_when_catalog_empty()
    {
        var handler = new CaptureHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "ok": true, "projects": [] }""", Encoding.UTF8, "application/json"),
            });
        var engine = new EngineApiClient(NewHttp(handler));

        Assert.False(await engine.HasEasyStartStoriesAsync());
    }

    [Fact]
    public async Task ListForkableProjectsAsync_puts_identity_on_the_request_not_defaults()
    {
        string? scheme = null;
        string? token = null;
        string? userId = null;
        var handler = new CaptureHandler((req, _) =>
        {
            scheme = req.Headers.Authorization?.Scheme;
            token = req.Headers.Authorization?.Parameter;
            if (req.Headers.TryGetValues(AuthHeaders.UserId, out var ids))
                userId = ids.FirstOrDefault();
            return CatalogResponse();
        });
        var http = NewHttp(handler);
        var session = new AdminSessionService(js: null);
        session.SetSession("tok-abc", "grokbot", roles: null, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        var engine = new EngineApiClient(http, session);
        http.DefaultRequestHeaders.Clear();

        var (projects, error) = await engine.ListForkableProjectsAsync();

        Assert.Null(error);
        Assert.Equal("Buster", projects[0].Title);
        Assert.Empty(http.DefaultRequestHeaders);
        Assert.Equal("Bearer", scheme);
        Assert.Equal("tok-abc", token);
        Assert.Equal("grokbot", userId);
    }

    [Fact]
    public async Task ListForkableProjectsAsync_timeout_returns_empty_list_and_error()
    {
        var handler = new HangHandler();
        var http = NewHttp(handler);
        var engine = new EngineApiClient(http) { ForkableListTimeout = TimeSpan.FromMilliseconds(80) };

        var started = DateTime.UtcNow;
        var (projects, error) = await engine.ListForkableProjectsAsync();
        var elapsed = DateTime.UtcNow - started;

        Assert.Empty(projects);
        Assert.Equal(EngineApiClient.ForkableStoriesTimeoutMessage, error);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"timeout took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task ListForkableProjectsAsync_http_error_returns_empty_list_and_error()
    {
        var handler = new CaptureHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        var engine = new EngineApiClient(NewHttp(handler));

        var (projects, error) = await engine.ListForkableProjectsAsync();

        Assert.Empty(projects);
        Assert.Equal(EngineApiClient.ForkableStoriesFailMessage, error);
    }

    private static HttpClient NewHttp(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost") };

    private static HttpResponseMessage CatalogResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(CatalogJson, Encoding.UTF8, "application/json"),
        };

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _reply;
        public CaptureHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> reply) =>
            _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_reply(request, cancellationToken));
    }

    /// <summary>Never completes and ignores cancellation — BrowserHttpHandler hang analogue.</summary>
    private sealed class HangHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            new TaskCompletionSource<HttpResponseMessage>().Task;
    }
}
