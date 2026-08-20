using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// HTTP/Bearer helper for the xAI video and video-edit adapters
/// (<see cref="GrokVideoClient"/>, <see cref="GrokVideoEditClient"/>).
/// Key slot comes from the catalog (model <c>providerId</c> / <c>requiredEnvKeys</c>,
/// or the catalog provider for this adapter's API base). No hardcoded
/// <c>GetKeyAsync(..., "grok")</c> and no <c>XAI_API_KEY</c> default unless that
/// name is on the catalog row.
/// </summary>
internal static class GrokProviderHttp
{
    /// <summary>
    /// Catalog provider id for <paramref name="model"/>, or for this adapter's API base
    /// when the call has no model. Throws when the model is unknown/disabled or the
    /// catalog has no provider for the API base.
    /// </summary>
    public static string CatalogProviderId(string? model = null)
    {
        if (TryCatalogProviderId(model, out var id))
            return id;
        if (!string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                $"Video: model '{model}' is not in the models catalog (or is disabled). Open Settings and pick a current model.");
        throw new InvalidOperationException(
            "Video: catalog has no provider for this adapter's API base.");
    }

    public static bool TryCatalogProviderId(string? model, out string providerId)
    {
        providerId = "";
        if (!string.IsNullOrWhiteSpace(model))
        {
            var entry = FindEnabledAdapterModel(model);
            if (entry is null || string.IsNullOrWhiteSpace(entry.ProviderId))
                return false;
            providerId = SupportedModelCatalog.NormalizeProviderId(entry.ProviderId);
            return providerId.Length > 0;
        }

        var fromBase = SupportedModelCatalog.ProviderIdForApiBase(SupportedModelCatalog.XaiApiBase);
        if (string.IsNullOrWhiteSpace(fromBase))
            return false;
        providerId = fromBase;
        return true;
    }

    /// <summary>
    /// Ambient catalog-slot key, else the first non-empty env var named on the catalog row.
    /// Null when neither is set — <see cref="IsConfigured"/> checks. Sends use
    /// <see cref="RequireApiKey"/>.
    /// </summary>
    public static string? ResolveApiKey(string? model = null)
    {
        if (!TryCatalogProviderId(model, out var providerId))
            return null;
        var ambient = ApiKeyScope.Get(providerId);
        if (!string.IsNullOrWhiteSpace(ambient))
            return ambient;
        return FirstCatalogEnvKey(model);
    }

    public static string RequireApiKey(string? model = null)
    {
        var providerId = CatalogProviderId(model);
        var key = ResolveApiKey(model);
        if (!string.IsNullOrWhiteSpace(key))
            return key;
        throw new InvalidOperationException(
            "Video: no API key for catalog provider '" + providerId
            + "'. Save the key in Settings.");
    }

    /// <summary>
    /// Same catalog slot as <see cref="ResolveApiKey"/>, then the signed-in user's key for
    /// that provider id (never <c>GetKeyAsync(null, …)</c>, never a hardcoded provider name).
    /// </summary>
    public static async Task<string?> ResolveApiKeyAsync(
        IUserApiKeyProvider? keyProvider,
        CancellationToken ct = default) =>
        await ResolveApiKeyAsync(keyProvider, model: null, ct).ConfigureAwait(false);

    public static async Task<string?> ResolveApiKeyAsync(
        IUserApiKeyProvider? keyProvider,
        string? model,
        CancellationToken ct = default)
    {
        if (!TryCatalogProviderId(model, out var providerId))
            return null;
        var ambient = ApiKeyScope.Get(providerId);
        if (!string.IsNullOrWhiteSpace(ambient))
            return ambient;
        if (keyProvider is not null && !string.IsNullOrWhiteSpace(UserApiCallScope.UserId))
        {
            var fromUser = await keyProvider.GetKeyAsync(UserApiCallScope.UserId, providerId, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromUser))
                return fromUser;
        }
        return FirstCatalogEnvKey(model);
    }

    public static async Task<string> RequireApiKeyAsync(
        IUserApiKeyProvider? keyProvider,
        string? model = null,
        CancellationToken ct = default)
    {
        var providerId = CatalogProviderId(model);
        var key = await ResolveApiKeyAsync(keyProvider, model, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(key))
            return key;
        throw new InvalidOperationException(
            "Video: no API key for catalog provider '" + providerId
            + "'. Save the key in Settings.");
    }

    public static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient http, HttpMethod method, string uri, object payload, CancellationToken ct,
        string? model = null) =>
        ProviderHttpHelpers.SendJsonAsync(
            http, method, uri, payload, ct,
            req => ProviderHttpHelpers.ApplyBearer(req, RequireApiKey(model)));

    public static Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpMethod method, string uri, HttpContent? content, CancellationToken ct,
        string? model = null) =>
        ProviderHttpHelpers.SendAsync(
            http, method, uri, content, ct,
            req => ProviderHttpHelpers.ApplyBearer(req, RequireApiKey(model)));

    internal static IReadOnlyList<string> CatalogEnvKeys(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            var entry = FindEnabledAdapterModel(model);
            return entry?.RequiredEnvKeys ?? Array.Empty<string>();
        }

        IReadOnlyList<SupportedModelEntry> entries;
        try { entries = SupportedModelCatalog.Entries; }
        catch { return Array.Empty<string>(); }
        var want = SupportedModelCatalog.XaiApiBase.Trim().TrimEnd('/');
        return entries
            .Where(e => e.Enabled
                && !string.IsNullOrWhiteSpace(e.ApiBase)
                && string.Equals(e.ApiBase.Trim().TrimEnd('/'), want, StringComparison.OrdinalIgnoreCase)
                && e.RequiredEnvKeys is { Count: > 0 })
            .SelectMany(e => e.RequiredEnvKeys)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? FirstCatalogEnvKey(string? model)
    {
        foreach (var env in CatalogEnvKeys(model))
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static SupportedModelEntry? FindEnabledAdapterModel(string model)
    {
        var video = SupportedModelCatalog.Find(model, ModelCapability.Video);
        if (video is { Enabled: true }) return video;
        var edit = SupportedModelCatalog.Find(model, ModelCapability.VideoEdit);
        return edit is { Enabled: true } ? edit : null;
    }
}
