using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Components;

public sealed partial class JobProgressCard : IDisposable
{
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public string Status { get; set; } = "";
    [Parameter] public string Message { get; set; } = "";
    [Parameter] public string? DetailMessage { get; set; }
    [Parameter] public int Index { get; set; }
    [Parameter] public int Total { get; set; }
    [Parameter] public int? Percent { get; set; }
    [Parameter] public bool Indeterminate { get; set; }
    [Parameter] public bool ShowCancel { get; set; }
    [Parameter] public bool CancelDisabled { get; set; }
    [Parameter] public string CancelLabel { get; set; } = "Cancel";
    [Parameter] public EventCallback OnCancel { get; set; }
    [Parameter] public IReadOnlyList<string>? LogLines { get; set; }
    [Parameter] public bool ShowLog { get; set; }
    [Parameter] public string LogSummary { get; set; } = "Details (admin)";
    [Parameter] public int LogMaxLines { get; set; } = 24;
    /// <summary>When true, admin log details start expanded (finished jobs).</summary>
    [Parameter] public bool OpenLogByDefault { get; set; }
    [Parameter] public string? Kind { get; set; }
    [Parameter] public string TestId { get; set; } = "job-panel";
    /// <summary>Override for status span testid (e.g. "job-status" for existing Playwright).</summary>
    [Parameter] public string? StatusTestId { get; set; }
    /// <summary>Override for cancel button testid (e.g. "job-cancel").</summary>
    [Parameter] public string? CancelTestId { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    /// <summary>Optional live elapsed label (e.g. "4m 12s") shown while the job is active.</summary>
    [Parameter] public string? Elapsed { get; set; }
    /// <summary>When set, the card ticks a live elapsed clock from this instant.</summary>
    [Parameter] public DateTimeOffset? StartedAt { get; set; }
    /// <summary>
    /// When false, skip the progress bar entirely (message + optional log only).
    /// Default: show bar while active/indeterminate; hide for successful done (bar looked “stuck”).
    /// </summary>
    [Parameter] public bool? ForceShowProgressBar { get; set; }

    Timer? _elapsedTimer;

    bool IsActive =>
        string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase);

    bool IsDone => string.Equals(Status, "done", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Status, "partial", StringComparison.OrdinalIgnoreCase);

    bool IsError => string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase);

    string? DisplayElapsed
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Elapsed))
                return Elapsed;
            if (IsActive && StartedAt is DateTimeOffset started)
                return ElapsedClock.FormatSince(started);
            return null;
        }
    }

    /// <summary>
    /// Active jobs always get a bar. Successful done: hide by default (message is enough).
    /// Error keeps a static red bar. ForceShowProgressBar overrides.
    /// </summary>
    bool ShowProgressBar
    {
        get
        {
            if (ForceShowProgressBar is bool forced)
                return forced;
            if (Indeterminate || IsActive)
                return true;
            if (IsError)
                return true;
            // done / cancelled / idle — no perpetual full bar
            return false;
        }
    }

    string BorderClass
    {
        get
        {
            if (IsError) return "border-danger";
            if (IsActive) return "border-primary";
            if (IsDone) return "border-success";
            return "border-secondary";
        }
    }

    string ProgressBarClasses
    {
        get
        {
            if (IsActive || Indeterminate)
                return "progress-bar progress-bar-striped progress-bar-animated bg-primary";
            if (string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase))
                return "progress-bar bg-danger";
            if (string.Equals(Status, "done", StringComparison.OrdinalIgnoreCase))
                return "progress-bar bg-success";
            return "progress-bar bg-secondary";
        }
    }

    protected override void OnParametersSet() => SyncElapsedTimer();

    int EffectivePercent()
    {
        if (Indeterminate) return 45;
        if (Percent is int p) return Math.Clamp(p, 0, 100);
        var st = Status?.Trim().ToLowerInvariant() ?? "";
        if (st is "done" or "partial") return 100;
        if (st == "error") return 100;
        if (st == "queued" || Total <= 0) return 8;
        if (st == "running" && Index <= 0) return 0;
        return (int)Math.Round(100.0 * Math.Clamp(Index, 0, Total) / Math.Max(1, Total));
    }

    void SyncElapsedTimer()
    {
        var need = IsActive && StartedAt is not null && string.IsNullOrWhiteSpace(Elapsed);
        if (need)
        {
            _elapsedTimer ??= new Timer(
                _ =>
                {
                    try
                    {
                        InvokeAsync(StateHasChanged).ContinueWith(
                            static t => t.Exception?.Handle(static _ => true),
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    catch { /* disposed */ }
                },
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
            return;
        }

        StopElapsedTimer();
    }

    void StopElapsedTimer()
    {
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;
    }

    public void Dispose()
    {
        StopElapsedTimer();
        GC.SuppressFinalize(this);
    }
}
