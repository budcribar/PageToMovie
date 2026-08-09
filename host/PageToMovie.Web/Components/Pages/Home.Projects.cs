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

// Forwarders: HomeProjects → Host.*
public partial class Home
{
    internal void OpenCollaboratorsModal(string projectId) => Projects.OpenCollaboratorsModal(projectId);

    internal void CloseCollaboratorsModal() => Projects.CloseCollaboratorsModal();

    internal void EnterFullStudio() => Projects.EnterFullStudio();

    internal void ExitFullStudio() => Projects.ExitFullStudio();

    internal Task PersistHomeModeAsync(string mode) => Projects.PersistHomeModeAsync(mode);

    internal Task ResolveHomeModeAsync() => Projects.ResolveHomeModeAsync();

    internal Task ToggleNewProjectAsync() => Projects.ToggleNewProjectAsync();

    internal Task OpenNewProjectAsync() => Projects.OpenNewProjectAsync();

    internal Task OnPickerChangedAsync(ChangeEventArgs e) => Projects.OnPickerChangedAsync(e);

    internal static string VisibilityBadgeClass(string? mode) => HomeProjects.VisibilityBadgeClass(mode);

    internal static string VisibilityBadgeText(string? mode) => HomeProjects.VisibilityBadgeText(mode);

    internal Task LoadAsync() => Projects.LoadAsync();

    internal void OnNewNameInput(ChangeEventArgs e) => Projects.OnNewNameInput(e);

    internal Task OnNewNameKeyDown(KeyboardEventArgs e) => Projects.OnNewNameKeyDown(e);

    internal bool CanManageProject(ProjectInfo? p) => Projects.CanManageProject(p);

    internal void BeginRenameAsync() => Projects.BeginRenameAsync();

    internal void OnRenameNameInput(ChangeEventArgs e) => Projects.OnRenameNameInput(e);

    internal void CancelRename() => Projects.CancelRename();

    internal Task ConfirmRenameAsync() => Projects.ConfirmRenameAsync();

    internal Task CreateProjectAsync() => Projects.CreateProjectAsync();

    internal Task SelectProjectAsync(string id) => Projects.SelectProjectAsync(id);

    internal Task OpenProjectAsync(string id) => Projects.OpenProjectAsync(id);

    internal Task BeginDeleteAsync(string id, string label) => Projects.BeginDeleteAsync(id, label);

    internal void OnDeleteConfirmInput(ChangeEventArgs e) => Projects.OnDeleteConfirmInput(e);

    internal void CancelDelete() => Projects.CancelDelete();

    internal Task ConfirmDeleteAsync() => Projects.ConfirmDeleteAsync();

    internal Task ChangeVisibilityAsync(string projectId, string? mode) => Projects.ChangeVisibilityAsync(projectId, mode);

    internal Task SyncOriginAsync(string projectId, string parentProjectId) => Projects.SyncOriginAsync(projectId, parentProjectId);

    internal Task SaveRevisionAsync(string projectId) => Projects.SaveRevisionAsync(projectId);

    internal Task OpenHistoryAsync(string historyUrl) => Projects.OpenHistoryAsync(historyUrl);

    internal static string FriendlyPushError(string raw) => HomeProjects.FriendlyPushError(raw);

    internal bool NeedsApiSetup => Projects.NeedsApiSetup;
    internal bool ShowEasyHome => Projects.ShowEasyHome;
    internal bool CanConfirmDelete => Projects.CanConfirmDelete;
}
