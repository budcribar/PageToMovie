using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using PageToMovie.Core.Models;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport : IPageSliceHost
{
    /// <summary>Slice host (see <see cref="IPageSliceHost"/>): the page-local sections are slices.</summary>
    public event Action? Rendered;

    public void RenderRequestedBySlice() => StateHasChanged();

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        Rendered?.Invoke();
    }

    public override string StepKey => "import";

    private ImportDrop? _drop;
    internal ImportDrop Drop => _drop ??= new ImportDrop(this);
    private ImportGate? _gate;
    internal ImportGate Gate => _gate ??= new ImportGate(this);
    private ImportRuntime? _runtime;
    internal ImportRuntime Runtime => _runtime ??= new ImportRuntime(this);

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
        if (!Session.IsLoggedIn)
        {
            Error = "Sign in required to import books.";
            Nav.NavigateTo("/login?returnUrl=/adaptation/import");
            return;
        }
        // Re-load status so PlanningModel reflects Settings saved just before navigating here.
        try { await LoadAsync(); } catch { /* base already tried */ }
        Runtime.SyncTargetEditFromStatus();
        Gate.RefreshImportGate();
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        Gate.RefreshImportGate();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await Drop.OnAfterRenderAsync(firstRender);
    }
}
