using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine.VoiceApply;

/// <summary>
/// xAI Custom Voices + Text to Speech apply strategy. Handles a voice model whose catalog
/// provider is xAI. Requests are English (<see cref="XaiVoiceCloneClient.EnglishLanguage"/>).
/// </summary>
public sealed class XaiVoiceApplyStrategy : IVoiceApplyStrategy
{
    private readonly IVoiceCloneClient _client;
    private readonly VoicePreviewStore _previews;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ProjectTelemetryService? _telemetry;
    private readonly ILogger<XaiVoiceApplyStrategy> _log;

    public XaiVoiceApplyStrategy(
        IVoiceCloneClient client,
        VoicePreviewStore previews,
        IHttpClientFactory httpFactory,
        ILogger<XaiVoiceApplyStrategy> log,
        ProjectTelemetryService? telemetry = null)
    {
        _client = client;
        _previews = previews;
        _httpFactory = httpFactory;
        _log = log;
        _telemetry = telemetry;
    }

    public string ProviderId =>
        SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(e => e.Provider == ModelProviderFamily.Xai)?.ProviderId
        ?? SupportedModelCatalog.ProviderIdForApiBase(SupportedModelCatalog.XaiApiBase);

    public bool IsConfigured => _client.IsConfigured;

    public bool CanHandle(SupportedModelEntry? cloneModel)
    {
        if (cloneModel is null)
            return false;
        if (cloneModel.Provider == ModelProviderFamily.Xai)
            return true;
        var providerId = ProviderId;
        return providerId.Length > 0
               && cloneModel.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<VoiceApplyResult> ApplyAsync(VoiceApplyContext ctx, CancellationToken ct = default)
    {
        if (!_client.IsConfigured)
            return new VoiceApplyResult
            {
                Ok = false,
                Error = "Connect xAI in Settings for the English voice option, or switch the voice model.",
            };

        if (string.IsNullOrWhiteSpace(ctx.CloneModel?.Id))
            return new VoiceApplyResult
            {
                Ok = false,
                Error = "No xAI voice clone model selected. Open Settings and choose the English xAI voice option.",
            };

        var cloneModelId = ctx.CloneModel.Id;
        var providerId = ctx.CloneModel.ProviderId;
        if (string.IsNullOrWhiteSpace(providerId))
            providerId = ProviderId;

        string? voiceId;
        try
        {
            voiceId = await _client.CloneVoiceAsync(ctx.SamplePath, cloneModelId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new VoiceApplyResult { Ok = false, ModelId = cloneModelId, Error = ex.Message };
        }

        await VoiceApplyCallLog.LogCloneAsync(
            _telemetry, new VoiceApplyCallIdentity(ctx.ProjectId, ctx.CharKey, cloneModelId, providerId),
            ctx.CloneModel.CostPerCloneUsd, voiceId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(voiceId))
            return new VoiceApplyResult { Ok = false, Error = "xAI voice clone failed — see server logs." };

        var label = string.IsNullOrWhiteSpace(ctx.VoiceLabel) ? "Personal clone" : ctx.VoiceLabel.Trim();
        var profile = $"Provider voice ({providerId}:{voiceId}). xAI English clone from operator sample.";
        _previews.PersistSeed(ctx.ProjectId, ctx.CharKey, providerId, voiceId, label, profile);

        var (previewRel, previewUrl) = await TryPreviewAsync(ctx, voiceId, providerId, ct).ConfigureAwait(false);

        _log.LogInformation(
            "xAI apply {Project}/{Char} voiceId={VoiceId}",
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
            Message = "Voice applied via xAI (English). Id stored on this character.",
            EstimatedCloneUsd = ctx.CloneModel.CostPerCloneUsd,
        };
    }

    private async Task<(string? Rel, string? Url)> TryPreviewAsync(
        VoiceApplyContext ctx, string voiceId, string providerId, CancellationToken ct)
    {
        var speak = VoiceApplyCallLog.SelectSpeak(ctx, providerId);
        try
        {
            var audioUrl = await _client.SynthesizeSpeechAsync(speak.Text, voiceId, speak.ModelId, ct)
                .ConfigureAwait(false);
            await VoiceApplyCallLog.LogSpeakAsync(
                _telemetry, new VoiceApplyCallIdentity(ctx.ProjectId, ctx.CharKey, speak.ModelId, speak.ProviderId), speak.Text,
                ctx.SpeakModel?.CostPerThousandCharsUsd, !string.IsNullOrWhiteSpace(audioUrl), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(audioUrl))
                return (null, null);
            var bytes = await VoiceSpeechPayload.TryReadAsync(_httpFactory, _log, audioUrl, ct)
                .ConfigureAwait(false);
            if (bytes is not { Length: > 0 })
                return (null, null);
            return await _previews.WriteAsync(ctx.ProjectId, ctx.CharKey, bytes, ".mp3", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "xAI TTS preview failed after successful clone — seed still saved");
            return (null, null);
        }
    }
}
