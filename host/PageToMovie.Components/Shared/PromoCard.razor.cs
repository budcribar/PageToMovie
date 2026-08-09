using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class PromoCard
{
    [Parameter] public string? TestId { get; set; }
    [Parameter] public string? Eyebrow { get; set; }
    [Parameter] public string? Title { get; set; }
    [Parameter] public RenderFragment? Body { get; set; }
    [Parameter] public RenderFragment? Cta { get; set; }
}
