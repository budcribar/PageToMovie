using PageToMovie.Engine;

namespace PageToMovie.Engine.Abstractions;

/// <summary>
/// Speech-to-text for a short dialogue segment. Implementations share
/// <see cref="ScribeResult"/> so callers keep word timings, text, and language.
/// The selected catalog model's provider chooses the implementation.
/// </summary>
public interface ITranscriptionClient
{
    Task<ScribeResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        string? languageCode = null,
        string? modelId = null,
        CancellationToken ct = default);
}
