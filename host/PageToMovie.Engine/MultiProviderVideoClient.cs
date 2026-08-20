using System.Collections.Concurrent;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Catalog-routed <see cref="IVideoClient"/> facade. A request arrives with a model id;
/// <see cref="SupportedModelCatalog.Find"/> supplies the row's <c>providerId</c>; that key
/// indexes a map of concrete adapters. No <see cref="ModelProviderFamily"/> switch, no
/// Grok-as-default, no hardcoded <c>grok:</c>/<c>gemini:</c>/<c>fal:</c> prefixes, no
/// download-URL host heuristics.
/// </summary>
public sealed class MultiProviderVideoClient : IVideoClient
{
    private readonly IReadOnlyDictionary<string, IVideoClient> _clients;
    private readonly ConcurrentDictionary<string, string> _requestProvider =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _urlProvider =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Production registration: each adapter is keyed by its catalog
    /// <see cref="IVideoClient.CatalogProviderId"/> (from <c>ProviderIdForApiBase</c>).
    /// Adding a catalog provider is a new adapter in this list — not a new if-branch.
    /// </summary>
    public MultiProviderVideoClient(GrokVideoClient grok, GeminiVideoClient gemini, FalVideoClient fal)
        : this(BindAdapters(grok, gemini, fal))
    {
    }

