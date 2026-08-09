using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class AdminPageHeader
{
    [Parameter] public string Title { get; set; } = "";
    [Parameter] public RenderFragment? Subtitle { get; set; }
    [Parameter] public RenderFragment? Actions { get; set; }
}
