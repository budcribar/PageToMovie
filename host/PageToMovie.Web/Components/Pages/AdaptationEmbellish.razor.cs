using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

/// <summary>Redirect shell — Enrich tools live on the screenplay card.</summary>
public partial class AdaptationEmbellish
{
    [Inject] public required NavigationManager Nav { get; set; }

    protected override void OnInitialized()
        => Nav.NavigateTo("adaptation/screenplay?tool=enrich", forceLoad: false, replace: true);
}
