using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine.VoiceApply;

/// <summary>ElevenLabs Instant Voice Clone + TTS apply strategy (supports mock without key).</summary>
public sealed class ElevenLabsVoiceApplyStrategy : IVoiceApplyStrategy
{
    private readonly IVoiceClient _client;
    private readonly VoicePreviewStore _previews;
    private readonly ProjectTelemetryService? _telemetry;
    private readonly ILogger<ElevenLabsVoiceApplyStrategy> _log;

    public ElevenLabsVoiceApplyStrategy(
        IVoiceClient client,
        VoicePreviewStore previews,
        ILogger<ElevenLabsVoiceApplyStrategy> log,
        ProjectTelemetryService? telemetry = null)
    {
        _client = client;
        _previews = previews;
        _log = log;
        _telemetry = telemetry;
    }

    public string ProviderId =>
        SupportedModelCatalog.Find("eleven_voice_clone", ModelCapability.Voice)?.ProviderId
        ?? SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(e => e.Provider == ModelProviderFamily.ElevenLabs)?.ProviderId
        ?? "";

    public bool IsConfigured => _client.IsConfigured;

    public bool CanHandle(SupportedModelEntry? cloneModel)
    {
        // Never claim "no model selected" — Settings must pick a voice clone model.
        if (cloneModel is null) return false;
        if (cloneModel.Provider == ModelProviderFamily.ElevenLabs) return true;
        if (cloneModel.ProviderId.Equals("elevenlabs", StringComparison.OrdinalIgnoreCase)) return true;
        if (cloneModel.Id.StartsWith("eleven_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<VoiceApplyResult> ApplyAsync(VoiceApplyContext ctx, CancellationToken ct = default)
    {
        var sample = await File.ReadAllBytesAsync(ctx.SamplePath, ct).ConfigureAwait(false);
        var fileName = Path.GetFileName(ctx.SamplePath);
        var clone = await _client.CreateCloneAsync(ctx.DisplayName, sample, fileName, ct).ConfigureAwait(false);
        if (!clone.Ok || string.IsNullOrWhiteSpace(clone.ProviderVoiceId))
            return new VoiceApplyResult { Ok = false, Error = clone.Error ?? "ElevenLabs clone failed" };

        var modelId = ctx.CloneModel?.Id
                      ?? SupportedModelCatalog.Find("eleven_voice_clone", ModelCapability.Voice)?.Id;
        var providerId = ctx.CloneModel?.ProviderId
                         ?? SupportedModelCatalog.CatalogProviderId(modelId, "voice")
                         ?? ProviderId;
        if (_telemetry is not null)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                ProjectId = ctx.ProjectId,
                Kind = "voice_clone",
                Mode = "voice_clone",
                Model = modelId,
                Provider = providerId,
                CharKey = ctx.CharKey,
                EstimatedUsd = ctx.CloneModel?.CostPerCloneUsd,
                Ok = true,
            }, ct).ConfigureAwait(false);
        }

        string label;
        if (string.IsNullOrWhiteSpace(ctx.VoiceLabel))
            label = clone.UsedMock ? "Personal clone (demo)" : "Personal clone";
        else
            label = ctx.VoiceLabel.Trim();
        var profile =
            $"Provider voice ({providerId}:{clone.ProviderVoiceId}). " +
            (clone.UsedMock
                ? "Mock clone — set ElevenLabs_API_KEY for live Instant Voice Cloning."
                : "ElevenLabs instant clone from operator sample.");

        _previews.PersistSeed(ctx.ProjectId, ctx.CharKey, providerId, clone.ProviderVoiceId, label, profile);

        string? previewRel = null;
        string? previewUrl = null;
        var ttsText = VoicePreviewStore.DefaultPreviewText(ctx.PreviewText);
        var ttsModel = ctx.SpeakModel?.Id
                       ?? SupportedModelCatalog.Find("eleven_multilingual_v2", ModelCapability.Voice)?.Id
                       ?? modelId;
        var ttsProvider = ctx.SpeakModel?.ProviderId
                          ?? SupportedModelCatalog.CatalogProviderId(ttsModel, "tts")
                          ?? providerId;
        var tts = await _client.TextToSpeechAsync(clone.ProviderVoiceId, ttsText, ttsModel ?? "", ct)
            .ConfigureAwait(false);
        if (tts.Ok && tts.AudioBytes is { Length: > 0 })
        {
            (previewRel, previewUrl) = await _previews.WriteAsync(
                ctx.ProjectId, ctx.CharKey, tts.AudioBytes, tts.FileExtension ?? ".mp3", ct)
                .ConfigureAwait(false);
            if (_telemetry is not null && !tts.UsedMock)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    ProjectId = ctx.ProjectId,
                    Kind = "tts",
                    Mode = "dialogue_tts",
                    Model = ttsModel,
                    Provider = ttsProvider,
                    CharKey = ctx.CharKey,
                    PromptChars = ttsText.Length,
                    Ok = true,
                }, ct).ConfigureAwait(false);
            }
        }

        _log.LogInformation(
            "ElevenLabs apply {Project}/{Char} voiceId={VoiceId} mock={Mock}",
            ctx.ProjectId, ctx.CharKey, clone.ProviderVoiceId, clone.UsedMock);

        return new VoiceApplyResult
        {
            Ok = true,
            ProviderId = providerId,
            ProviderVoiceId = clone.ProviderVoiceId,
            ModelId = modelId,
            UsedMock = clone.UsedMock || tts.UsedMock,
            PreviewRelativePath = previewRel,
            PreviewUrl = previewUrl,
            VoiceLabel = label,
            Message = clone.UsedMock
                ? "Demo voice applied (no ElevenLabs key). Sample + TTS preview saved."
                : "Voice applied via ElevenLabs. Id stored on this character.",
            EstimatedCloneUsd = ctx.CloneModel?.CostPerCloneUsd,
        };
    }
}
