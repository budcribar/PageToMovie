using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.ModelBacked;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

public class JitBenchmarkServiceTests
{
    private sealed class TestVideoClient : IVideoClient
    {
        public bool IsConfigured => true;
        public bool GenerationSubmitted { get; private set; }

        public Task<string> SubmitGenerationAsync(
            string prompt,
            int durationSeconds,
            string resolution,
            string model,
            CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null,
            string? startFrameImagePath = null,
            string? continueFromVideoPath = null,
            string? aspectRatio = null)
        {
            GenerationSubmitted = true;
            return Task.FromResult("test_job_123");
        }

        public Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
        {
            return Task.FromResult("https://fal.media/test_clip.mp4");
        }

        public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public (string? FileId, long? ExpiresAtUnixSeconds) TryGetStoredFileReference(string requestId) => (null, null);
    }

    private sealed class TestVisionClient : IVisionClient
    {
        public bool IsConfigured => true;

        public Task<string> CompleteWithImagesAsync(string prompt, IReadOnlyList<string> imagePaths, string model = "grok-4.5", string detail = "low", CancellationToken ct = default)
        {
            return Task.FromResult("""
            {
              "actionCompletionSec": 3.4,
              "explanation": "Action completed at 3.4 seconds mark."
            }
            """);
        }

        public Task<string> TranscribePageAsync(string imagePath, int page, string model = "grok-4.5", CancellationToken ct = default)
        {
            return Task.FromResult("{}");
        }

        public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast, string model = "grok-4.5", CancellationToken ct = default)
        {
            return Task.FromResult(new CharacterPageClassification { Page = page });
        }
    }

    private sealed class TestChatClient : IChatClient
    {
        public bool IsConfigured => true;
        public string ResponseToReturn { get; set; } = "{}";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, string model, double temperature = 0.2, CancellationToken ct = default, string? mode = null, string? reasoningEffort = null)
        {
            return Task.FromResult(ResponseToReturn);
        }

        public Task<string> CompleteWithImageAsync(string systemPrompt, string userPrompt, byte[] imageBytes, string model, double temperature = 0.2, CancellationToken ct = default)
        {
            return Task.FromResult("{}");
        }

        public Task<string> CompleteWithImagesAsync(string systemPrompt, string userPrompt, IEnumerable<byte[]> imageBytesList, string model, double temperature = 0.2, CancellationToken ct = default)
        {
            return Task.FromResult("{}");
        }
    }

    [Fact]
    public void AiActionOverheadClassifier_ClassifiesWeaponActionCorrectly()
    {
        var estimation = AiActionOverheadClassifier.ClassifyNovelAction("Pulls out a rusty blade", "(clicks open)");

        Assert.Equal("act_knife_pull", estimation.MatchCategoryId);
        Assert.Equal(2.0, estimation.EstimatedOverheadSec);
        Assert.True(estimation.ConfidenceScore >= 0.85);
    }

    [Fact]
    public async Task AiActionOverheadClassifierAsync_InvokesLlmClassifierWhenAvailable()
    {
        var ledger = new ActionCameraOverheadLedger();
        var router = new SmartClassifierModelRouter();
        var testChat = new TestChatClient
        {
            ResponseToReturn = """
            {
              "matchCategoryId": "act_stabbing",
              "estimatedOverheadSec": 3.1,
              "confidenceScore": 0.98,
              "explanation": "Llm classified as physical stabbing."
            }
            """
        };

        var classifier = new AiActionOverheadClassifier(
            router, ledger, testChat,
            modelOverride: OfflineTestModelConfig.Required("chat"));
        var estimation = await classifier.ClassifyNovelActionAsync("Lunges forward with a spear", "(screaming)");

        Assert.Equal("act_stabbing", estimation.MatchCategoryId);
        Assert.Equal(3.1, estimation.EstimatedOverheadSec);
        Assert.Equal(0.98, estimation.ConfidenceScore);
    }

    [Fact]
    public async Task AiActionOverheadClassifierAsync_ValidatesLlmCategoryAgainstLedger()
    {
        var ledger = new ActionCameraOverheadLedger();
        var router = new SmartClassifierModelRouter();
        var testChat = new TestChatClient
        {
            ResponseToReturn = """
            {
              "matchCategoryId": "hallucinated_uncalibrated_category",
              "estimatedOverheadSec": 9.9,
              "confidenceScore": 0.99,
              "explanation": "Uncalibrated category."
            }
            """
        };

        var classifier = new AiActionOverheadClassifier(router, ledger, testChat);
        var estimation = await classifier.ClassifyNovelActionAsync("Creeping quietly through the dark hall", "(stealth)");

        // Should reject hallucinated category and fall back to calibrated creeping category
        Assert.Equal("act_creeping_step", estimation.MatchCategoryId);
        Assert.Equal(2.8, estimation.EstimatedOverheadSec);
    }

    [Fact]
    public async Task EnsureBeatCalibratedAsync_ExecutesLiveJitAndVisionAnalysis()
    {
        var ledger = new ActionCameraOverheadLedger();
        var router = new SmartClassifierModelRouter();
        var classifier = new AiActionOverheadClassifier(router, ledger);
        var videoClient = new TestVideoClient();
        var visionClient = new TestVisionClient();
        var jitService = new JitBenchmarkService(ledger, classifier, videoClient, visionClient);

        var result = await jitService.EnsureBeatCalibratedAsync(
            "Slashes with a machete",
            "(roaring)",
            modelId: "fal-ai/hunyuan-video",
            evaluatorModelId: "gemini-2.5-flash");

        Assert.True(result.IsLiveJitBenchmark);
        Assert.True(videoClient.GenerationSubmitted);
        Assert.Contains("Live Video API", result.SourceDescription);
    }

    [Fact]
    public async Task EnsureBeatCalibratedAsync_ReturnsJitResultWithFallbackWhenKeysMissing()
    {
        var ledger = new ActionCameraOverheadLedger();
        var router = new SmartClassifierModelRouter();
        var classifier = new AiActionOverheadClassifier(router, ledger);
        var jitService = new JitBenchmarkService(ledger, classifier);

        var result = await jitService.EnsureBeatCalibratedAsync("Sorting pill bottles on the counter", "(while speaking)");

        Assert.Equal("act_pills_sorting", result.CategoryId);
        Assert.Equal(2.3, result.MeasuredOverheadSec);
        Assert.Equal(0.85, result.OverlapRatioGamma);
        Assert.False(result.IsLiveJitBenchmark);
    }

    [Fact]
    public async Task EnsureBeatCalibratedAsync_SkipsLiveMeasurementWhenIndexMatchIsConfident()
    {
        var ledger = new ActionCameraOverheadLedger();
        var router = new SmartClassifierModelRouter();
        var classifier = new AiActionOverheadClassifier(router, ledger);
        var videoClient = new TestVideoClient();
        var visionClient = new TestVisionClient();
        var jitService = new JitBenchmarkService(ledger, classifier, videoClient, visionClient);

        // "Sorting pill bottles" heuristically matches act_pills_sorting at 0.92 confidence —
        // above the 0.80 threshold — so no video generation should be dispatched even though
        // the video/vision clients are configured and available.
        var result = await jitService.EnsureBeatCalibratedAsync("Sorting pill bottles on the counter", "(while speaking)");

        Assert.Equal("act_pills_sorting", result.CategoryId);
        Assert.Equal(2.3, result.MeasuredOverheadSec);
        Assert.False(result.IsLiveJitBenchmark);
        Assert.False(videoClient.GenerationSubmitted);
        Assert.Contains("Confident index match", result.SourceDescription);
    }
}
