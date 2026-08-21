using System.Net;
using System.Text;
using PageToMovie.Core.Auth;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Login-screen probes: <c>POST /api/auth/dev-login</c> is allowed (200 + Ok=false is a no-op);
/// <c>GET /api/projects</c> must not fire while the bound session is anonymous.
/// </summary>
public class EngineApiClientAuthProbeTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_reply(request));
        }
    }

    private static EngineApiClient NewClient(RecordingHandler handler, AdminSessionService? session = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new EngineApiClient(http, session);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void CanListProjects_false_when_session_anonymous()
    {
        var session = new AdminSessionService(js: null);
        var engine = NewClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}")), session);
        Assert.False(engine.CanListProjects);
    }

    [Fact]
    public void CanListProjects_true_when_no_session_bound()
    {
        var engine = NewClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}")));
        Assert.True(engine.CanListProjects);
    }

    [Fact]
    public async Task TryDevLoginAsync_treats_200_ok_false_as_silent_no_op()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{ "ok": false, "error": "Dev login is only available when the server runs with fakes enabled." }"""));
        var engine = NewClient(handler);

        var login = await engine.TryDevLoginAsync();

        Assert.NotNull(login);
        Assert.False(login!.Ok);
        Assert.True(string.IsNullOrWhiteSpace(login.Token));
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/auth/dev-login", req.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task TryDevLoginAsync_returns_session_when_fakes_ok()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{ "ok": true, "token": "dev-jwt", "userId": "dev" }"""));
        var engine = NewClient(handler);

        var login = await engine.TryDevLoginAsync();

        Assert.NotNull(login);
        Assert.True(login!.Ok);
        Assert.Equal("dev-jwt", login.Token);
        Assert.Equal("dev", login.UserId);
    }

    [Fact]
    public async Task GetProjectsAsync_does_not_get_while_anonymous()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized, """{ "ok": false }"""));
        var session = new AdminSessionService(js: null);
        var engine = NewClient(handler, session);

        var dto = await engine.GetProjectsAsync();

        Assert.NotNull(dto);
        Assert.True(dto!.Ok);
        Assert.Empty(dto.Projects);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/projects");
    }

    [Fact]
    public async Task GetProjectsAsync_lists_when_signed_in()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{ "ok": true, "projects": [ { "id": "Demo" } ], "active": { "id": "Demo" } }"""));
        var session = new AdminSessionService(js: null);
        session.SetSession("tok", "user", roles: null, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        var engine = NewClient(handler, session);

        var dto = await engine.GetProjectsAsync();

        Assert.NotNull(dto);
        Assert.Equal("Demo", dto!.Active?.Id);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects", req.RequestUri!.AbsolutePath);
    }
}
