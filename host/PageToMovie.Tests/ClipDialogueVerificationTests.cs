using System.IO;
using System.Threading.Tasks;
using Xunit;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Options;

namespace PageToMovie.Tests;

public class ClipDialogueVerificationTests
{
    [Fact]
    public void BuildExpectedDialogue_covers_both_speakers_in_a_two_hander()
    {
        // A cross-speaker two-hander clip: both lines must be part of the expected content so the
        // second speaker's line is actually verified (regression: it was previously dropped).
        var twoHander = new ClipSummary
        {
            ClipNumber = 1,
            Speaker = "Character_Children",
            Dialogue = "Why does the lamb love Mary so?",
            Delivery = "spoken_on_camera",
            SecondarySpeaker = "Character_Teacher",
            SecondaryDialogue = "Oh, Mary loves the lamb, you know.",
        };

        var expected = ClipDialogueVerificationService.BuildExpectedDialogue(twoHander);
        Assert.Contains("Why does the lamb love Mary so?", expected);
        Assert.Contains("Oh, Mary loves the lamb, you know.", expected);

        // Single-speaker clip → just its own line.
        var single = new ClipSummary
        {
            ClipNumber = 2,
            Speaker = "Character_Children",
            Dialogue = "Why does the lamb love Mary so?",
            Delivery = "spoken_on_camera",
        };
        Assert.Equal("Why does the lamb love Mary so?",
            ClipDialogueVerificationService.BuildExpectedDialogue(single));
    }

