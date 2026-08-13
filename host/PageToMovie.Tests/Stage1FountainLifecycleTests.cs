using PageToMovie.Engine;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelBacked;
using PageToMovie.Engine.ModelExecution;
using Xunit;

namespace PageToMovie.Tests;

public sealed class Stage1FountainLifecycleTests
{
    private const string CompletePackage = """
        Title: Lamb

        FADE IN:

        EXT. SCHOOLYARD - DAY

        MARY walks beside her lamb while the children laugh together in the morning sun.

        MARY
        My lamb follows me wherever I go.

        FADE OUT.

        THE END

        ---VISION_META---
        {"visual_medium":"illustrated_picture_book","render_style_lock":"watercolor nursery-book continuity","notes":"verse picture book"}
        ---END_VISION_META---
        """;

    [Fact]
    public async Task Recorded_primary_package_is_accepted_with_stable_provenance()
    {
        var result = await ExecuteAsync(new QueueChatClient(CompletePackage));

        Assert.Equal(ModelResultSource.PrimaryResponse, result.Source);
        Assert.Single(result.Attempts);
        Assert.Equal("stage1-replay-v1", result.PromptVersion);
        Assert.NotNull(result.InputHash);
        Assert.NotNull(result.Attempts[0].RawResponseHash);
    }

    [Fact]
    public async Task Recorded_malformed_primary_uses_focused_correction()
    {
        var chat = new QueueChatClient("not fountain", CompletePackage);
        var result = await ExecuteAsync(chat);

        Assert.Equal(ModelResultSource.CorrectiveResponse, result.Source);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Contains("Validation findings", chat.UserPrompts[1], StringComparison.Ordinal);
        Assert.Contains("complete corrected Fountain package", chat.UserPrompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recorded_invalid_responses_use_explicit_deterministic_terminal_policy()
    {
        var fallback = CompletePackage.Replace("watercolor nursery-book continuity", "fallback continuity");
        var result = await ExecuteAsync(new QueueChatClient("bad one", "bad two"), fallback);

        Assert.Equal(ModelResultSource.DeterministicFallback, result.Source);
        Assert.Equal(fallback, result.Value!.FountainPackage);
        Assert.Equal(2, result.Attempts.Count);
    }

    private static Task<ValidatedModelResult<Stage1FountainResponse>> ExecuteAsync(
        IChatClient chat,
        string? fallback = null) =>
        Stage1FountainLifecycle.ExecuteAsync(
            chat,
            new Stage1FountainRequest(
                "system", "adapt", "grok-4.5", 0.2, "test",
                "stage1-replay-v1", "Fix the exact validation findings.",
                DeterministicFallback: fallback),
            ValidatePackage,
            CancellationToken.None);

    private static IReadOnlyList<ModelValidationIssue> ValidatePackage(string package)
    {
        var split = PageToMovie.Engine.ProjectVisionMeta.SplitVisionMetaTrailer(package);
        var issues = new List<ModelValidationIssue>();
        if (!AdaptationFountain.LooksLikeGoodFountain(split.Fountain))
            issues.Add(new("invalid_fountain", "Fountain is invalid.", "$.fountain"));
        if (split.Vision is null)
            issues.Add(new("missing_vision_meta", "VISION_META is required.", "$.vision_meta"));
        return issues;
    }

    private sealed class QueueChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> UserPrompts { get; } = [];
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null,
            string? reasoningEffort = null)
        {
            ct.ThrowIfCancellationRequested();
            UserPrompts.Add(userPrompt);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
