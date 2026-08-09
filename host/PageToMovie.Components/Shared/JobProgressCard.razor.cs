using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class JobProgressCard
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
    [Parameter] public string? Kind { get; set; }
    [Parameter] public string TestId { get; set; } = "job-panel";
    /// <summary>Override for status span testid (e.g. "job-status" for existing Playwright).</summary>
    [Parameter] public string? StatusTestId { get; set; }
    /// <summary>Override for cancel button testid (e.g. "job-cancel").</summary>
    [Parameter] public string? CancelTestId { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    bool IsActive =>
        string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase);

    string BorderClass =>
        string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase) ? "border-danger"
        : IsActive ? "border-primary"
        : "border-secondary";

    int EffectivePercent()
    {
        if (Indeterminate) return 45;
        if (Percent is int p) return Math.Clamp(p, 0, 100);
        var st = Status?.Trim().ToLowerInvariant() ?? "";
        if (st == "queued" || Total <= 0) return 8;
        if (st == "running" && Index <= 0) return 0;
        return (int)Math.Round(100.0 * Math.Clamp(Index, 0, Total) / Math.Max(1, Total));
    }
}
