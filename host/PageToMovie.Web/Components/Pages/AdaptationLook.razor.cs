using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

/// <summary>Redirect shell — Look tools live on the screenplay card.</summary>
public partial class AdaptationLook
{
    [Inject] private NavigationManager Nav { get; set; } = null!;

    protected override void OnInitialized()
        => Nav.NavigateTo("adaptation/screenplay?tool=look", forceLoad: false, replace: true);
}
