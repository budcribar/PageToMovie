using Microsoft.JSInterop;

namespace PageToMovie.Web.Services;

/// <summary>
/// JS → .NET bridge for multi-phase backup/export progress (download, media merge, pack).
/// </summary>
public sealed class ExportProgressSink
{
    private readonly Func<string, double?, string?, Task> _onReport;

    public ExportProgressSink(Func<string, double?, string?, Task> onReport)
        => _onReport = onReport ?? throw new ArgumentNullException(nameof(onReport));

    /// <param name="phase">wait | download | merge | pack | done</param>
    /// <param name="percent">0–100 within the phase, or null for indeterminate</param>
    /// <param name="message">Human-readable status line</param>
    [JSInvokable]
    public Task ReportAsync(string phase, double? percent, string? message)
        => _onReport(phase ?? "", percent, message);
}
