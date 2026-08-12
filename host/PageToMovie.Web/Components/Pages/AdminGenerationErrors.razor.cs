using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

/// <summary>
/// Code-behind for the admin "Generation Errors" page (<c>/admin/generation-errors</c>). A standalone
/// admin page — markup in AdminGenerationErrors.razor, logic here — that reads recent
/// <c>generation_errors</c> rows (partial-coverage / structural-gate / transient-retry events) that
/// were previously write-only (logged via GenerationErrorLogger, never surfaced anywhere). Linked
/// from the top-level Admin page.
/// </summary>
public partial class AdminGenerationErrors : ComponentBase
{
    [Inject] private EngineApiClient Api { get; set; } = default!;
    [Inject] private AdminSessionService Session { get; set; } = default!;

    internal List<EngineApiClient.GenerationErrorRowDto> _rows = new();
    internal bool _loading;
    internal string? _error;

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
            var dto = await Api.GetAdminGenerationErrorsAsync();
            _rows = dto?.Rows ?? new List<EngineApiClient.GenerationErrorRowDto>();
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
