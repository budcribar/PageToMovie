using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The HttpClient pipeline stage that classifies every API answer for
/// <see cref="ServerHealthState"/>: gateway errors and network/timeout failures mean Down; any
/// real answer from the server (2xx, 4xx, 500) means Up and triggers recovery.
/// </summary>
public class ServerHealthHandlerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        public StubHandler(HttpStatusCode status)
            : this((_, _) => Task.FromResult(new HttpResponseMessage(status))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _send(request, cancellationToken);
    }

    private static (HttpClient Http, ServerHealthState State) Build(HttpMessageHandler inner)
    {
        var state = new ServerHealthState { Probe = null };
        var http = new HttpClient(new ServerHealthHandler(state, inner)) { BaseAddress = new Uri("http://api.test/") };
        return (http, state);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Gateway_status_reports_down(HttpStatusCode status)
    {
        var (http, state) = Build(new StubHandler(status));
        using var resp = await http.GetAsync("/api/projects");
        Assert.Equal(status, resp.StatusCode); // response is passed through untouched
        Assert.Equal(ServerHealth.Down, state.Health);
        Assert.Contains(((int)status).ToString(), state.LastError);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Answered_status_does_not_report_down(HttpStatusCode status)
    {
        var (http, state) = Build(new StubHandler(status));
        using var resp = await http.GetAsync("/api/projects");
        Assert.Equal(ServerHealth.Up, state.Health);
    }

    [Fact]
    public async Task Network_failure_reports_down_and_rethrows()
    {
        var (http, state) = Build(new StubHandler((_, _) => throw new HttpRequestException("TypeError: Failed to fetch")));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/api/projects"));
        Assert.Equal(ServerHealth.Down, state.Health);
        Assert.Equal("TypeError: Failed to fetch", state.LastError);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_an_outage()
    {
        var (http, state) = Build(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cts = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync("/api/slow", cts.Token));
        Assert.Equal(ServerHealth.Up, state.Health);
    }

    [Fact]
    public async Task Any_answer_after_an_outage_starts_recovery()
    {
        var status = HttpStatusCode.BadGateway;
        var (http, state) = Build(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var recovered = 0;
        state.Recovered += () => { recovered++; return Task.CompletedTask; };

        (await http.GetAsync("/api/projects")).Dispose();
        Assert.Equal(ServerHealth.Down, state.Health);

        status = HttpStatusCode.Unauthorized; // server is answering again, even if it says no
        (await http.GetAsync("/api/projects")).Dispose();
        await Task.Delay(30);

        Assert.Equal(1, recovered);
        Assert.Equal(ServerHealth.Up, state.Health);
    }
}
