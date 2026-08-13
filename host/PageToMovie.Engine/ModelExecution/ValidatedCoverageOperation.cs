namespace PageToMovie.Engine.ModelExecution;

/// <summary>
/// Adapts batched id→value classifiers to the shared model lifecycle. Parsed values are merged
/// across semantic attempts; validation reports the exact missing IDs, and corrective calls may
/// request only those IDs.
/// </summary>
public static class ValidatedCoverageOperation
{
    public static async Task<(ValidatedModelResult<Dictionary<string, T>> Lifecycle, AiRetryPolicy.CoverageRetryResult<T> Compatibility)>
        ExecuteAsync<T>(
            string operationName,
            string promptVersion,
            IReadOnlyList<string> requestedIds,
            Func<ModelAttemptContext<string>, IReadOnlyList<string>, Task<ModelResponse<string>>> call,
            Func<string, Dictionary<string, T>?> parse,
            int correctiveMaxAttempts,
            int transportMaxAttempts,
            int transportBackoffMs,
            CancellationToken ct = default)
    {
        var input = new CoverageInput(requestedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var parser = new MergingParser<T>(parse);
        var pipeline = new ValidatedModelOperation<CoverageInput, string, Dictionary<string, T>>(
            new CoverageOperation(operationName, promptVersion, call),
            parser,
            new CoverageValidator<T>(input.RequestedIds),
            new PartialCoverageFallback<T>(parser),
            new ModelOperationOptions
            {
                CorrectiveMaxAttempts = Math.Max(0, correctiveMaxAttempts),
                TransportMaxAttempts = Math.Max(1, transportMaxAttempts),
                TransportBackoffMs = transportBackoffMs,
                BehaviorVersions = new Dictionary<string, string> { ["coverage"] = "1" },
            });
        var lifecycle = await pipeline.ExecuteAsync(input, ct).ConfigureAwait(false);
        var values = lifecycle.Value is { Count: > 0 } ? lifecycle.Value : null;
        IEnumerable<string> returnedIds = values is null ? Array.Empty<string>() : values.Keys;
        var (missing, covered) = AiRetryPolicy.CheckCoverage(input.RequestedIds, returnedIds);
        var compatibility = new AiRetryPolicy.CoverageRetryResult<T>
        {
            Result = values,
            Missing = missing,
            FullyCovered = covered,
            Attempts = lifecycle.Attempts.Count,
            ReturnedCount = input.RequestedIds.Count - missing.Count,
            LastRawResponse = parser.LastRaw,
            LastError = lifecycle.Error ?? lifecycle.Attempts.LastOrDefault(attempt =>
                !string.IsNullOrWhiteSpace(attempt.Error))?.Error,
        };
        return (lifecycle, compatibility);
    }
}

file sealed record CoverageInput(IReadOnlyList<string> RequestedIds);

file sealed class CoverageOperation(
    string operationName,
    string promptVersion,
    Func<ModelAttemptContext<string>, IReadOnlyList<string>, Task<ModelResponse<string>>> call)
    : IModelOperation<CoverageInput, string>
{
    public string OperationName => operationName;
    public string PromptVersion => promptVersion;

    public Task<ModelResponse<string>> ExecuteAsync(
        CoverageInput input, ModelAttemptContext<string> context, CancellationToken ct)
    {
        var missing = context.Kind == ModelAttemptKind.Primary
            ? input.RequestedIds
            : context.ValidationIssues
                .Where(issue => issue.Code == "missing_id" && issue.Path is { Length: > 2 })
                .Select(issue =>
                {
                    var path = issue.Path ?? "";
                    return path.Length > 2 ? path[2..] : path;
                })
                .ToArray();
        return call(context, missing.Count > 0 ? missing : input.RequestedIds);
    }
}

file sealed class MergingParser<T>(Func<string, Dictionary<string, T>?> parse)
    : IModelResponseParser<string, Dictionary<string, T>>
{
    private readonly Dictionary<string, T> _merged = new(StringComparer.OrdinalIgnoreCase);
    public string? LastRaw { get; private set; }
    public IReadOnlyDictionary<string, T> Merged => _merged;

    public ModelParseResult<Dictionary<string, T>> Parse(string response)
    {
        LastRaw = response;
        Dictionary<string, T>? parsed;
        try { parsed = parse(response); }
        catch (Exception ex)
        {
            return ModelParseResult<Dictionary<string, T>>.Failure(
                new ModelValidationIssue("invalid_json", ex.Message, "$"));
        }
        if (parsed is null)
            return ModelParseResult<Dictionary<string, T>>.Failure(
                new ModelValidationIssue("invalid_json", "The response did not contain a usable id map.", "$"));
        foreach (var pair in parsed) _merged[pair.Key] = pair.Value;
        return ModelParseResult<Dictionary<string, T>>.Success(
            new Dictionary<string, T>(_merged, StringComparer.OrdinalIgnoreCase));
    }
}

file sealed class CoverageValidator<T>(IReadOnlyList<string> requestedIds)
    : IModelResultValidator<Dictionary<string, T>>
{
    public IReadOnlyList<ModelValidationIssue> Validate(Dictionary<string, T> result) =>
        requestedIds
            .Where(id => !result.ContainsKey(id))
            .Select(id => new ModelValidationIssue("missing_id", $"Required id '{id}' is missing.", "$." + id))
            .ToArray();
}

file sealed class PartialCoverageFallback<T>(MergingParser<T> parser)
    : IDeterministicFallback<CoverageInput, Dictionary<string, T>>
{
    public Dictionary<string, T> Create(CoverageInput input, IReadOnlyList<ModelValidationIssue> unresolvedIssues) =>
        new(parser.Merged, StringComparer.OrdinalIgnoreCase);
}
