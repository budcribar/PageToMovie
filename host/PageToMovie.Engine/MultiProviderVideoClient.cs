using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Catalog-routed <see cref="IVideoClient"/> facade. Fail-fast: model id →
/// <see cref="SupportedModelCatalog.Find"/> → <c>providerId</c> → registered adapter.
/// Missing model, unknown/disabled catalog row, or no map entry throws. No Grok default,
/// no first-configured-provider fallback, no download-URL host inference.
/// </summary>
public sealed class MultiProviderVideoClient : IVideoClient
{
    private readonly IReadOnlyDictionary<string, IVideoClient> _clients;

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
        return TagRequestId(entry.ProviderId, raw);
    }

    public Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
    {
        var (client, raw) = ResolveRequest(requestId);
        return client.PollForVideoUrlAsync(raw, onProgress, ct);
    }

    public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct) =>
        throw new InvalidOperationException(
            "Video: model is required to download. Open Settings and choose a Video generation model.");

    public Task DownloadToFileAsync(string url, string destPath, string? model, CancellationToken ct)
    {
        var client = ResolveClientForModel(model);
        return client.DownloadToFileAsync(url, destPath, ct);
    }

    public StoredVideoFileRef TryGetStoredFileReference(string requestId)
    {
        var (client, raw) = ResolveRequest(requestId);
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
    /// Model id → catalog Video row → <c>providerId</c> → registered adapter.
    /// Throws when the model is missing, unknown, disabled, or has no map entry.
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
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                "Video: catalog provider id is required to tag a request. No default provider.");
        if (string.IsNullOrWhiteSpace(rawId))
            throw new InvalidOperationException("Video: request id is required.");
        return id + ":" + rawId;
    }

    private (IVideoClient Client, string RawId) ResolveRequest(string requestId)
    {
        if (TrySplitTaggedRequestId(requestId, _clients.Keys, out var taggedProvider, out var raw)
            && _clients.TryGetValue(taggedProvider, out var taggedClient))
            return (taggedClient, raw);

        throw new InvalidOperationException(
            "Video: cannot route this request — it has no catalog provider tag. Generate again with a selected video model.");
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
        var entry = SupportedModelCatalog.Find(model, ModelCapability.Video);
        if (entry is null || !entry.Enabled)
            throw new InvalidOperationException(
                $"Video: model '{model}' is not in the models catalog (or is disabled). Open Settings and pick a current model.");
        SupportedModelCatalog.EnsureNotVirtualWireModel(entry.Id);
        if (string.IsNullOrWhiteSpace(entry.ProviderId))
            throw new InvalidOperationException(
                $"Video: catalog row '{entry.Id}' has no providerId.");
        return entry;
    }
}
