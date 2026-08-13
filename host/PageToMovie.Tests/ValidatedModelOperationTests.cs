using System.Net.Http;
using PageToMovie.Engine.ModelExecution;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ValidatedModelOperationTests
{
    [Fact]
    public async Task Valid_primary_response_returns_primary_provenance()
    {
        var request = new StubOperation(_ => Task.FromResult(new ModelResponse<string>("ok", "test-model")));
        var result = await CreatePipeline(request).ExecuteAsync("input");

        Assert.True(result.Success);
        Assert.Equal(ModelResultSource.PrimaryResponse, result.Source);
        Assert.Equal("ok", result.Value!.Text);
        Assert.Equal(1, result.ModelCalls);
        Assert.NotNull(result.Attempts[0].RawResponseHash);
    }

    [Fact]
    public async Task Invalid_primary_is_corrected_with_exact_validation_issues()
    {
        ModelAttemptContext<string>? correction = null;
        var request = new StubOperation(context =>
        {
            if (context.Kind == ModelAttemptKind.Primary)
                return Task.FromResult(new ModelResponse<string>("missing", "test-model"));
            correction = context;
            return Task.FromResult(new ModelResponse<string>("ok", "test-model"));
        });

        var result = await CreatePipeline(request).ExecuteAsync("input");

        Assert.Equal(ModelResultSource.CorrectiveResponse, result.Source);
        Assert.Equal(2, result.ModelCalls);
        Assert.NotNull(correction);
        Assert.Equal("missing", correction!.PreviousResponse);
        Assert.Contains(correction.ValidationIssues, issue => issue.Code == "missing_value");
    }

    [Fact]
    public async Task Invalid_correction_uses_deterministic_fallback()
    {
        var request = new StubOperation(_ => Task.FromResult(new ModelResponse<string>("missing")));
        var result = await CreatePipeline(request).ExecuteAsync("source");

        Assert.Equal(ModelResultSource.DeterministicFallback, result.Source);
        Assert.Equal("fallback:source", result.Value!.Text);
        Assert.Equal(2, result.ModelCalls);
        Assert.Contains(result.ValidationIssues, issue => issue.Code == "missing_value");
    }

    [Fact]
    public async Task Transient_transport_failure_retries_before_semantic_correction()
    {
        var calls = 0;
        var request = new StubOperation(_ =>
        {
            calls++;
            if (calls == 1)
                throw new HttpRequestException("temporary");
            return Task.FromResult(new ModelResponse<string>("ok"));
        });

        var result = await CreatePipeline(request).ExecuteAsync("input");

        Assert.Equal(ModelResultSource.PrimaryResponse, result.Source);
        Assert.Equal(2, calls);
        Assert.Equal(2, result.ModelCalls);
        Assert.Single(result.Attempts);
    }

    [Fact]
    public async Task Cancellation_propagates_without_fallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = new StubOperation(_ => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreatePipeline(request).ExecuteAsync("input", cts.Token));
    }

    [Fact]
    public async Task Manifest_is_byte_reproducible_for_identical_replay()
    {
        var first = await CreateReplayPipeline().ExecuteAsync("same-input");
        var second = await CreateReplayPipeline().ExecuteAsync("same-input");

        Assert.Equal("fixture-v1", first.PromptVersion);
        Assert.Equal(first.InputHash, second.InputHash);
        Assert.Equal(ModelExecutionManifest.Serialize(first), ModelExecutionManifest.Serialize(second));
    }

    [Fact]
    public async Task Offline_replay_runs_correction_through_current_validation()
    {
        var replay = new ReplayModelOperation<string, string>(
            "stub",
            "fixture-v1",
            [new ModelResponse<string>("missing"), new ModelResponse<string>("ok")]);
        var pipeline = new ValidatedModelOperation<string, StubResult>(
            replay,
            new StubParser(),
            new StubValidator(),
            new StubFallback(),
            new ModelOperationOptions { TransportMaxAttempts = 1, CorrectiveMaxAttempts = 1 });

        var result = await pipeline.ExecuteAsync("input");

        Assert.Equal(ModelResultSource.CorrectiveResponse, result.Source);
        Assert.Equal(2, result.ModelCalls);
    }

    private static ValidatedModelOperation<string, StubResult> CreateReplayPipeline() =>
        new(
            new ReplayModelOperation<string, string>(
                "stub",
                "fixture-v1",
                [new ModelResponse<string>("ok", "recorded-model")]),
            new StubParser(),
            new StubValidator(),
            new StubFallback(),
            new ModelOperationOptions
            {
                TransportMaxAttempts = 1,
                CorrectiveMaxAttempts = 1,
                BehaviorVersions = new Dictionary<string, string> { ["lexicon"] = "test-v1" },
            });

    private static ValidatedModelOperation<string, StubResult> CreatePipeline(StubOperation operation) =>
        new(
            operation,
            new StubParser(),
            new StubValidator(),
            new StubFallback(),
            new ModelOperationOptions
            {
                TransportMaxAttempts = 2,
                CorrectiveMaxAttempts = 1,
                TransportBackoffMs = 1,
            });

    private sealed record StubResult(string Text);

    private sealed class StubOperation(
        Func<ModelAttemptContext<string>, Task<ModelResponse<string>>> execute)
        : IModelOperation<string, string>
    {
        public string OperationName => "stub";

        public Task<ModelResponse<string>> ExecuteAsync(
            string input,
            ModelAttemptContext<string> context,
            CancellationToken ct) => execute(context);
    }

    private sealed class StubParser : IModelResponseParser<string, StubResult>
    {
        public ModelParseResult<StubResult> Parse(string response) =>
            ModelParseResult<StubResult>.Success(new StubResult(response));
    }

    private sealed class StubValidator : IModelResultValidator<StubResult>
    {
        public IReadOnlyList<ModelValidationIssue> Validate(StubResult result) =>
            result.Text == "missing"
                ? [new ModelValidationIssue("missing_value", "A required value is missing.", "$.value")]
                : Array.Empty<ModelValidationIssue>();
    }

    private sealed class StubFallback : IDeterministicFallback<string, StubResult>
    {
        public StubResult Create(string input, IReadOnlyList<ModelValidationIssue> unresolvedIssues) =>
            new($"fallback:{input}");
    }
}
