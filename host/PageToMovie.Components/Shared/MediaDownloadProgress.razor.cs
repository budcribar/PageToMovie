using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class MediaDownloadProgress
{
    [Parameter] public string Label { get; set; } = "";
    [Parameter] public bool ShowProgress { get; set; } = true;
    [Parameter] public string? CurrentFile { get; set; }
    [Parameter] public int Current { get; set; }
    [Parameter] public int Total { get; set; }
    [Parameter] public double Percent { get; set; }
    [Parameter] public string? FooterText { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }
}
