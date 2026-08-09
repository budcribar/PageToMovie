using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: AdminState → Host.*
public partial class Admin
{
    internal Task ConnectHubAsync() => State.ConnectHubAsync();

    internal void OnAdminState(object? payload) => State.OnAdminState(payload);

    internal Task PollLoopAsync(CancellationToken ct) => State.PollLoopAsync(ct);

    internal Task RefreshAsync() => State.RefreshAsync();

    internal static string GetDiskProgressBarClass(double pct) => AdminState.GetDiskProgressBarClass(pct);

    internal static string FormatUptime(long sec) => AdminState.FormatUptime(sec);

    internal static string FormatAge(long? ms) => AdminState.FormatAge(ms);
}
