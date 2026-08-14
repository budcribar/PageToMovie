using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine.VoiceApply;

/// <summary>Fal.ai MiniMax voice-clone + speech-02-hd apply strategy.</summary>
public sealed class FalVoiceApplyStrategy : IVoiceApplyStrategy
{
    private readonly IVoiceCloneClient _client;
    private readonly VoicePreviewStore _previews;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ProjectTelemetryService? _telemetry;
    private readonly ILogger<FalVoiceApplyStrategy> _log;

    public FalVoiceApplyStrategy(
        IVoiceCloneClient client,
        VoicePreviewStore previews,
        IHttpClientFactory httpFactory,
        ILogger<FalVoiceApplyStrategy> log,
        ProjectTelemetryService? telemetry = null)
    {
        _client = client;
        _previews = previews;
        _httpFactory = httpFactory;
        _log = log;
        _telemetry = telemetry;
    }

    public string ProviderId =>
        SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice)?.ProviderId
        ?? SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(e => e.Provider == ModelProviderFamily.Fal)?.ProviderId
        ?? "";

    public bool IsConfigured => _client.IsConfigured;

    public bool CanHandle(SupportedModelEntry? cloneModel)
    {
        if (cloneModel is null) return false;
        if (cloneModel.Provider == ModelProviderFamily.Fal) return true;
        if (cloneModel.ProviderId.Equals("fal", StringComparison.OrdinalIgnoreCase)) return true;
        if (cloneModel.Id.StartsWith("fal-ai/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<VoiceApplyResult> ApplyAsync(VoiceApplyContext ctx, CancellationToken ct = default)
    {
        if (!_client.IsConfigured)
            return new VoiceApplyResult
            {
                Ok = false,
                Error = "Connect Fal (FAL_API_KEY) in Settings for MiniMax voice clone, or switch the voice model to ElevenLabs.",
            };

        var cloneModelId = ctx.CloneModel?.Id
                           ?? SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice)?.Id
                           ?? "";
        var providerId = ctx.CloneModel?.ProviderId
                         ?? SupportedModelCatalog.CatalogProviderId(cloneModelId, "voice")
                         ?? ProviderId;
        var voiceId = await _client.CloneVoiceAsync(ctx.SamplePath, cloneModelId, ct).ConfigureAwait(false);
        if (_telemetry is not null)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                ProjectId = ctx.ProjectId,
                Kind = "voice_clone",
                Mode = "voice_clone",
                Model = cloneModelId,
                Provider = providerId,
                CharKey = ctx.CharKey,
                EstimatedUsd = ctx.CloneModel?.CostPerCloneUsd,
                Ok = !string.IsNullOrWhiteSpace(voiceId),
                Error = string.IsNullOrWhiteSpace(voiceId) ? "Voice clone failed" : null,
            }, ct).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(voiceId))
            return new VoiceApplyResult { Ok = false, Error = "Fal MiniMax voice clone failed — see server logs." };

        var label = string.IsNullOrWhiteSpace(ctx.VoiceLabel) ? "Personal clone" : ctx.VoiceLabel.Trim();
        var profile = $"Provider voice ({providerId}:{voiceId}). MiniMax clone from operator sample.";
        _previews.PersistSeed(ctx.ProjectId, ctx.CharKey, providerId, voiceId, label, profile);

        string? previewRel = null;
        string? previewUrl = null;
        var ttsText = VoicePreviewStore.DefaultPreviewText(ctx.PreviewText);
        var speakModelId = ctx.SpeakModel?.Id
                           ?? SupportedModelCatalog.FirstEnabledSpeakModelId(providerId)
                           ?? "";
        var speakProvider = ctx.SpeakModel?.ProviderId
                            ?? SupportedModelCatalog.CatalogProviderId(speakModelId, "tts")
                            ?? providerId;
        try
        {
            var audioUrl = await _client.SynthesizeSpeechAsync(ttsText, voiceId, speakModelId, ct)
                .ConfigureAwait(false);
            var estimatedUsd = ctx.SpeakModel?.CostPerThousandCharsUsd is { } rate
                ? Math.Round(rate * ttsText.Length / 1000.0, 4)
                : (double?)null;
            if (_telemetry is not null)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    ProjectId = ctx.ProjectId,
                    Kind = "tts",
                    Mode = "dialogue_tts",
                    Model = speakModelId,
                    Provider = speakProvider,
                    CharKey = ctx.CharKey,
                    PromptChars = ttsText.Length,
                    EstimatedUsd = estimatedUsd,
                    Ok = !string.IsNullOrWhiteSpace(audioUrl),
                    Error = string.IsNullOrWhiteSpace(audioUrl) ? "Speech synthesis failed" : null,
                }, ct).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(audioUrl))
            {
                var bytes = await DownloadBytesAsync(audioUrl, ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    (previewRel, previewUrl) = await _previews.WriteAsync(
                        ctx.ProjectId, ctx.CharKey, bytes, ".mp3", ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Fal TTS preview failed after successful clone — seed still saved");
        }

        _log.LogInformation(
            "Fal apply {Project}/{Char} voiceId={VoiceId}",
            ctx.ProjectId, ctx.CharKey, voiceId);

        return new VoiceApplyResult
        {
            Ok = true,
            ProviderId = providerId,
            ProviderVoiceId = voiceId,
            ModelId = cloneModelId,
            UsedMock = false,
            PreviewRelativePath = previewRel,
            PreviewUrl = previewUrl,
            VoiceLabel = label,
            Message = "Voice applied via Fal MiniMax. Id stored on this character.",
            EstimatedCloneUsd = ctx.CloneModel?.CostPerCloneUsd,
        };
    }

    private async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to download TTS audio from {Url}", url);
            return null;
        }
    }
}