    /// <summary>Test / explicit map: keys are catalog <c>providers[].id</c>.</summary>
    public MultiProviderVideoClient(IReadOnlyDictionary<string, IVideoClient> clientsByProviderId)
    {
        var map = new Dictionary<string, IVideoClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, client) in clientsByProviderId)
        {
            var id = SupportedModelCatalog.NormalizeProviderId(key);
            if (string.IsNullOrWhiteSpace(id) || client is null)
                continue;
            map[id] = client;
        }
        if (map.Count == 0)
            throw new InvalidOperationException(
                "Video: no IVideoClient adapters registered from catalog provider attributes.");
        _clients = map;
    }

    public bool IsConfigured => _clients.Values.Any(c => c.IsConfigured);

    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null,
        string? aspectRatio = null,
        string? extendSourceFileId = null)
    {
        var entry = RequireVideoEntry(model);
        var client = ClientForProviderId(entry.ProviderId, model);
        var raw = await client.SubmitGenerationAsync(
            prompt, durationSeconds, resolution, model, ct,
            referenceImagePaths, startFrameImagePath, continueFromVideoPath, aspectRatio, extendSourceFileId)
            .ConfigureAwait(false);
        var providerId = SupportedModelCatalog.NormalizeProviderId(entry.ProviderId);
        var tagged = TagRequestId(providerId, raw);
        RememberRequest(tagged, providerId);
        RememberRequest(raw, providerId);
        return tagged;
    }

    public async Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
    {
        var (client, raw, providerId) = ResolveRequest(requestId);
        var url = await client.PollForVideoUrlAsync(raw, onProgress, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(providerId))
            _urlProvider[url] = providerId;
        return url;
    }

    public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct) =>
        DownloadToFileAsync(url, destPath, model: null, ct);

    public Task DownloadToFileAsync(string url, string destPath, string? model, CancellationToken ct)
    {
        var client = ResolveDownloadClient(url, model);
        return client.DownloadToFileAsync(url, destPath, ct);
    }

    public StoredVideoFileRef TryGetStoredFileReference(string requestId)
    {
        var (client, raw, _) = ResolveRequest(requestId);
        return client.TryGetStoredFileReference(raw);
    }

    public Task<Stream?> OpenStoredFileStreamAsync(string fileId, string? model, CancellationToken ct)
    {
        var client = ResolveClientForModel(model);
        return client.OpenStoredFileStreamAsync(fileId, model, ct);
    }

    public Task<string?> TryUploadVideoStreamAsync(Stream mp4, string fileName, string? model, CancellationToken ct)
    {
        var client = ResolveClientForModel(model);
        return client.TryUploadVideoStreamAsync(mp4, fileName, model, ct);
    }

    /// <summary>
    /// Model id → catalog row → <c>providerId</c> → registered adapter.
    /// Throws when the catalog has no row or no adapter is registered for that provider.
    /// </summary>
    public IVideoClient ResolveClientForModel(string? model)
    {
        var entry = RequireVideoEntry(model);
        return ClientForProviderId(entry.ProviderId, model);
    }

    /// <summary>
    /// Split a catalog-tagged request id (<c>{providerId}:{raw}</c>). The prefix must be a
    /// catalog provider id (or alias) — not a hardcoded family name.
    /// </summary>
    public static bool TrySplitTaggedRequestId(
        string? requestId,
        IEnumerable<string> catalogProviderIds,
        out string providerId,
        out string rawId)
    {
        providerId = "";
        rawId = requestId ?? "";
        if (string.IsNullOrWhiteSpace(requestId))
            return false;
        var colon = requestId.IndexOf(':');
        if (colon <= 0)
            return false;
        var prefix = requestId[..colon];
        var known = catalogProviderIds
            .Select(SupportedModelCatalog.NormalizeProviderId)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (known.Count == 0)
            return false;
        var normalized = SupportedModelCatalog.NormalizeProviderId(prefix);
        if (string.IsNullOrWhiteSpace(normalized) || !known.Contains(normalized))
            return false;
        providerId = normalized;
        rawId = requestId[(colon + 1)..];
        return true;
    }

    public static string TagRequestId(string providerId, string rawId)
    {
        var id = SupportedModelCatalog.NormalizeProviderId(providerId);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(rawId))
            return rawId ?? "";
        return id + ":" + rawId;
    }

    private IVideoClient ResolveDownloadClient(string url, string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return ResolveClientForModel(model);
        if (!string.IsNullOrWhiteSpace(url)
            && _urlProvider.TryGetValue(url, out var providerId)
            && _clients.TryGetValue(providerId, out var mapped))
            return mapped;
        throw new InvalidOperationException(
            "Video: model is required to download. Open Settings and choose a Video generation model.");
    }

    private (IVideoClient Client, string RawId, string ProviderId) ResolveRequest(string requestId)
    {
        if (TrySplitTaggedRequestId(requestId, _clients.Keys, out var taggedProvider, out var raw)
            && _clients.TryGetValue(taggedProvider, out var taggedClient))
            return (taggedClient, raw, taggedProvider);

        if (_requestProvider.TryGetValue(requestId, out var mappedProvider)
            && _clients.TryGetValue(mappedProvider, out var mappedClient))
        {
            var untagged = TrySplitTaggedRequestId(requestId, _clients.Keys, out _, out var inner)
                ? inner
                : requestId;
            return (mappedClient, untagged, mappedProvider);
        }

        throw new InvalidOperationException(
            "Video: cannot route this request — it has no catalog provider tag and no in-flight map entry. Generate again with a selected video model.");
    }

    private IVideoClient ClientForProviderId(string? providerId, string? model)
    {
        var key = SupportedModelCatalog.NormalizeProviderId(providerId);
        if (string.IsNullOrWhiteSpace(key) || !_clients.TryGetValue(key, out var client))
        {
            throw new InvalidOperationException(
                "Video: no client is registered for catalog provider '"
                + (string.IsNullOrWhiteSpace(key) ? "(empty)" : key)
                + "' (model '" + (model ?? "") + "'). Add an IVideoClient adapter for that providers[].id.");
        }
        return client;
    }

    private void RememberRequest(string? requestId, string providerId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(providerId))
            return;
        _requestProvider[requestId] = providerId;
    }

    private static Dictionary<string, IVideoClient> BindAdapters(params IVideoClient[] adapters)
    {
        var map = new Dictionary<string, IVideoClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            var id = SupportedModelCatalog.NormalizeProviderId(adapter.CatalogProviderId);
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(
                    "Video: adapter " + adapter.GetType().Name
                    + " has no catalog provider id. Bind CatalogProviderId from SupportedModelCatalog.");
            map[id] = adapter;
        }
        return map;
    }

    private static SupportedModelEntry RequireVideoEntry(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Video: model is required. Open Settings and choose a Video generation model.");
        var entry = SupportedModelCatalog.Find(model, ModelCapability.Video) ?? SupportedModelCatalog.Find(model);
        if (entry is null || !entry.Enabled)
            throw new InvalidOperationException(
                $"Video: model '{model}' is not in the models catalog (or is disabled). Open Settings and pick a current model.");
        if (string.IsNullOrWhiteSpace(entry.ProviderId))
            throw new InvalidOperationException(
                $"Video: catalog row '{entry.Id}' has no providerId.");
        return entry;
    }
}
