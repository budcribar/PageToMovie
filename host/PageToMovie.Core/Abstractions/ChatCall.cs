namespace PageToMovie.Core.Abstractions;

/// <summary>
/// Shared chat-client + model + progress cluster for Stage-1 / Adaptation calls.
/// </summary>
public sealed record ChatCall(
    IChatClient Chat,
    string Model = "",
    ProgressCall Progress = default,
    double Temperature = 0.2,
    string? ReasoningEffort = null)
{
    public ChatCall(
        IChatClient chat,
        string model,
        CancellationToken ct,
        Action<string>? onProgress = null,
        double temperature = 0.2,
        string? reasoningEffort = null)
        : this(chat, model, new ProgressCall(ct, onProgress), temperature, reasoningEffort)
    {
    }

    public CancellationToken Ct => Progress.Ct;
    public Action<string>? OnProgress => Progress.OnProgress;

    public void Report(string message) => Progress.Report(message);

    public static ChatCall FromProgress(
        IChatClient chat,
        string model,
        IProgress<string>? progress,
        CancellationToken ct = default,
        double temperature = 0.2,
        string? reasoningEffort = null) =>
        new(chat, model, ProgressCall.From(progress, ct), temperature, reasoningEffort);
}
