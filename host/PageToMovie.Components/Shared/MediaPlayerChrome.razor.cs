using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class MediaPlayerChrome
{
    [Parameter] public string? Title { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public bool ShowClose { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public string CssClass { get; set; } = "card mb-3 border-secondary";
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? Footer { get; set; }
}
