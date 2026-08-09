using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: AdminUi → Host.*
public partial class Admin
{
    internal void OpenTestEmailModal() => Ui.OpenTestEmailModal();

    internal void CloseTestEmailModal() => Ui.CloseTestEmailModal();

    internal void ToggleJobsAndLocks() => Ui.ToggleJobsAndLocks();

    internal void ToggleProjectArchiving() => Ui.ToggleProjectArchiving();

    internal void ToggleLoadSim() => Ui.ToggleLoadSim();

    internal void ToggleTimingTelemetry() => Ui.ToggleTimingTelemetry();

    internal void ToggleGenErrors() => Ui.ToggleGenErrors();

    internal void ToggleStorageAndCapacity() => Ui.ToggleStorageAndCapacity();

    internal void ExpandAllCards() => Ui.ExpandAllCards();

    internal void CollapseAllCards() => Ui.CollapseAllCards();

    internal void OnMediaFolderChanged() => Ui.OnMediaFolderChanged();

    internal Task LogoutAsync() => Ui.LogoutAsync();

    protected override Task OnAfterRenderAsync(bool firstRender) => Ui.OnAfterRenderAsync(firstRender);
}
