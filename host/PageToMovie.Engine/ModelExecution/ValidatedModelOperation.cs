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
        var attempts = new List<ModelOperationAttempt>();
        var unresolved = new List<ModelValidationIssue>();
        string? previous = default;
        string? lastModel = null;
        string? lastError = null;
        var semanticAttemptCount = 1 + Math.Max(0, _options.CorrectiveMaxAttempts);

        for (var semanticAttempt = 1; semanticAttempt <= semanticAttemptCount; semanticAttempt++)
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
                            new ModelAttemptContext<string>(kind, semanticAttempt, previous, unresolved.ToArray()),
                            ct).ConfigureAwait(false);
                    },
                    _isTransient,
                    _options.TransportMaxAttempts,
                    _options.TransportBackoffMs,
                    ct: ct).ConfigureAwait(false);

                previous = response.Raw;
                lastModel = response.Model ?? lastModel;
                var parsed = _parser.Parse(response.Raw);
                var issues = new List<ModelValidationIssue>(parsed.Issues);
                if (parsed.Value is not null)
                    issues.AddRange(_validator.Validate(parsed.Value));

                unresolved = issues
                    .Where(issue => issue.Severity == ModelValidationSeverity.Error)
                    .ToList();

                attempts.Add(new ModelOperationAttempt(
                    kind,
                    semanticAttempt,
                    Math.Max(1, transportAttempts),
                    response.Model,
                    HashRawResponse(response.Raw),
                    issues,
                    null));

                if (parsed.Value is not null && unresolved.Count == 0)
                {
                    return ModelOperationTraceScope.Record(AddReproducibility(input, new ValidatedModelResult<TResult>(
                        parsed.Value,
                        kind == ModelAttemptKind.Primary
                            ? ModelResultSource.PrimaryResponse
                            : ModelResultSource.CorrectiveResponse,
                        _operation.OperationName,
                        lastModel,
                        attempts,
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
                lastError = ex.Message;
                unresolved =
                [
                    new ModelValidationIssue("model_call_failed", ex.Message),
                ];
                attempts.Add(new ModelOperationAttempt(
                    kind,
                    semanticAttempt,
                    Math.Max(1, transportAttempts),
                    lastModel,
                    null,
                    unresolved.ToArray(),
                    ex.Message));
            }
        }

        try
        {
            var fallback = _fallback.Create(input, unresolved);
            return ModelOperationTraceScope.Record(AddReproducibility(input, new ValidatedModelResult<TResult>(
                fallback,
                ModelResultSource.DeterministicFallback,
                _operation.OperationName,
                lastModel,
                attempts,
                unresolved,
                lastError)));
        }
        catch (Exception ex)
        {
            var issues = unresolved
                .Append(new ModelValidationIssue("fallback_failed", ex.Message))
                .ToArray();
            return ModelOperationTraceScope.Record(AddReproducibility(input, new ValidatedModelResult<TResult>(
                null,
                ModelResultSource.Failed,
                _operation.OperationName,
                lastModel,
                attempts,
                issues,
                ex.Message)));
        }
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
