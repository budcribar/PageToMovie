using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Routes <see cref="IVideoClient"/> calls to the right concrete provider client based on
/// the requested <c>model</c>'s provider in <see cref="SupportedModelCatalog"/>. Submit / poll /
/// download are three separate calls in this interface, and Grok's and Gemini's request ids
/// have different shapes (Grok: short opaque id; Gemini: an operation resource path) — rather
/// than keep a requestId→provider lookup table (extra state to go stale across restarts), this
/// tags the id itself with a small provider prefix on submit and strips it again on poll, so
/// routing stays correct with no server-side memory of in-flight jobs.
/// </summary>
public sealed class MultiProviderVideoClient : IVideoClient
{
    private const string GrokPrefix = "grok:";
    private const string GeminiPrefix = "gemini:";
    private const string FalPrefix = "fal:";

    private readonly GrokVideoClient _grok;
    private readonly GeminiVideoClient _gemini;
    private readonly FalVideoClient _fal;

    public MultiProviderVideoClient(GrokVideoClient grok, GeminiVideoClient gemini, FalVideoClient fal)
    {
        _grok = grok;
        _gemini = gemini;
        _fal = fal;
    }

    /// <summary>True when at least one provider has an API key configured.</summary>
    public bool IsConfigured => _grok.IsConfigured || _gemini.IsConfigured || _fal.IsConfigured;

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
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Video: model is required. Open Settings and choose a Video generation model.");
        var entry = SupportedModelCatalog.Find(model, ModelCapability.Video) ?? SupportedModelCatalog.Find(model);
        if (entry is null || !entry.Enabled)
            throw new InvalidOperationException(
                $"Video: model '{model}' is not in the models catalog (or is disabled). Open Settings and pick a current model.");
        var provider = entry.Provider;
        if (provider == ModelProviderFamily.Fal)
        {
            var falId = await _fal.SubmitGenerationAsync(
                prompt, durationSeconds, resolution, model, ct,
                referenceImagePaths, startFrameImagePath, continueFromVideoPath, aspectRatio, extendSourceFileId).ConfigureAwait(false);
            return FalPrefix + falId;
        }

        if (provider == ModelProviderFamily.Google)
        {
            var id = await _gemini.SubmitGenerationAsync(
                prompt, durationSeconds, resolution, model, ct,
                referenceImagePaths, startFrameImagePath, continueFromVideoPath, aspectRatio, extendSourceFileId).ConfigureAwait(false);
            return GeminiPrefix + id;
        }

        var grokId = await _grok.SubmitGenerationAsync(
            prompt, durationSeconds, resolution, model, ct,
            referenceImagePaths, startFrameImagePath, continueFromVideoPath, aspectRatio, extendSourceFileId).ConfigureAwait(false);
            return GrokPrefix + grokId;
    }

    public Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
    {
        var (client, id) = Resolve(requestId);
        return client.PollForVideoUrlAsync(id, onProgress, ct);
    }

    public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        var client = ResolveDownloadClient(url);
        return client.DownloadToFileAsync(url, destPath, ct);
    }

    public (string? FileId, long? ExpiresAtUnixSeconds) TryGetStoredFileReference(string requestId)
    {
        var (client, id) = Resolve(requestId);
        return client.TryGetStoredFileReference(id);
    }

    public IVideoClient ResolveDownloadClient(string url)
    {
        var inferred = InferProviderFromDownloadUrl(url);
        if (inferred == ModelProviderFamily.Fal)
            return _fal;
        if (inferred == ModelProviderFamily.Google)
            return _gemini;
        if (inferred == ModelProviderFamily.Xai)
            return _grok;

        if (_fal.IsConfigured)
            return _fal;
        if (_grok.IsConfigured)
            return _grok;
        if (_gemini.IsConfigured)
            return _gemini;
        return _grok;
    }

    public static ModelProviderFamily? InferProviderFromDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;
        if (host.Contains("fal.ai", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("fal.run", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("fal.media", StringComparison.OrdinalIgnoreCase))
            return ModelProviderFamily.Fal;

        if (host.Contains("googleapis", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("googleusercontent", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("google.com", StringComparison.OrdinalIgnoreCase))
            return ModelProviderFamily.Google;

        if (host.Equals("api.x.ai", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("x.ai", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("xai", StringComparison.OrdinalIgnoreCase))
            return ModelProviderFamily.Xai;

        return null;
    }

    private (IVideoClient Client, string Id) Resolve(string requestId)
    {
        var (provider, id) = ParseTaggedRequestId(requestId);
        return provider switch
        {
            ModelProviderFamily.Fal => (_fal, id),
            ModelProviderFamily.Google => (_gemini, id),
            _ => (_grok, id),
        };
    }

    public static (ModelProviderFamily Provider, string Id) ParseTaggedRequestId(string requestId)
    {
        if (requestId.StartsWith(FalPrefix, StringComparison.Ordinal))
            return (ModelProviderFamily.Fal, requestId[FalPrefix.Length..]);
        if (requestId.StartsWith(GeminiPrefix, StringComparison.Ordinal))
            return (ModelProviderFamily.Google, requestId[GeminiPrefix.Length..]);
        if (requestId.StartsWith(GrokPrefix, StringComparison.Ordinal))
            return (ModelProviderFamily.Xai, requestId[GrokPrefix.Length..]);
        return (ModelProviderFamily.Xai, requestId);
    }
}
