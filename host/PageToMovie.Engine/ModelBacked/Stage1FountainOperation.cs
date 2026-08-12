using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;

namespace PageToMovie.Engine.ModelBacked;

internal sealed record Stage1FountainRequest(
    string SystemPrompt,
    string UserPrompt,
    string Model,
    double Temperature,
    string Mode,
    string PromptVersion,
    string CorrectionInstruction,
    string? ReasoningEffort = null,
    string? DeterministicFallback = null,
    string OperationName = "stage1_book_to_fountain");

/// <summary>
/// Shared, versioned model boundary for Stage 1 Fountain generation and focused repairs.
/// Transport retry, semantic correction, parsing, validation, and terminal policy are owned by
/// <see cref="ValidatedModelOperation{TInput,TRaw,TResult}"/> rather than converter-local loops.
/// </summary>
internal static class Stage1FountainLifecycle
{
    internal static Task<ValidatedModelResult<Stage1FountainResponse>> ExecuteAsync(
        IChatClient chat,
        Stage1FountainRequest request,
        Func<string, IReadOnlyList<ModelValidationIssue>> validate,
        CancellationToken ct)
    {
        var pipeline = new ValidatedModelOperation<Stage1FountainRequest, string, Stage1FountainResponse>(
            new Operation(chat),
            new Parser(),
            new Validator(validate),
            new Fallback(),
            new ModelOperationOptions
            {
                TransportMaxAttempts = 2,
                CorrectiveMaxAttempts = 1,
                BehaviorVersions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fountain_parser"] = "1",
                    ["stage1_terminal_policy"] = "1",
                },
            });
        return pipeline.ExecuteAsync(request, ct);
    }
}

file sealed class Operation(IChatClient chat) : IModelOperation<Stage1FountainRequest, string>
{
    public string OperationName { get; private set; } = "stage1_book_to_fountain";
    public string PromptVersion { get; private set; } = "unversioned";

    public async Task<ModelResponse<string>> ExecuteAsync(
        Stage1FountainRequest input,
        ModelAttemptContext<string> context,
        CancellationToken ct)
    {
        OperationName = input.OperationName;
        PromptVersion = input.PromptVersion;
        var user = input.UserPrompt;
        var mode = input.Mode;
        var temperature = input.Temperature;
        if (context.Kind == ModelAttemptKind.Correction)
        {
            user += $"""


                CORRECTIVE ATTEMPT ({input.PromptVersion})
                {input.CorrectionInstruction}
                Validation findings:
                {string.Join("\n", context.ValidationIssues.Select(i => $"- {i.Path ?? "$"}: {i.Message}"))}
                Return the complete corrected Fountain package only. Do not return a patch list.
                Previous response:
                --- BEGIN PREVIOUS RESPONSE ---
                {context.PreviousResponse}
                --- END PREVIOUS RESPONSE ---
                """;
            mode += "_correction";
            temperature = Math.Min(temperature, 0.15);
        }

        var raw = await chat.CompleteAsync(
            input.SystemPrompt,
            user,
            input.Model,
            temperature,
            ct,
            mode: mode,
            reasoningEffort: input.ReasoningEffort).ConfigureAwait(false);
        return new ModelResponse<string>(raw, input.Model);
    }
}

file sealed class Parser : IModelResponseParser<string, Stage1FountainResponse>
{
    public ModelParseResult<Stage1FountainResponse> Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return ModelParseResult<Stage1FountainResponse>.Failure(
                new ModelValidationIssue("empty_response", "The Stage 1 response was empty."));

        var cleaned = AdaptationFountain.StripBookPageTags(
            AdaptationFountain.StripFences(response));
        return ModelParseResult<Stage1FountainResponse>.Success(new Stage1FountainResponse(cleaned));
    }
}

file sealed class Validator(Func<string, IReadOnlyList<ModelValidationIssue>> validate)
    : IModelResultValidator<Stage1FountainResponse>
{
    public IReadOnlyList<ModelValidationIssue> Validate(Stage1FountainResponse result) =>
        validate(result.FountainPackage);
}

file sealed class Fallback : IDeterministicFallback<Stage1FountainRequest, Stage1FountainResponse>
{
    public Stage1FountainResponse Create(
        Stage1FountainRequest input,
        IReadOnlyList<ModelValidationIssue> unresolvedIssues) =>
        input.DeterministicFallback is not null
            ? new Stage1FountainResponse(input.DeterministicFallback)
            : throw new InvalidOperationException(
                $"Stage 1 operation did not produce a valid response: {string.Join("; ", unresolvedIssues.Select(i => i.Message))}");
}

internal sealed record Stage1FountainResponse(string FountainPackage);
