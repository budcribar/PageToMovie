namespace PageToMovie.Core.Abstractions;

/// <summary>
/// Shared cancellation + progress callback for long-running studio jobs.
/// </summary>
public readonly record struct ProgressCall(
    CancellationToken Ct = default,
    Action<string>? OnProgress = null)
{
    public void Report(string message) => OnProgress?.Invoke(message);

    public static ProgressCall From(IProgress<string>? progress, CancellationToken ct = default) =>
        new(ct, progress is null ? null : progress.Report);
}
