using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin_TakesSection
{
    [Inject] private EngineApiClient Api { get; set; } = default!;
    [CascadingParameter] public Admin? Host { get; set; }

    internal bool _busy;
    internal bool _expanded;
    internal string? _error;
    private TakesTelemetryStats? _global;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private Task OnExpandedChanged(bool v)
    {
        _expanded = v;
        return Task.CompletedTask;
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var dto = await Api.GetAdminTakesTelemetryAsync();
            _global = dto?.Global;
            if (_global is null && dto is { Ok: false })
                _error = "Could not load takes telemetry.";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
