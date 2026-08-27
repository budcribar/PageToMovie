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

    private const string KindVision = "vision";

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
                Kind = KindVision,
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
                Kind = KindVision,
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
        double temperature = 0.0,
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake vision multi-image n={N}", imagePaths?.Count ?? 0);
        string result;
        string kind;

        if (TryStyleGateResponse(prompt, out result)
            || TryLookPickResponse(prompt, out result)
            || TryMovieSceneGroupReviewResponse(prompt, out result)
            || TryExecutiveSummaryResponse(prompt, out result)
            || TryMusicSupervisorResponse(prompt, out result))
        {
            kind = KindVision;
        }
        else
        {
            kind = "review";
            result = AutoReviewFallbackJson;
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

    // Portrait style gate (CharacterDesignService.EnsurePortraitStyleAllowedAsync) — always pass
    // for fakes so UI tests can lock a portrait. Match markers that are ACTUALLY in the gate
    // prompt ("Expected medium for this project" / "Classify the image medium"); the old
    // "PORTRAIT STYLE GATE" string never appeared, so the gate fell through to auto-review JSON,
    // read medium=other, and every portrait lock failed in fakes mode ("could not read the image").
    private static bool TryStyleGateResponse(string prompt, out string result)
    {
        result = "";
        if (string.IsNullOrEmpty(prompt)) return false;
        if (!prompt.Contains("Classify the image medium", StringComparison.OrdinalIgnoreCase)
            && !prompt.Contains("Expected medium for this project", StringComparison.OrdinalIgnoreCase))
            return false;

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
        return true;
    }

    private static bool TryLookPickResponse(string prompt, out string result)
    {
        result = "";
        if (string.IsNullOrEmpty(prompt)) return false;
        if (!prompt.Contains("choosing the best character portrait", StringComparison.OrdinalIgnoreCase)
            && !prompt.Contains("choosing the best location set plate", StringComparison.OrdinalIgnoreCase)
            && !prompt.Contains("\"best\":1", StringComparison.OrdinalIgnoreCase)
            && !prompt.Contains("Pick the single image that best matches", StringComparison.OrdinalIgnoreCase))
            return false;

        // Prefer first image — stable for tests; real vision ranks quality.
        result = """{"best":1,"reason":"Fake look pick — first variant."}""";
        return true;
    }

    /// <summary>
    /// Full-movie continuity review (<c>MovieAutoReviewService</c>). Its validator rejects missing
    /// notes and out-of-range scores, so the fake has to answer in full — a stub reply fails the
    /// whole review and there is no fake coverage of the feature at all.
    /// </summary>
    private static bool TryMovieSceneGroupReviewResponse(string prompt, out string result)
    {
        result = "";
        if (string.IsNullOrEmpty(prompt)
            || !prompt.Contains("continuityScore", StringComparison.OrdinalIgnoreCase)
            || !prompt.Contains("visualConsistencyNotes", StringComparison.OrdinalIgnoreCase))
            return false;

        result = """
            {
              "overallScore": 8,
              "continuityScore": 8,
              "characterScore": 8,
              "lightingScore": 7,
              "pacingScore": 8,
              "dialogueScore": 7,
              "musicScore": 8,
              "continuityNotes": "Fake review — shot-to-shot spatial alignment holds across the sampled cuts.",
              "visualConsistencyNotes": "Fake review — faces and wardrobe stay locked between sampled frames.",
              "lightingNotes": "Fake review — exposure and palette stay stable across the sampled cuts.",
              "dialogueNotes": "Fake review — speaking posture matches the scripted beats in sampled frames.",
              "audioNotes": "Fake review — music cues fade rather than cutting off between scenes."
            }
            """;
        return true;
    }

    /// <summary>
    /// Executive synthesis for the full-movie review. Answers in Markdown with a score table,
    /// which is what the real models do — and what the report card has to be able to render.
    /// </summary>
    private static bool TryExecutiveSummaryResponse(string prompt, out string result)
    {
        result = "";
        if (string.IsNullOrEmpty(prompt)
            || !prompt.Contains("Executive Director Summary Report", StringComparison.OrdinalIgnoreCase))
            return false;

        result = """
            ## Executive Post-Production Assessment

            **Overall Evaluation:** Fake review — the cut holds together across sequences.

            | Category | Score | Status |
            | :--- | :---: | :--- |
            | Continuity & Visual Cohesion | 8/10 | Approved |
            | Character Lock & Model Fidelity | 8/10 | Approved |
            | Lighting & Color Grading | 7/10 | Needs Polish |

            ### Remediation Roadmap

            1. **Colour pass:** even out exposure between interior and exterior setups.
            2. **Audio conform:** smooth the music cue transitions between sequences.
            """;
        return true;
    }

    private static bool TryMusicSupervisorResponse(string prompt, out string result)
    {
        result = "";
        if (string.IsNullOrEmpty(prompt)
            || !prompt.Contains("music supervisor", StringComparison.OrdinalIgnoreCase))
            return false;

        result = """
            {
              "1": { "prompt": "Dark orchestral theme with low cello and tense pulse.", "genre": "Thriller", "mood": "Tense", "tempo": "90 BPM" },
              "2": { "prompt": "Subtle atmospheric ambient drone with eerie strings.", "genre": "Ambient", "mood": "Unsettling", "tempo": "75 BPM" }
            }
            """;
        return true;
    }

    private const string AutoReviewFallbackJson =
        """
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
