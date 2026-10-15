using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine.VoiceApply;

/// <summary>One telemetry shape for clone and speak apply steps.</summary>
internal readonly record struct VoiceSpeakSelection(string Text, string ModelId, string ProviderId);

internal static class VoiceApplyCallLog
{
    public static VoiceSpeakSelection SelectSpeak(VoiceApplyContext ctx, string providerId)
    {
        var text = VoicePreviewStore.DefaultPreviewText(ctx.PreviewText);
        var modelId = ctx.SpeakModel?.Id
                      ?? SupportedModelCatalog.FirstEnabledSpeakModelId(providerId)
                      ?? "";
        var speakProvider = ctx.SpeakModel?.ProviderId
                            ?? SupportedModelCatalog.CatalogProviderId(modelId, "tts")
                            ?? providerId;
        return new VoiceSpeakSelection(text, modelId, speakProvider);
    }

    public static Task LogCloneAsync(
        ProjectTelemetryService? telemetry,
        string projectId,
        string? charKey,
        string? modelId,
        string providerId,
        double? estimatedUsd,
        string? voiceId,
        CancellationToken ct)
    {
        if (telemetry is null)
            return Task.CompletedTask;
        var ok = !string.IsNullOrWhiteSpace(voiceId);
        return telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            ProjectId = projectId,
            Kind = "voice_clone",
            Mode = "voice_clone",
            Model = modelId,
            Provider = providerId,
            CharKey = charKey,
            EstimatedUsd = estimatedUsd,
            Ok = ok,
            Error = ok ? null : "Voice clone failed",
        }, ct);
    }

    public static Task LogSpeakAsync(
        ProjectTelemetryService? telemetry,
        string projectId,
        string? charKey,
        string? modelId,
        string providerId,
        string text,
        double? costPerThousandCharsUsd,
        bool ok,
        CancellationToken ct)
    {
        if (telemetry is null)
            return Task.CompletedTask;
        var estimatedUsd = costPerThousandCharsUsd is { } rate
            ? Math.Round(rate * text.Length / 1000.0, 4)
            : (double?)null;
        return telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            ProjectId = projectId,
            Kind = "tts",
            Mode = "dialogue_tts",
            Model = modelId,
            Provider = providerId,
            CharKey = charKey,
            PromptChars = text.Length,
            EstimatedUsd = estimatedUsd,
            Ok = ok,
            Error = ok ? null : "Speech synthesis failed",
        }, ct);
    }
}
