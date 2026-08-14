using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

/// <summary>
/// Code-behind for the admin "AI Calls" analytics page (<c>/admin/ai-calls</c>). A standalone admin
/// page — markup in AdminAiCalls.razor, logic here — that reads the aggregated model-call telemetry
/// (the read side of the AI-call feedback loop). Linked from the top-level Admin page.
/// </summary>
public partial class AdminAiCalls : ComponentBase
{
    [Inject] public required EngineApiClient Api { get; set; }
    [Inject] public required AdminSessionService Session { get; set; }

    internal AiCallAnalyticsDto? _data;
    internal bool _loading;
    internal string? _error;

    private static string SuccessRateClass(double pct)
    {
        if (pct >= 95) return "text-success";
        if (pct >= 80) return "text-warning";
        return "text-danger";
    }

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); } catch { /* session hydration is best-effort */ }
        if (Session.IsAdmin)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _data = await Api.GetAiCallAnalyticsAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }
}
