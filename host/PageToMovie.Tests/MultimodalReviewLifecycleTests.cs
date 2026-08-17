using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelBacked;
using Xunit;

namespace PageToMovie.Tests;

public sealed class MultimodalReviewLifecycleTests
{
    [Fact]
    public async Task Clip_review_replays_invalid_response_then_corrects_with_provenance()
    {
        var valid = """
            {"suggestion":"pass","category":"continuity","confidence":"high","continuity":"ok","note":"The subject remains aligned across the cut.","suggestions":[]}
            """;
        var first = await ExecuteClipAsync(new SequenceVisionClient("not json", valid));
        var replay = await ExecuteClipAsync(new SequenceVisionClient("not json", valid));

        Assert.True(first.Success);
        Assert.Equal(ModelResultSource.CorrectiveResponse, first.Source);
        Assert.Equal(2, first.Attempts.Count);
        Assert.Equal("clip-auto-review.v1", first.PromptVersion);
        Assert.False(string.IsNullOrWhiteSpace(first.InputHash));
        Assert.Contains(first.Attempts[0].ValidationIssues, issue => issue.Code == "invalid_json");
        Assert.Equal(ModelExecutionManifest.Serialize(first), ModelExecutionManifest.Serialize(replay));
    }

    [Fact]
    public async Task Movie_review_requests_missing_observations_before_accepting_judgment()
    {
        var incomplete = """{"overallScore":8,"continuityScore":8}""";
        var complete = """
            {"overallScore":8,"continuityScore":8,"characterScore":9,"lightingScore":7,"pacingScore":8,"dialogueScore":8,"musicScore":7,"continuityNotes":"Blocking remains stable.","visualConsistencyNotes":"Wardrobe remains consistent.","lightingNotes":"Exposure shifts slightly warmer.","dialogueNotes":"Speaking posture matches the beat.","audioNotes":"The cue ends cleanly."}
            """;
        var vision = new SequenceVisionClient(incomplete, complete);
        var operation = new MultimodalReviewOperation<MovieSceneGroupFeedback>(
            vision, ["frame.jpg"], "catalog-review-model", "movie_scene_group_review", "movie-scene-review.v1",
            raw => MovieAutoReviewService.ParseSceneGroupFeedback(raw, "Scenes 1-2", [1, 2]),
            MovieAutoReviewService.ValidateSceneGroupFeedback);

        var result = await operation.ExecuteAsync(
            new MultimodalReviewObservation("Scenes 1-2", ["SCENE_01", "SCENE_02"], "review"));

        Assert.True(result.Success);
        Assert.Equal(ModelResultSource.CorrectiveResponse, result.Source);
        Assert.Contains("CORRECTION REQUIRED", vision.Prompts[1]);
        Assert.Contains("characterScore", vision.Prompts[1]);
        Assert.Equal(9, result.Value!.CharacterScore);
    }

    private static Task<ValidatedModelResult<ClipAutoReviewDraft>> ExecuteClipAsync(IVisionClient vision)
    {
        var operation = new MultimodalReviewOperation<ClipAutoReviewDraft>(
            vision, ["frame.jpg"], "catalog-review-model", "clip_multimodal_review", "clip-auto-review.v1",
            raw => ClipAutoReviewService.ParseDraftForReplay(raw, "project", 1, 1, false),
            ClipAutoReviewService.ValidateDraft);
        return operation.ExecuteAsync(new MultimodalReviewObservation("Clip S01C01", ["CURRENT_CLIP"], "review"));
    }

    private sealed class SequenceVisionClient(params string[] responses) : IVisionClient
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> Prompts { get; } = [];
        public bool IsConfigured => true;

        public Task<string> TranscribePageAsync(string imagePath, int page, string model = "", CancellationToken ct = default) =>
            Task.FromResult("");

        public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
            string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
            string model = "", CancellationToken ct = default) =>
            Task.FromResult(new CharacterPageClassification());

        public Task<string> CompleteWithImagesAsync(
            string prompt, IReadOnlyList<string> imagePaths, string model = "",
            string detail = "low", double temperature = 0.0, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
