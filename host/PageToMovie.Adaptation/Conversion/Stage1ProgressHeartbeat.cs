using PageToMovie.Core.Utils;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// 15s ticks while a Stage‑1 model call is in flight so the job card does not look hung.
/// Replaces the previous heartbeat line (same prefix) rather than flooding the log.
/// </summary>
internal sealed class Stage1ProgressHeartbeat : IDisposable
{
    public const string LinePrefix = "Still working — ";

    private readonly Action<string> _onProgress;
    private readonly string _label;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Timer _timer;

    private Stage1ProgressHeartbeat(Action<string> onProgress, string label)
    {
        _onProgress = onProgress;
        _label = string.IsNullOrWhiteSpace(label) ? "this step" : label.Trim();
        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public static Stage1ProgressHeartbeat? Start(Action<string>? onProgress, string label)
        => onProgress is null ? null : new Stage1ProgressHeartbeat(onProgress, label);

    public static bool IsHeartbeatLine(string? line) =>
        !string.IsNullOrWhiteSpace(line)
        && (line.StartsWith(LinePrefix, StringComparison.Ordinal)
            || line.StartsWith("Still writing", StringComparison.Ordinal)
            || line.StartsWith("Still generating", StringComparison.Ordinal));

    private void Tick(object? _)
    {
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _started;
            _onProgress($"{LinePrefix}{_label} ({ElapsedClock.Format(elapsed)})");
        }
        catch
        {
            /* UI sink disposed */
        }
    }

    public void Dispose() => _timer.Dispose();
}
