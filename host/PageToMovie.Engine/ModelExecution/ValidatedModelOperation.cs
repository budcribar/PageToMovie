using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PageToMovie.Engine.ModelExecution;

/// <summary>
/// Executes a model operation through transport retry, parse/domain validation, focused
/// corrective requests, and finally a deterministic fallback. Caller cancellation always
/// propagates. Ordinary model and fallback failures are represented in the returned provenance.
/// </summary>
public sealed class ValidatedModelOperation<TInput, TResult>
    where TResult : class
{
    private readonly IModelOperation<TInput, string> _operation;
    private readonly IModelResponseParser<string, TResult> _parser;
    private readonly IModelResultValidator<TResult> _validator;
    private readonly IDeterministicFallback<TInput, TResult> _fallback;
    private readonly ModelOperationOptions _options;
    private readonly Func<Exception, bool> _isTransient;

    public ValidatedModelOperation(
        IModelOperation<TInput, string> operation,
        IModelResponseParser<string, TResult> parser,
        IModelResultValidator<TResult> validator,
        IDeterministicFallback<TInput, TResult> fallback,
        ModelOperationOptions? options = null,
        Func<Exception, bool>? isTransient = null)
    {
        _operation = operation;
        _parser = parser;
        _validator = validator;
        _fallback = fallback;
        _options = options ?? new ModelOperationOptions();
        _isTransient = isTransient ?? AiRetryPolicy.IsTransientChatFailure;
    }

    public async Task<ValidatedModelResult<TResult>> ExecuteAsync(
        TInput input,
        CancellationToken ct = default)
    {
        var state = new ExecuteState();
        var semanticAttemptCount = 1 + Math.Max(0, _options.CorrectiveMaxAttempts);

        for (var semanticAttempt = 1; semanticAttempt <= semanticAttemptCount; semanticAttempt++)
        {
            var completed = await ExecuteSemanticAttemptAsync(input, semanticAttempt, state, ct)
                .ConfigureAwait(false);
            if (completed is not null)
                return completed;
        }

        return ExecuteFallback(input, state);
    }

    private async Task<ValidatedModelResult<TResult>?> ExecuteSemanticAttemptAsync(
        TInput input,
        int semanticAttempt,
        ExecuteState state,
        CancellationToken ct)
    {
        var kind = semanticAttempt == 1 ? ModelAttemptKind.Primary : ModelAttemptKind.Correction;
        var transportAttempts = 0;

        try
        {
            var response = await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                async transportAttempt =>
                {
                    transportAttempts = transportAttempt;
                    return await _operation.ExecuteAsync(
                        input,
                        new ModelAttemptContext<string>(kind, semanticAttempt, state.Previous, state.Unresolved.ToArray()),
                        ct).ConfigureAwait(false);
                },
                _isTransient,
                _options.TransportMaxAttempts,
                _options.TransportBackoffMs,
                ct: ct).ConfigureAwait(false);

            state.Previous = response.Raw;
            state.LastModel = response.Model ?? state.LastModel;
            var (value, issues, unresolved) = ParseAndValidate(response.Raw);
            state.Unresolved = unresolved;

            state.Attempts.Add(new ModelOperationAttempt(
                kind,
                semanticAttempt,
                Math.Max(1, transportAttempts),
                response.Model,
                HashRawResponse(response.Raw),
                issues,
                null));

            if (value is not null && unresolved.Count == 0)
            {
                return ModelOperationTraceScope.Record(AddReproducibility(input, new ValidatedModelResult<TResult>(
                    value,
                    kind == ModelAttemptKind.Primary
                        ? ModelResultSource.PrimaryResponse
                        : ModelResultSource.CorrectiveResponse,
                    _operation.OperationName,
                    state.LastModel,
                    state.Attempts,
                    issues,
                    null)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            state.Unresolved =
            [
                new ModelValidationIssue("model_call_failed", ex.Message),
            ];
            state.Attempts.Add(new ModelOperationAttempt(
                kind,
                semanticAttempt,
                Math.Max(1, transportAttempts),
                state.LastModel,
                null,
                state.Unresolved.ToArray(),
                ex.Message));
        }

        return null;
    }

    private (TResult? Value, List<ModelValidationIssue> Issues, List<ModelValidationIssue> Unresolved)
        ParseAndValidate(string raw)
    {
        var parsed = _parser.Parse(raw);
        var issues = new List<ModelValidationIssue>(parsed.Issues);
        if (parsed.Value is not null)
            issues.AddRange(_validator.Validate(parsed.Value));

        var unresolved = issues
            .Where(issue => issue.Severity == ModelValidationSeverity.Error)
            .ToList();
        return (parsed.Value, issues, unresolved);
    }

    private ValidatedModelResult<TResult> ExecuteFallback(TInput input, ExecuteState state)
    {
        try
        {
            var fallback = _fallback.Create(input, state.Unresolved);
            return ModelOperationTraceScope.Record(AddReproducibility(input, new ValidatedModelResult<TResult>(
                fallback,
                ModelResultSource.DeterministicFallback,
                _operation.OperationName,
                state.LastModel,
                state.Attempts,
                state.Unresolved,
                state.LastError)));
        }
        catch (Exception ex)
        {
            var issues = state.Unresolved
                .Append(new ModelValidationIssue("fallback_failed", ex.Message))
                .ToArray();
            return ModelOperationTraceScope.Record(AddReproducibility(input, new ValidatedModelResult<TResult>(
                null,
                ModelResultSource.Failed,
                _operation.OperationName,
                state.LastModel,
                state.Attempts,
                issues,
                ex.Message)));
        }
    }

    private sealed class ExecuteState
    {
        public List<ModelOperationAttempt> Attempts { get; } = new();
        public List<ModelValidationIssue> Unresolved { get; set; } = new();
        public string? Previous;
        public string? LastModel;
        public string? LastError;
    }

    private static string HashRawResponse(string response)
    {
        var text = response?.ToString() ?? "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private ValidatedModelResult<TResult> AddReproducibility(
        TInput input,
        ValidatedModelResult<TResult> result) =>
        result with
        {
            InputHash = HashText(JsonSerializer.Serialize(input)),
            PromptVersion = _operation.PromptVersion,
            BehaviorVersions = new SortedDictionary<string, string>(
                _options.BehaviorVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
        };

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
