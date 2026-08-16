namespace PageToMovie.Web.Services;

/// <summary>
/// HttpClient pipeline stage that feeds <see cref="ServerHealthState"/> from every API call:
/// gateway errors (502/503/504) and network/timeout failures report an outage; any other answer
/// (2xx, 4xx, even 500) proves the server is up. Registered once in Program.cs so the hundreds of
/// EngineApiClient call sites need no changes.
/// </summary>
public sealed class ServerHealthHandler : DelegatingHandler
{
    private readonly ServerHealthState _health;

    public ServerHealthHandler(ServerHealthState health) => _health = health;

    public ServerHealthHandler(ServerHealthState health, HttpMessageHandler inner) : base(inner) => _health = health;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ServerHealthState.IsOutageException(ex, cancellationToken))
        {
            _health.ReportFailure(ex);
            throw;
        }

        if (ServerHealthState.IsOutageStatus(resp.StatusCode))
            _health.ReportFailure($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        else
            _health.ReportSuccess();
        return resp;
    }
}