    /// <summary>
    /// Regression: ClipDialogueVerificationService used to take a concrete GeminiChatClient?
    /// dependency, which fakes mode couldn't provide (only registered in Program.cs's non-fakes
    /// branch) — it silently resolved to null, so fakes-mode dialogue verification always took
    /// the still-image IVisionClient fallback and never exercised the native-video branch
    /// production actually uses when the configured vision model can't watch video itself.
    /// IGeminiVideoAnalysisClient (fakeable) fixed that; this proves the branch is really taken.
    /// </summary>
    [Fact]
    public async Task VerifyClipDialogueAsync_does_not_switch_models_when_selection_cannot_review_video()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var projDir = Path.Combine(tempDir, "projects", "test_proj");
        Directory.CreateDirectory(Path.Combine(projDir, "assets", "video"));
        try
        {
            File.WriteAllText(Path.Combine(projDir, "project.json"), """{"id":"test_proj"}""");
            // grok-4.5 (Vision) has SupportsVideoReview=false — forces the Gemini escape hatch.
            File.WriteAllText(Path.Combine(projDir, "pipeline_config.json"),
                """{"blueprint_file":"blueprint.clips.grok.json","vision_model_name":"grok-4.5"}""");
            File.WriteAllText(Path.Combine(projDir, "blueprint.clips.grok.json"), """
                {
                  "scenes": [
                    {
                      "scene_number": 1,
                      "veo_clips": [
                        { "clip_number": 1, "visual_prompt": "clip one", "dialogue": "Hello world!", "speaker": "Character_Buster" }
                      ]
                    }
                  ]
                }
                """);
            File.WriteAllBytes(Path.Combine(projDir, "assets", "video", "scene_01_clip_01.mp4"), new byte[2048]);

            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tempDir });
            var store = new ProjectStore(opts);
            var service = new ClipDialogueVerificationService(
                store, new StillImageOnlyVisionClient(), telemetry: null!, gemini: new FakeVideoAnalysisClient());

            var result = await service.VerifyClipDialogueAsync("test_proj", sceneNumber: 1, clipNumber: 1);

            Assert.Equal("unverified", result.Status);
            Assert.Contains("does not support native video", result.SummaryNote ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private sealed class FakeVideoAnalysisClient : IGeminiVideoAnalysisClient
    {
        public bool IsConfigured => true;

        public Task<string> CompleteWithImagesAsync(string prompt, System.Collections.Generic.IReadOnlyList<string> imagePaths, string model = "gemini-2.5-flash", string detail = "low", double temperature = 0.0, System.Threading.CancellationToken ct = default) =>
            Task.FromResult(@"{ ""detectedSpeaker"": ""gemini-native-video"", ""transcribedDialogue"": ""Hello world!"", ""dialogueAccuracyScore"": 1.0, ""speakerMatch"": true, ""status"": ""verified"" }");
    }

    /// <summary>Distinguishable from FakeVideoAnalysisClient's response so the test can tell which
    /// branch actually ran — if this response leaks through, the Gemini escape hatch was skipped.</summary>
    private sealed class StillImageOnlyVisionClient : IVisionClient
    {
        public bool IsConfigured => true;

        public Task<string> TranscribePageAsync(string imagePath, int page, string model = "grok-4.5", System.Threading.CancellationToken ct = default) =>
            Task.FromResult("");

        public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(string imagePath, int page, System.Collections.Generic.IReadOnlyList<CharacterClassifyHint> cast, string model = "grok-4.5", System.Threading.CancellationToken ct = default) =>
            Task.FromResult(new CharacterPageClassification());

        public Task<string> CompleteWithImagesAsync(string prompt, System.Collections.Generic.IReadOnlyList<string> imagePaths, string model = "grok-4.5", string detail = "low", double temperature = 0.0, System.Threading.CancellationToken ct = default) =>
            Task.FromResult(@"{ ""detectedSpeaker"": ""still-image-fallback"", ""transcribedDialogue"": ""Hello world!"", ""dialogueAccuracyScore"": 1.0, ""speakerMatch"": true, ""status"": ""verified"" }");
    }

    [Fact]
    public void LoadVerification_returns_null_when_file_does_not_exist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var projDir = Path.Combine(tempDir, "projects", "test_proj");
        Directory.CreateDirectory(projDir);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new PageToMovie.Core.Options.PageToMovieOptions { WorkspaceRoot = tempDir });
            var store = new ProjectStore(opts);
            var service = new ClipDialogueVerificationService(store, new MockVisionClient(), new ProjectTelemetryService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectTelemetryService>.Instance));
            var result = service.LoadVerification("test_proj", 1, 1);
            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SaveVerification_persists_json_report()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var projDir = Path.Combine(tempDir, "projects", "test_proj");
        Directory.CreateDirectory(projDir);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new PageToMovie.Core.Options.PageToMovieOptions { WorkspaceRoot = tempDir });
            var store = new ProjectStore(opts);
            var service = new ClipDialogueVerificationService(store, new MockVisionClient(), new ProjectTelemetryService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectTelemetryService>.Instance));

            var vo = new ClipDialogueVerificationResult
            {
                SceneNumber = 1,
                ClipNumber = 1,
                ExpectedSpeaker = "Buster",
                ExpectedDialogue = "Hello world!",
                DetectedSpeaker = "Buster",
                TranscribedDialogue = "Hello world!",
                DialogueAccuracyScore = 1.0,
                SpeakerMatch = true,
                Status = "verified",
            };

            await service.SaveVerificationAsync("test_proj", vo);

            var loaded = service.LoadVerification("test_proj", 1, 1);
            Assert.NotNull(loaded);
            Assert.Equal("Buster", loaded!.ExpectedSpeaker);
            Assert.Equal("verified", loaded.Status);
            Assert.Equal(1.0, loaded.DialogueAccuracyScore);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LooksTruncated_TrueWhenTranscribedIsMeaningfullyShorterThanExpected()
    {
        var result = new ClipDialogueVerificationResult
        {
            ExpectedDialogue = "I need you to listen to me very carefully right now.",
            TranscribedDialogue = "I need you to listen",
            Status = "mismatch",
        };
        Assert.True(ClipDialogueVerificationService.LooksTruncated(result));
    }

    [Fact]
    public void LooksTruncated_FalseWhenFullyMatched()
    {
        var result = new ClipDialogueVerificationResult
        {
            ExpectedDialogue = "Hello world!",
            TranscribedDialogue = "Hello world!",
            Status = "verified",
        };
        Assert.False(ClipDialogueVerificationService.LooksTruncated(result));
    }

    [Fact]
    public void LooksTruncated_FalseForSpeakerSwapEvenIfShorter()
    {
        // A speaker-identity mismatch is a different failure mode, not a timing/truncation problem.
        var result = new ClipDialogueVerificationResult
        {
            ExpectedDialogue = "I need you to listen to me very carefully right now.",
            TranscribedDialogue = "Get out",
            Status = "speaker_swap",
        };
        Assert.False(ClipDialogueVerificationService.LooksTruncated(result));
    }

    [Fact]
    public void CalculateAccuracyScore_IgnoresUSUKSpellingDifferences()
    {
        var exp = "A shriek was heard by a neighbour during the night. Suspicion of foul play.";
        var act = "A shriek was heard by a neighbor during the night. Suspicion of foul play.";

        var score = ClipDialogueVerificationService.CalculateAccuracyScore(exp, act);
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void CalculateAccuracyScore_FullMatch_Returns100Percent()
    {
        var exp = "And everywhere that Mary went, The lamb was sure to go.";
        var act = "And everywhere that Mary went, the lamb was sure to go.";

        var score = ClipDialogueVerificationService.CalculateAccuracyScore(exp, act);
        Assert.Equal(1.0, score);
    }

    private class MockVisionClient : PageToMovie.Engine.Abstractions.IVisionClient
    {
        public bool IsConfigured => true;

        public Task<string> TranscribePageAsync(string imagePath, int page, string model = "grok-4.5", System.Threading.CancellationToken ct = default) =>
            Task.FromResult("");

        public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(string imagePath, int page, System.Collections.Generic.IReadOnlyList<CharacterClassifyHint> cast, string model = "grok-4.5", System.Threading.CancellationToken ct = default) =>
            Task.FromResult(new CharacterPageClassification());

        public Task<string> CompleteWithImagesAsync(string prompt, System.Collections.Generic.IReadOnlyList<string> imagePaths, string model = "grok-4.5", string detail = "low", double temperature = 0.0, System.Threading.CancellationToken ct = default) =>
            Task.FromResult(@"{ ""detectedSpeaker"": ""Buster"", ""transcribedDialogue"": ""Hello world!"", ""dialogueAccuracyScore"": 1.0, ""speakerMatch"": true, ""status"": ""verified"" }");
    }
}
