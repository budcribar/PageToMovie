using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Shared HTTP client + API key + logger cluster used by provider POST/poll helpers.
/// Optional progress rides along for long-running reseller submits.
/// </summary>
public sealed record HttpCall(
    HttpClient Http,
    string ApiKey,
    ILogger Log,
    CancellationToken Ct = default,
    Action<string>? OnProgress = null);
