using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: HomeCosts → Host.*
public partial class Home
{
    internal static string FormatUsd(double? amount) => HomeCosts.FormatUsd(amount);

    internal Task RefreshProjectCostAsync() => Costs.RefreshProjectCostAsync();

    internal Task LoadDemoShowcaseAsync() => Costs.LoadDemoShowcaseAsync();

}
