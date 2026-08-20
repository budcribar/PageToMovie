using PageToMovie.Core.Models;
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

    /// <summary>
    /// Same order as video generation once a job/request has pushed <see cref="ApiKeyScope"/>,
    /// plus the user-id lookup when scope is empty
    /// (<see cref="UserApiCallScope.UserId"/> — never <c>null</c>, which would skip the
    /// personal key and spend the server env key on someone else's Files).
    /// Provider id comes from the catalog row that talks to this client's API base.
    /// </summary>
    public static async Task<string?> ResolveApiKeyAsync(
        IUserApiKeyProvider? keyProvider, CancellationToken ct = default)
    {
        var providerId = SupportedModelCatalog.ProviderIdForApiBase(SupportedModelCatalog.XaiApiBase);
        if (!string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(ApiKeyScope.Get(providerId)))
            return ApiKeyScope.Get(providerId);
        if (!string.IsNullOrWhiteSpace(ApiKeyScope.Current))
            return ApiKeyScope.Current;
        if (keyProvider is not null && !string.IsNullOrWhiteSpace(UserApiCallScope.UserId)
            && !string.IsNullOrWhiteSpace(providerId))
        {
            var fromUser = await keyProvider.GetKeyAsync(UserApiCallScope.UserId, providerId, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromUser))
                return fromUser;
        }
        return Environment.GetEnvironmentVariable(SupportedModelCatalog.XaiApiKeyEnv);
    }

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
