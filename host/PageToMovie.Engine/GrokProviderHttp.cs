using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Shared xAI/Grok key resolution + per-request Bearer send used by
/// <see cref="GrokVideoClient"/> and <see cref="GrokVideoEditClient"/>.
/// Auth is applied per <see cref="HttpRequestMessage"/> — never on the shared
/// <see cref="HttpClient.DefaultRequestHeaders"/> (concurrent jobs would race).
/// </summary>
internal static class GrokProviderHttp
{
    /// <summary>Prefer ambient job/request key (multi-user), else process env.</summary>
    public static string? ResolveApiKey() =>
        ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY");

    public static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient http, HttpMethod method, string uri, object payload, CancellationToken ct) =>
        ProviderHttpHelpers.SendJsonAsync(
            http, method, uri, payload, ct,
            req => ProviderHttpHelpers.ApplyBearer(req, ResolveApiKey()));

    public static Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpMethod method, string uri, HttpContent? content, CancellationToken ct) =>
        ProviderHttpHelpers.SendAsync(
            http, method, uri, content, ct,
            req => ProviderHttpHelpers.ApplyBearer(req, ResolveApiKey()));
}
