using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Home_Landing
{
    [Parameter] public bool IsLoggedIn { get; set; }

    /// <summary>When false, drop the “with your voice” Easy Start pitch from the lede.</summary>
    [Parameter] public bool ShowEasyStart { get; set; }
}
