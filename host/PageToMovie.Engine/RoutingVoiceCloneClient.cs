using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// <see cref="IVoiceCloneClient"/> router. Selected catalog voice model → <c>providerId</c> →
/// adapter (Fal or xAI). No default provider. ElevenLabs stays on <see cref="IVoiceClient"/>.
/// </summary>
public sealed class RoutingVoiceCloneClient : IVoiceCloneClient
{
    private readonly IReadOnlyDictionary<string, IVoiceCloneClient> _byProviderId;

    public RoutingVoiceCloneClient(FalVoiceCloneClient fal, XaiVoiceCloneClient xai)
    {
        var map = new Dictionary<string, IVoiceCloneClient>(StringComparer.OrdinalIgnoreCase);
        Add(map, FalVoiceCloneClient.ApiBase, fal);
        Add(map, SupportedModelCatalog.XaiApiBase, xai);
        if (map.Count == 0)
            throw new InvalidOperationException(
                "Voice clone: no adapters registered from catalog provider ids.");
        _byProviderId = map;
    }

    public bool IsConfigured => _byProviderId.Values.Any(c => c.IsConfigured);

    public bool IsReady(SupportedModelEntry? entry)
    {
        if (entry is null)
            return IsConfigured;
        var id = SupportedModelCatalog.NormalizeProviderId(entry.ProviderId);
        return _byProviderId.TryGetValue(id, out var client) && client.IsConfigured;
    }

    public Task<string?> CloneVoiceAsync(
        string sampleAudioPath,
        string? model = null,
        CancellationToken ct = default) =>
        ClientFor(model).CloneVoiceAsync(sampleAudioPath, model, ct);

    public Task<string?> SynthesizeSpeechAsync(
        string text,
        string voiceId,
        string? model = null,
        CancellationToken ct = default) =>
        ClientFor(model).SynthesizeSpeechAsync(text, voiceId, model, ct);

    private IVoiceCloneClient ClientFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Voice model is not selected. Open Settings and choose a voice clone model.");

        var entry = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                    ?? SupportedModelCatalog.Find(model);
        if (entry is not { Enabled: true })
            throw new InvalidOperationException(
                $"Voice model '{model}' is not an enabled catalog voice model.");

        var id = SupportedModelCatalog.NormalizeProviderId(entry.ProviderId);
        if (_byProviderId.TryGetValue(id, out var client))
            return client;

        throw new InvalidOperationException(
            $"No voice-clone client for provider '{entry.ProviderId}'. " +
            "Choose Fal or the English xAI voice option in Settings.");
    }

    private static void Add(
        Dictionary<string, IVoiceCloneClient> map,
        string apiBase,
        IVoiceCloneClient client)
    {
        var id = SupportedModelCatalog.ProviderIdForApiBase(apiBase);
        if (string.IsNullOrWhiteSpace(id))
            return;
        map[id] = client;
    }
}
