using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class CostPie
{
    /// <summary>A single ring segment: dollar weight and stroke color.</summary>
    public sealed record Segment(double Usd, string Color);

    [Parameter, EditorRequired] public IReadOnlyList<Segment> Segments { get; set; } = Array.Empty<Segment>();
    [Parameter, EditorRequired] public double Total { get; set; }
    [Parameter] public string CenterLabel { get; set; } = "";
    [Parameter] public string CenterValue { get; set; } = "";
    [Parameter] public string TestId { get; set; } = "";
    [Parameter] public int Size { get; set; } = 180;
    [Parameter] public string AriaLabel { get; set; } = "";
    [Parameter] public string EmptyText { get; set; } = "Nothing yet";
}
