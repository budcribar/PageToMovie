using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class EmptyTableRow
{
    [Parameter] public int ColSpan { get; set; }
    [Parameter] public string Message { get; set; } = "";
    [Parameter] public string CssClass { get; set; } = "text-center py-3";
}
