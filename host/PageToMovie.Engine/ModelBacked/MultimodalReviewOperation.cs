using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine.ModelBacked;

public sealed record MultimodalReviewObservation(
    string Scope,
    IReadOnlyList<string> EvidenceLabels,
    string Prompt);

/// <summary>Shared validated lifecycle for judgments made from image observations.</summary>
public sealed class MultimodalReviewOperation<TJudgment>
    where TJudgment : class
{
    private readonly ValidatedModelOperation<MultimodalReviewObservation, TJudgment> _pipeline;

    public MultimodalReviewOperation(
        IVisionClient vision,
        IReadOnlyList<string> imagePaths,
        string model,
        string operationName,
        string promptVersion,
        Func<string, ModelParseResult<TJudgment>> parse,
        Func<TJudgment, IReadOnlyList<ModelValidationIssue>> validate)
    {
        _pipeline = new ValidatedModelOperation<MultimodalReviewObservation, TJudgment>(
            new VisionOperation(vision, imagePaths, model, operationName, promptVersion),
            new Parser(parse),
            new Validator(validate),
            new RejectFallback(),
            new ModelOperationOptions
            {
                CorrectiveMaxAttempts = 1,
                BehaviorVersions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["observation-judgment-schema"] = "1",
                },
            });
    }

    public Task<ValidatedModelResult<TJudgment>> ExecuteAsync(
        MultimodalReviewObservation observation,
        CancellationToken ct = default) => _pipeline.ExecuteAsync(observation, ct);

    private sealed class VisionOperation(
        IVisionClient vision,
        IReadOnlyList<string> imagePaths,
        string model,
        string operationName,
        string promptVersion) : IModelOperation<MultimodalReviewObservation, string>
    {
        public string OperationName => operationName;
        public string PromptVersion => promptVersion;

        public async Task<ModelResponse<string>> ExecuteAsync(
            MultimodalReviewObservation input,
            ModelAttemptContext<string> context,
            CancellationToken ct)
        {
            var prompt = input.Prompt;
            if (context.Kind == ModelAttemptKind.Correction)
            {
                prompt += "\n\nCORRECTION REQUIRED: Return the complete requested format. Fix only these validation errors:\n" +
                          string.Join("\n", context.ValidationIssues.Select(i => $"- {i.Path ?? "$"}: {i.Message}"));
            }

            var raw = await vision.CompleteWithImagesAsync(
                prompt, imagePaths, model: model, detail: "low", ct: ct).ConfigureAwait(false);
            return new ModelResponse<string>(raw, model);
        }
    }

    private sealed class Parser(Func<string, ModelParseResult<TJudgment>> parse)
        : IModelResponseParser<string, TJudgment>
    {
        public ModelParseResult<TJudgment> Parse(string response) => parse(response);
    }

    private sealed class Validator(Func<TJudgment, IReadOnlyList<ModelValidationIssue>> validate)
        : IModelResultValidator<TJudgment>
    {
        public IReadOnlyList<ModelValidationIssue> Validate(TJudgment result) => validate(result);
    }

    private sealed class RejectFallback : IDeterministicFallback<MultimodalReviewObservation, TJudgment>
    {
        public TJudgment Create(
            MultimodalReviewObservation input,
            IReadOnlyList<ModelValidationIssue> unresolvedIssues) =>
            throw new InvalidOperationException(
                $"{input.Scope} did not return a valid judgment: " +
                string.Join(" ", unresolvedIssues.Select(i => i.Message)));
    }
}
