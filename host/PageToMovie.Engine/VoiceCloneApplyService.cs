using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.VoiceApply;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Orchestrates voice apply: ensure sample on disk, resolve catalog models, pick an
/// <see cref="IVoiceApplyStrategy"/>, execute. Strategies own provider HTTP + seed writes.
/// </summary>
public sealed class VoiceCloneApplyService
{
    private readonly ProjectStore _projects;
    private readonly VoicePreviewStore _previews;
    private readonly IReadOnlyList<IVoiceApplyStrategy> _strategies;
    private readonly IVoiceClient _eleven; // catalog/premade only
    private readonly ILogger<VoiceCloneApplyService> _log;

    public VoiceCloneApplyService(
        ProjectStore projects,
        VoicePreviewStore previews,
        IEnumerable<IVoiceApplyStrategy> strategies,
        IVoiceClient eleven,
        ILogger<VoiceCloneApplyService> log)
    {
        _projects = projects;
        _previews = previews;
        _strategies = strategies.ToList();
        _eleven = eleven;
        _log = log;
    }

    public bool IsConfigured => _strategies.Any(s => s.IsConfigured);

    /// <summary>
    /// Create a provider clone from the character's saved sample (or supplied bytes),
    /// store provider voice id on the seed, optional TTS preview.
    /// </summary>
    public async Task<VoiceApplyResult> ApplyFromSampleAsync(
        string projectId,
        string charKey,
        byte[]? sampleOverride = null,
        string? sampleFileName = null,
        string? previewText = null,
        string? voiceLabel = null,
        string? modelOverride = null,
        CancellationToken ct = default)
    {
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        var display = charKey.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');

        var samplePath = await EnsureSampleAsync(projectId, charKey, sampleOverride, sampleFileName, ct)
            .ConfigureAwait(false);

        var (cloneModel, speakModel) = await ResolveVoiceModelsAsync(projectId, modelOverride, ct)
            .ConfigureAwait(false);

        if (cloneModel is null)
        {
            return new VoiceApplyResult
            {
                Ok = false,
                Error =
                    "No voice clone model selected for this project. " +
                    "Open Settings → Studio coverage → Voice clone, choose ElevenLabs Instant voice clone " +
                    "or Fal MiniMax Voice Clone, save, then try again. Your recording is still saved.",
            };
        }

        var strategy = SelectStrategy(cloneModel);
        if (strategy is null)
            return new VoiceApplyResult
            {
                Ok = false,
                ModelId = cloneModel.Id,
                Error =
                    $"No voice apply path for model '{cloneModel.Id}' (provider {cloneModel.ProviderId}). " +
                    "Pick ElevenLabs Instant voice clone or Fal MiniMax Voice Clone in Settings.",
            };

        // Do not auto-switch providers. If this key/model cannot clone, surface the error and let
        // the user pick another voice model in Settings.
        if (!strategy.IsConfigured)
        {
            return new VoiceApplyResult
            {
                Ok = false,
                ProviderId = strategy.ProviderId,
                ModelId = cloneModel.Id,
                Error = NotConfiguredMessage(strategy.ProviderId, cloneModel.Id),
            };
        }

        var ctx = new VoiceApplyContext
        {
            ProjectId = projectId,
            CharKey = charKey,
            DisplayName = display,
            SamplePath = samplePath,
            CloneModel = cloneModel,
            SpeakModel = speakModel,
            PreviewText = previewText,
            VoiceLabel = voiceLabel,
        };

        _log.LogInformation(
            "Apply voice {Project}/{Char} via {Provider} model={Model}",
            projectId, charKey, strategy.ProviderId, cloneModel.Id);

        var result = await strategy.ApplyAsync(ctx, ct).ConfigureAwait(false);
        if (result.Ok)
            return result;
        if (!string.IsNullOrWhiteSpace(result.Error))
            return result;
        return new VoiceApplyResult
        {
            Ok = false,
            ProviderId = result.ProviderId ?? strategy.ProviderId,
            ModelId = result.ModelId ?? cloneModel.Id,
            Error = $"Voice clone failed with {strategy.ProviderId}. Check the key in Settings or choose another voice model yourself.",
        };
    }

    private static string NotConfiguredMessage(string? providerId, string? modelId)
    {
        var p = string.IsNullOrWhiteSpace(providerId) ? "this provider" : providerId;
        var m = string.IsNullOrWhiteSpace(modelId) ? "voice clone" : modelId;
        return $"No working API key for {p} (model {m}). "
               + "Add or fix that key in Settings, or pick a different voice model yourself — "
               + "we do not switch providers automatically. Your recording is still saved.";
    }

    /// <summary>Attach a catalog/premade ElevenLabs voice id (no sample clone).</summary>
    public async Task<VoiceApplyResult> ApplyCatalogVoiceAsync(
        string projectId,
        string charKey,
        string providerVoiceId,
        string? displayName = null,
        string? previewText = null,
        CancellationToken ct = default)
    {
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(providerVoiceId))
            return new VoiceApplyResult { Ok = false, Error = "providerVoiceId required" };

