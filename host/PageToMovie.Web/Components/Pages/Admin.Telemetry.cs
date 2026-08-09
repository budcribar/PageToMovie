using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: AdminTelemetry → Host.*
public partial class Admin
{
    internal Task RefreshGenerationErrorsAsync() => Telemetry.RefreshGenerationErrorsAsync();

    internal static string GetGenErrorTypeBadgeClass(string errorType) => AdminTelemetry.GetGenErrorTypeBadgeClass(errorType);

    internal Task SeedTimingDatabaseAsync() => Telemetry.SeedTimingDatabaseAsync();

    internal Task UpdateChartsAsync() => Telemetry.UpdateChartsAsync();

    internal string GetHitRatePolylinePoints() => Telemetry.GetHitRatePolylinePoints();

    internal string GetMaePolylinePoints() => Telemetry.GetMaePolylinePoints();

    internal static string FormatTrendTimestamp(string ts) => AdminTelemetry.FormatTrendTimestamp(ts);
}
