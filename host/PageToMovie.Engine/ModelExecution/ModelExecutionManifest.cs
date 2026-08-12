using System.Text.Json;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine.ModelExecution;

public static class ModelExecutionManifest
{
    public const string SchemaVersion = "1";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<TResult>(ValidatedModelResult<TResult> result)
        where TResult : class =>
        JsonSerializer.Serialize(new ManifestEnvelope<TResult>(SchemaVersion, result), Options) + "\n";
}

file sealed record ManifestEnvelope<TResult>(
    string SchemaVersion,
    ValidatedModelResult<TResult> Result)
    where TResult : class;

/// <summary>Offline operation that replays recorded raw responses through current parsing and validation.</summary>
public sealed class ReplayModelOperation<TInput, TRaw> : IModelOperation<TInput, TRaw>
{
    private readonly Queue<ModelResponse<TRaw>> _responses;

    public ReplayModelOperation(
        string operationName,
        string promptVersion,
        IEnumerable<ModelResponse<TRaw>> responses)
    {
        OperationName = operationName;
        PromptVersion = promptVersion;
        _responses = new Queue<ModelResponse<TRaw>>(responses);
    }

    public string OperationName { get; }
    public string PromptVersion { get; }

    public Task<ModelResponse<TRaw>> ExecuteAsync(
        TInput input,
        ModelAttemptContext<TRaw> context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_responses.Count == 0)
            throw new InvalidOperationException("The offline replay has no response for this attempt.");
        return Task.FromResult(_responses.Dequeue());
    }
}
