using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components;

public partial class CostLegend
{
    /// <summary>A single legend row: label, dollar value, and swatch color.</summary>
    public sealed record Item(string Label, double Usd, string Color);

    [Parameter, EditorRequired] public IReadOnlyList<Item> Items { get; set; } = Array.Empty<Item>();
    [Parameter, EditorRequired] public double Total { get; set; }
}
