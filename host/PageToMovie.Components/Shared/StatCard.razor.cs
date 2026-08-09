using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class StatCard
{
    [Parameter, EditorRequired] public string? Label { get; set; }
    [Parameter, EditorRequired] public string? Value { get; set; }
    [Parameter] public RenderFragment? Sub { get; set; }
    [Parameter] public string CardClass { get; set; } = "card h-100";
    [Parameter] public string ValueClass { get; set; } = "fs-3 fw-semibold";
}