        var label = string.IsNullOrWhiteSpace(displayName) ? "Catalog voice" : displayName.Trim();
        var speakModel = SupportedModelCatalog.Find("eleven_multilingual_v2", ModelCapability.Voice)
                         ?? SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                             .FirstOrDefault(m => !m.IsVoiceCloneStep
                                                  && m.Provider == ModelProviderFamily.ElevenLabs);
        var providerId = speakModel?.ProviderId
                         ?? SupportedModelCatalog.CatalogProviderId(speakModel?.Id, "tts")
                         ?? "";
        var profile = string.IsNullOrWhiteSpace(providerId)
            ? $"Provider voice ({providerVoiceId.Trim()}). Catalog/premade selection."
            : $"Provider voice ({providerId}:{providerVoiceId.Trim()}). Catalog/premade selection.";
        _previews.PersistSeed(
            projectId, charKey, providerId, providerVoiceId.Trim(), label, profile);

        string? previewRel = null;
        string? previewUrl = null;
        var ttsText = VoicePreviewStore.DefaultPreviewText(previewText);
        if (speakModel is null)
        {
            return new VoiceApplyResult
            {
                Ok = false,
                Error = "No TTS model in the catalog for catalog voices. Open Settings and pick a voice model.",
            };
        }
        var tts = await _eleven.TextToSpeechAsync(
            providerVoiceId.Trim(), ttsText, speakModel.Id, ct).ConfigureAwait(false);
        if (tts.Ok && tts.AudioBytes is { Length: > 0 })
        {
            (previewRel, previewUrl) = await _previews.WriteAsync(
                projectId, charKey, tts.AudioBytes, tts.FileExtension ?? ".mp3", ct).ConfigureAwait(false);
        }

        return new VoiceApplyResult
        {
            Ok = true,
            ProviderId = providerId,
            ProviderVoiceId = providerVoiceId.Trim(),
            ModelId = speakModel.Id,
            UsedMock = tts.UsedMock || providerVoiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase),
            PreviewRelativePath = previewRel,
            PreviewUrl = previewUrl,
            VoiceLabel = label,
            Message = "Catalog voice assigned to character.",
        };
    }

    public string? GetTtsPreviewPath(string projectId, string charKey) =>
        _previews.GetTtsPreviewPath(projectId, charKey);

    private IVoiceApplyStrategy? SelectStrategy(SupportedModelEntry? cloneModel)
    {
        // Stick to the model the user (or project config) selected — no silent provider hop.
        var match = _strategies.FirstOrDefault(s => s.CanHandle(cloneModel));
        if (match is not null) return match;
        return null;
    }

    private async Task<string> EnsureSampleAsync(
        string projectId,
        string charKey,
        byte[]? sampleOverride,
        string? sampleFileName,
        CancellationToken ct)
    {
        if (sampleOverride is { Length: > 0 })
        {
            var fileName = string.IsNullOrWhiteSpace(sampleFileName) ? "voice_clone_sample.wav" : sampleFileName;
            await using var ms = new MemoryStream(sampleOverride);
            return await _projects.SaveVoiceCloneSampleAsync(projectId, charKey, ms, fileName, ct)
                .ConfigureAwait(false);
        }

        var path = _projects.GetVoiceCloneSamplePath(projectId, charKey);
        if (File.Exists(path)) return path;

        var seed = MockToneWav.Sine(2.2, 210);
        await using var seedMs = new MemoryStream(seed);
        return await _projects.SaveVoiceCloneSampleAsync(
            projectId, charKey, seedMs, "voice_clone_sample.wav", ct).ConfigureAwait(false);
    }

    private async Task<(SupportedModelEntry? Clone, SupportedModelEntry? Speak)> ResolveVoiceModelsAsync(
        string projectId,
        string? modelOverride,
        CancellationToken ct)
    {
        string? configured = modelOverride;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            if (cfg.TryGetValue("voice_model_name", out var el) && el.ValueKind == JsonValueKind.String)
                configured = el.GetString();
        }

        SupportedModelEntry? selected = null;
        if (!string.IsNullOrWhiteSpace(configured) &&
            !configured.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !configured.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            selected = SupportedModelCatalog.Find(configured, ModelCapability.Voice)
                       ?? SupportedModelCatalog.Find(configured);
        }

        SupportedModelEntry? clone = selected is { IsVoiceCloneStep: true }
            ? selected
            : selected is not null
                ? SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                    .FirstOrDefault(m => m.IsVoiceCloneStep && m.Provider == selected.Provider)
                : null;

        if (clone is null)
        {
            // No invent / no hop to first configured strategy — Settings must pick a voice model.
            return (null, null);
        }

        SupportedModelEntry? speak = null;
        if (selected is { IsVoiceCloneStep: false })
            speak = selected;
        else if (clone is not null)
        {
            speak = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Provider == clone.Provider);
        }

        return (clone, speak);
    }
}
