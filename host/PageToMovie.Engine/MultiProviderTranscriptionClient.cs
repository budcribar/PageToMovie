using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Routes speech-to-text to the client for the selected catalog model's provider.
/// An unset model uses <c>capabilities[transcription].defaultModelId</c> (ElevenLabs Scribe).
/// It does not switch that default to xAI.
/// </summary>
public sealed class MultiProviderTranscriptionClient : ITranscriptionClient
{
    private readonly ElevenLabsScribeClient _eleven;
    private readonly XaiSpeechToTextClient _xai;

    public MultiProviderTranscriptionClient(ElevenLabsScribeClient eleven, XaiSpeechToTextClient xai)
    {
        _eleven = eleven;
        _xai = xai;
    }

    public Task<ScribeResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        string? languageCode = null,
        string? modelId = null,
        CancellationToken ct = default)
    {
        var entry = Resolve(modelId);
        if (entry is null)
            return Task.FromResult(ScribeResult.Fail("Speech to text: no model selected. Open Settings and choose one."));

        return entry.Provider switch
        {
            ModelProviderFamily.ElevenLabs => _eleven.TranscribeAsync(audio, fileName, languageCode, entry.Id, ct),
            ModelProviderFamily.Xai => _xai.TranscribeAsync(audio, fileName, languageCode, entry.Id, ct),
            _ => Task.FromResult(ScribeResult.Fail(
                $"Speech to text is not available for provider '{entry.ProviderId}'.")),
        };
    }

    /// <summary>
    /// Enabled transcription row for <paramref name="modelId"/>, or the capability default when unset.
    /// </summary>
    public static SupportedModelEntry? Resolve(string? modelId)
    {
        if (ProjectModelSelection.IsUsableModelId(modelId))
            return SupportedModelCatalog.Find(modelId, ModelCapability.Transcription);

        var def = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Transcription);
        if (string.IsNullOrWhiteSpace(def))
            return null;
        return SupportedModelCatalog.Find(def, ModelCapability.Transcription);
    }
}
