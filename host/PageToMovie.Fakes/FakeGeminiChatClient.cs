using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Fakes;

/// <summary>
/// Fakes ClipDialogueVerificationService's Gemini-native-video escape hatch
/// (IGeminiVideoAnalysisClient) — distinct from FakeGrokVisionClient/IVisionClient so fakes mode
/// actually exercises the "force real video capability regardless of configured vision model"
/// branch instead of silently resolving to null and falling through to the still-image path.
/// </summary>
public sealed class FakeGeminiChatClient : IGeminiVideoAnalysisClient
{
    private readonly ILogger<FakeGeminiChatClient> _log;

    public FakeGeminiChatClient(ILogger<FakeGeminiChatClient> log) => _log = log;

    public bool IsConfigured => true;

    public Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake Gemini native-video analysis ({Count} media file(s))", imagePaths.Count);
        return Task.FromResult("""
        {
          "detectedSpeaker": "Narrator",
          "transcribedDialogue": "[fake-gemini-video] simulated transcription",
          "dialogueAccuracyScore": 1.0,
          "speakerMatch": true,
          "status": "verified",
          "summaryNote": "[fake-gemini-video] fakes-mode canned result — no real Gemini call made"
        }
        """);
    }
}
