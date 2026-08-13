using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

/// <summary>Redirect shell — Fit length tools live on the screenplay card.</summary>
public partial class AdaptationTrim
{
    [Inject] private NavigationManager Nav { get; set; } = null;

    protected override void OnInitialized()
        => Nav.NavigateTo("adaptation/screenplay?tool=fit", forceLoad: false, replace: true);
}
