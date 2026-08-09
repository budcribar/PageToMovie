using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class JobLogDetails
{
    [Parameter] public IReadOnlyList<string>? Lines { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public string Summary { get; set; } = "Details (admin)";
    [Parameter] public int MaxLines { get; set; } = 24;
    [Parameter] public string? TestId { get; set; }
}
