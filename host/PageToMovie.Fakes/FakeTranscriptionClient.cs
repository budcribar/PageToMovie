using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Fakes;

/// <summary>
/// Offline speech-to-text. Never opens a socket to xAI or ElevenLabs.
/// </summary>
public sealed class FakeTranscriptionClient : ITranscriptionClient
{
    private readonly ILogger<FakeTranscriptionClient> _log;

    public FakeTranscriptionClient(ILogger<FakeTranscriptionClient> log) => _log = log;

    public Task<ScribeResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        string? languageCode = null,
        string? modelId = null,
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake speech-to-text ({Bytes} bytes)", audio?.Length ?? 0);
        if (audio is null || audio.Length < 128)
            return Task.FromResult(ScribeResult.Fail("Audio is empty or too short."));

        IReadOnlyList<ScribeWord> words =
        [
            new ScribeWord("hello", 0.1, 0.4, "word"),
            new ScribeWord("there", 0.4, 0.8, "word"),
        ];
        return Task.FromResult(new ScribeResult(true, "hello there", words, languageCode ?? "en", null));
    }
}
