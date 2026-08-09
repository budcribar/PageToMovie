using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class CenteredSpinner
{
    [Parameter] public string WrapperClass { get; set; } = "text-center py-5";
    [Parameter] public string SpinnerClass { get; set; } = "spinner-border text-primary";
    [Parameter] public string HiddenText { get; set; } = "Loading…";
    [Parameter] public RenderFragment? Caption { get; set; }
    [Parameter] public string CaptionClass { get; set; } = "text-muted mb-0";
}
