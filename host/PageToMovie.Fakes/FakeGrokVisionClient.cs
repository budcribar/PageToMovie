using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Fakes;

public sealed class FakeGrokVisionClient : IVisionClient
{
    private readonly ILogger<FakeGrokVisionClient> _log;
    private readonly ProjectTelemetryService _telemetry;

    public FakeGrokVisionClient(ILogger<FakeGrokVisionClient> log, ProjectTelemetryService telemetry)
    {
        _log = log;
        _telemetry = telemetry;
    }

    public bool IsConfigured => true;

    public async Task<string> TranscribePageAsync(
        string imagePath,
        int page,
        string model = "",
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake vision transcribe page={Page}", page);
        var result = "(illustration only)";
        try
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Mode = "transcribe_page",
                Model = model,
                ResponseChars = result.Length,
                Fakes = true,
                Ok = true,
            }, ct).ConfigureAwait(false);
        }
        catch { /* telemetry is best-effort */ }
        return result;
    }

    public async Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast,
        string model = "",
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake vision classify page={Page} cast={N}", page, cast.Count);
        try
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Mode = "classify_characters",
                Model = model,
                Fakes = true,
                Ok = true,
            }, ct).ConfigureAwait(false);
        }
        catch { /* telemetry is best-effort */ }
        return new CharacterPageClassification
        {
            Page = page,
            PageKind = "illustration",
            Matches = new List<CharacterPageMatch>(),
        };
    }

    public async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake vision multi-image n={N}", imagePaths?.Count ?? 0);
        string result;
        string kind;

        // Portrait style gate (CharacterDesignService.EnsurePortraitStyleAllowedAsync) — always pass
        // for fakes so UI tests can lock a portrait. Match markers that are ACTUALLY in the gate
        // prompt ("Expected medium for this project" / "Classify the image medium"); the old
        // "PORTRAIT STYLE GATE" string never appeared, so the gate fell through to auto-review JSON,
        // read medium=other, and every portrait lock failed in fakes mode ("could not read the image").
        if (!string.IsNullOrEmpty(prompt) &&
            (prompt.Contains("Classify the image medium", StringComparison.OrdinalIgnoreCase) ||
             prompt.Contains("Expected medium for this project", StringComparison.OrdinalIgnoreCase)))
        {
            kind = "vision";
            var expectedIllustration = prompt.Contains("Expected medium for this project: illustration", StringComparison.OrdinalIgnoreCase);
            // Test hook: force a style-mismatch verdict so the "Use this look anyway" override path is
            // reachable in fakes mode (the gate otherwise always passes). Reports the OPPOSITE medium.
            var forceReject = string.Equals(Environment.GetEnvironmentVariable("PAGETOMOVIE_FAKE_STYLE_REJECT"), "1", StringComparison.Ordinal)
                || string.Equals(Environment.GetEnvironmentVariable("PAGETOMOVIE_FAKE_STYLE_REJECT"), "true", StringComparison.OrdinalIgnoreCase);
            if (forceReject)
            {
                var wrong = expectedIllustration ? "photoreal" : "illustration";
                result = $"{{\"pass\":false,\"medium\":\"{wrong}\",\"reason\":\"Fake forced style-mismatch for override testing.\"}}";
            }
            else
            {
                var medium = expectedIllustration ? "illustration" : "photoreal";
                result = $"{{\"pass\":true,\"medium\":\"{medium}\",\"reason\":\"Fake style gate pass.\"}}";
            }
        }
        else if (!string.IsNullOrEmpty(prompt) &&
                 (prompt.Contains("choosing the best character portrait", StringComparison.OrdinalIgnoreCase)
                  || prompt.Contains("choosing the best location set plate", StringComparison.OrdinalIgnoreCase)
                  || prompt.Contains("\"best\":1", StringComparison.OrdinalIgnoreCase)
                  || prompt.Contains("Pick the single image that best matches", StringComparison.OrdinalIgnoreCase)))
        {
            kind = "vision";
            // Prefer first image — stable for tests; real vision ranks quality.
            result = """{"best":1,"reason":"Fake look pick — first variant."}""";
        }
        else if (!string.IsNullOrEmpty(prompt) && prompt.Contains("music supervisor", StringComparison.OrdinalIgnoreCase))
        {
            kind = "vision";
            result = """
                {
                  "1": { "prompt": "Dark orchestral theme with low cello and tense pulse.", "genre": "Thriller", "mood": "Tense", "tempo": "90 BPM" },
                  "2": { "prompt": "Subtle atmospheric ambient drone with eerie strings.", "genre": "Ambient", "mood": "Unsettling", "tempo": "75 BPM" }
                }
                """;
        }
        else
        {
            // Minimal valid auto-review JSON for UI/job testing without spend
            kind = "review";
            result = """
                {
                  "suggestion": "unclear",
                  "category": "other",
                  "confidence": "low",
                  "continuity": "unclear",
                  "note": "Fake review — connect API for real analysis.",
                  "suggestions": []
                }
                """;
        }

        try
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = kind,
                Model = model,
                PromptChars = prompt?.Length ?? 0,
                ImageCount = imagePaths?.Count ?? 0,
                Fakes = true,
                Ok = true,
            }, ct).ConfigureAwait(false);
        }
        catch { /* telemetry is best-effort */ }

        return result;
    }
}
