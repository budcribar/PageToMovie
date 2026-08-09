using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class AdminDataTable
{
    [Parameter] public bool Loading { get; set; }
    [Parameter] public bool IsEmpty { get; set; }
    [Parameter] public string EmptyMessage { get; set; } = "No rows yet.";
    [Parameter] public int EmptyColSpan { get; set; } = 1;
    [Parameter] public string TableClass { get; set; } = "table table-sm table-hover align-middle";
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? Toolbar { get; set; }
    [Parameter] public RenderFragment? Header { get; set; }
    [Parameter] public RenderFragment? Rows { get; set; }
    [Parameter] public string CssClass { get; set; } = "";
}
