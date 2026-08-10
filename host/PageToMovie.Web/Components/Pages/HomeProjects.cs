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

public partial class Home
{
    /// <summary>Projects domain for the Home page. Owns related UI state and behavior.</summary>
    public sealed class HomeProjects
    {
        private readonly Home S;
        public HomeProjects(Home host) => S = host;

        internal string _collaboratorsProjectId = "";

        internal string _deleteConfirm = "";

        internal string? _deleteId;

        internal string _deleteLabel = "";

        /// <summary>When true, show project picker / active project; hide easy-start card.</summary>
            internal bool _fullStudioHome;

        /// <summary>Last successful push history URL per project id (session-local).</summary>
            internal readonly Dictionary<string, string> _historyUrls =
                new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Home defaults to a one-row project picker; full project management
            /// (visibility, collaborate, delete, fork/sync) is opt-in via "Manage".</summary>
            internal bool _manageExpanded;

        internal ElementReference _nameInputRef;

        internal string _newName = "";

        internal ProjectsDto? _projects;

        internal string _renameName = "";

        /// <summary>Last saved revision hash per project id (session-local).</summary>
            internal readonly Dictionary<string, string> _revisionHashes =
                new(StringComparer.OrdinalIgnoreCase);

        internal bool _showCollaboratorsModal;

        internal bool _showNew;

        internal bool _showRename;


        /// <summary>Show Setup keys only while personal BYOK studio keys are missing.</summary>
        internal bool NeedsApiSetup =>
            S.ActiveProject.Status is { XaiConfigured: false };


        /// <summary>Default home = easy start. Full studio hides easy cards and shows project picker.</summary>
        internal bool ShowEasyHome => !_fullStudioHome;


        internal void OpenCollaboratorsModal(string projectId)
        {
            _collaboratorsProjectId = projectId;
            _showCollaboratorsModal = true;
        }


        internal void CloseCollaboratorsModal() => _showCollaboratorsModal = false;


        internal void EnterFullStudio()
        {
            _fullStudioHome = true;
            _manageExpanded = false;
            _ = PersistHomeModeAsync("full");
        }


        internal void ExitFullStudio()
        {
            _fullStudioHome = false;
            _manageExpanded = false;
            _showNew = false;
            _showRename = false;
            _ = PersistHomeModeAsync("easy");
        }


        internal async Task PersistHomeModeAsync(string mode)
        {
            try { await S.Js.InvokeVoidAsync("localStorage.setItem", "ptm.homeMode", mode); }
            catch { /* non-fatal */ }
        }


        /// <summary>
        /// Prefer full studio when the user already has projects (or last chose full),
        /// so creating a project and returning home still shows it in the list.
        /// </summary>
        internal async Task ResolveHomeModeAsync()
        {
            string? mode = null;
            try { mode = await S.Js.InvokeAsync<string?>("localStorage.getItem", "ptm.homeMode"); }
            catch { /* ignore */ }

            if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            {
                _fullStudioHome = true;
                return;
            }
            if (string.Equals(mode, "easy", StringComparison.OrdinalIgnoreCase))
            {
                _fullStudioHome = false;
                return;
            }
            // Default: full studio if any projects exist so empty new projects stay visible.
            _fullStudioHome = _projects?.Projects is { Count: > 0 };
        }


        internal async Task ToggleNewProjectAsync()
        {
            _showNew = !_showNew;
            if (_showNew)
            {
                _newName = "";
                S.StateHasChanged();
                await Task.Yield();
                try { await _nameInputRef.FocusAsync(); } catch { }
            }
        }


        /// <summary>"+ New" opens a centered create-project modal (not buried under Manage).</summary>
        internal async Task OpenNewProjectAsync()
        {
            _fullStudioHome = true;
            await PersistHomeModeAsync("full");
            if (!_showNew)
                await ToggleNewProjectAsync();
            else
                S.StateHasChanged();
        }


        /// <summary>Compact picker &lt;select&gt; — switches the active project without navigating away.</summary>
        internal async Task OnPickerChangedAsync(ChangeEventArgs e)
        {
            var id = e.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(id))
                await SelectProjectAsync(id);
        }


        internal static string VisibilityBadgeClass(string? mode) => mode?.Trim().ToLowerInvariant() switch
        {
            "open" => "bg-success",
            "public" => "bg-info text-dark",
            _ => "bg-dark border border-secondary text-muted",
        };


        internal static string VisibilityBadgeText(string? mode) => mode?.Trim().ToLowerInvariant() switch
        {
            "open" => "🍴 Forkable",
            "public" => "👁️ Public",
            _ => "🔒 Private",
        };


        internal bool CanConfirmDelete => _deleteId is not null;


        internal async Task LoadAsync()
        {
            S._error = null;
            S._busy = true;
            try
            {
                try
                {
                    await S.Engine.EnsureHealthyAsync();
                    S._healthOk = true;
                }
                catch
                {
                    S._healthOk = false;
                    _projects = null;
                    return;
                }

                // Project inventory requires sign-in — do not hit /api/projects while anonymous
                // (avoids noisy 401 in the browser console on Home / login returnUrl=/).
                if (!S.Session.IsLoggedIn)
                {
                    _projects = new ProjectsDto { Ok = true, Projects = new List<ProjectInfo>() };
                    S.Jobs._job = null;
                    S.Jobs._myJobs = new List<JobSnapshot>();
                    S.Jobs.SyncJobsExpandedFromJob();
                    return;
                }

                _projects = await S.Engine.GetProjectsAsync();
                await S.ActiveProject.RefreshFromServerAsync(S.Engine);
                await S.Costs.RefreshProjectCostAsync();
                await ResolveHomeModeAsync();

                var jobs = await S.Engine.GetJobAsync();
                S.Jobs._job = jobs?.Job;
                try
                {
                    var mine = await S.Engine.GetJobsAsync(mine: true);
                    S.Jobs._myJobs = mine?.Jobs?
                        .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
                        .Take(12)
                        .ToList()
                        ?? new List<JobSnapshot>();
                }
                catch { S.Jobs._myJobs = new List<JobSnapshot>(); }
                S.Jobs.SyncJobsExpandedFromJob();
            }
            catch (Exception ex)
            {
                // Stale token / expired JWT → treat as signed out rather than a red wall of text
                var msg = ex.Message ?? "";
                if (msg.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("auth_required", StringComparison.OrdinalIgnoreCase))
                {
                    _projects = new ProjectsDto { Ok = true, Projects = new List<ProjectInfo>() };
                    S._error = null;
                }
                else
                {
                    S._error = msg;
                }
            }
            finally
            {
                S._busy = false;
            }
        }


        internal void OnNewNameInput(ChangeEventArgs e)
        {
            _newName = e.Value?.ToString() ?? "";
        }


        internal async Task OnNewNameKeyDown(KeyboardEventArgs e)
        {
            if (e.Key == "Enter" && !S._busy && !string.IsNullOrWhiteSpace(_newName))
                await CreateProjectAsync();
        }



        /// <summary>
        /// Whether the current user may manage (rename/settings) a project. Mirrors the server's
        /// ownership rule (ProjectOwnership.IsOwnedBy) rather than a strict OwnerUserId == UserId match:
        /// a real admin can manage anything, otherwise ownership is by the "username/slug" folder-owner
        /// segment OR the OwnerUserId field. This keeps the button in step with the project LIST (which
        /// is already scoped to owned projects) so a project you own with an empty OwnerUserId field
        /// still shows its rename affordance.
        /// </summary>
        internal bool CanManageProject(ProjectInfo? p)
        {
            if (p is null) return false;
            if (S.Session.IsAdmin) return true;
            var uid = (S.Session.UserId ?? "").Trim();
            if (uid.Length == 0) return false;
            if (!string.IsNullOrWhiteSpace(p.OwnerUserId) &&
                string.Equals(p.OwnerUserId.Trim(), uid, StringComparison.OrdinalIgnoreCase))
                return true;
            var pid = (p.Id ?? "").Replace('\\', '/').Trim('/');
            var slash = pid.IndexOf('/');
            return slash > 0 && string.Equals(pid[..slash], uid, StringComparison.OrdinalIgnoreCase);
        }


        internal void BeginRenameAsync()
        {
            _showRename = true;
            _manageExpanded = true;
            _showNew = false;
            _renameName = S.ActiveProject.Label
                ?? _projects?.Active?.Label
                ?? _projects?.Active?.Title
                ?? S.ActiveProject.ProjectId
                ?? "";
        }


        internal void OnRenameNameInput(ChangeEventArgs e) => _renameName = e.Value?.ToString() ?? "";


        internal void CancelRename()
        {
            _showRename = false;
            _renameName = "";
        }


        internal async Task ConfirmRenameAsync()
        {
            var id = _projects?.Active?.Id ?? S.ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(_renameName)) return;
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                var updated = await S.Engine.RenameProjectAsync(id, _renameName.Trim());
                _showRename = false;
                S._message = $"Renamed to “{updated?.Label ?? _renameName.Trim()}”.";
                await LoadAsync();
                if (S.ActiveProject.HasProject)
                    await S.ActiveProject.RefreshFromServerAsync(S.Engine);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }


        internal async Task CreateProjectAsync()
        {
            if (string.IsNullOrWhiteSpace(_newName)) return;
            if (!S.Session.IsLoggedIn)
            {
                S._error = "Sign in required to create a project.";
                S.Nav.NavigateTo("/login?returnUrl=/");
                return;
            }
            S._busy = true;
            S._error = null;
            try
            {
                var name = _newName.Trim();
                var result = await S.Engine.CreateProjectAsync(name);
                // Prefer filtered GET list (create response may include unowned projects for admins).
                _projects = await S.Engine.GetProjectsAsync() ?? result;
                var created = _projects?.Active
                    ?? _projects?.Projects?.FirstOrDefault(p =>
                        string.Equals(p.Title, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Label, name, StringComparison.OrdinalIgnoreCase))
                    ?? result?.Active;
                if (created?.Id is { Length: > 0 } cid)
                {
                    S.ActiveProject.Set(cid, created.Label ?? created.Title ?? cid, created.ParentProjectId, created.StudioPath);
                    await S.ActiveProject.RefreshReadinessAsync(S.Engine);
                    await S.Costs.RefreshProjectCostAsync();
                }
                else
                    await S.ActiveProject.RefreshFromServerAsync(S.Engine);

                // Stay in full studio so going Home still shows the new project in the picker.
                _fullStudioHome = true;
                await PersistHomeModeAsync("full");
                S._message = $"Created “{name}” — ready to import a book.";
                _newName = "";
                _showNew = false;
                // Go to import (book) rather than a generic adaptation shell so the project is clearly "started".
                S.Nav.NavigateTo("adaptation/import");
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task SelectProjectAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (string.Equals(id, _projects?.Active?.Id, StringComparison.OrdinalIgnoreCase))
                return;

            S._busy = true;
            S._error = null;
            S._message = null;
            _deleteId = null;
            _deleteConfirm = "";
            try
            {
                _projects = await S.Engine.GetProjectsAsync();
                var a = _projects?.Active ?? _projects?.Projects.FirstOrDefault(p =>
                    string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                await S.ActiveProject.SelectAsync(S.Engine, id, a?.Label ?? a?.Title ?? id, a?.ParentProjectId, a?.StudioPath);
                await S.Costs.RefreshProjectCostAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task OpenProjectAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            await SelectProjectAsync(id);
            S.Nav.NavigateTo("adaptation");
        }


        internal Task BeginDeleteAsync(string id, string label)
        {
            _deleteId = id;
            _deleteLabel = label;
            _deleteConfirm = "";
            S._error = null;
            S._message = null;
            return Task.CompletedTask;
        }


        internal void OnDeleteConfirmInput(ChangeEventArgs e) =>
            _deleteConfirm = e.Value?.ToString() ?? "";


        internal void CancelDelete()
        {
            _deleteId = null;
            _deleteLabel = "";
            _deleteConfirm = "";
        }


        internal async Task ConfirmDeleteAsync()
        {
            if (_deleteId is null || !CanConfirmDelete) return;
            var id = _deleteId;
            S._busy = true;
            S._error = null;
            try
            {
                var result = await S.Engine.DeleteProjectAsync(id);
                _projects = result ?? await S.Engine.GetProjectsAsync();
                await S.ActiveProject.RefreshFromServerAsync(S.Engine);
                S._message = $"Deleted “{_deleteLabel}”";
                CancelDelete();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task ChangeVisibilityAsync(string projectId, string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return;
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.SetProjectVisibilityModeAsync(projectId, mode);
                await LoadAsync();
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }


        internal async Task SyncOriginAsync(string projectId, string parentProjectId)
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                var res = await S.Engine.SyncOriginAsync(projectId, parentProjectId);
                if (res is { Ok: true } || res is { Success: true })
                {
                    if (res.HasConflicts)
                    {
                        S._error = $"Sync from origin returned conflicts: {res.Message}";
                    }
                    else
                    {
                        S._message = $"Successfully synced from parent project {parentProjectId}!";
                    }
                }
                else
                {
                    S._error = res?.Error ?? res?.Message ?? "Sync from origin failed";
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }


        internal async Task SaveRevisionAsync(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId)) return;
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                var res = await S.Engine.PushProjectAsync(projectId, commitFirst: true, message: "Project update");
                if (res is null || res.Ok == false)
                {
                    S._error = FriendlyPushError(res?.Error ?? res?.Message ?? "Could not save revision.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(res.CommitHash))
                    _revisionHashes[projectId] = res.CommitHash!;
                if (!string.IsNullOrWhiteSpace(res.HistoryUrl))
                    _historyUrls[projectId] = res.HistoryUrl!;

                var shortHash = Home.ShortHash(res.CommitHash);
                S._message = string.IsNullOrEmpty(shortHash)
                    ? "Revision saved."
                    : $"Revision saved ({shortHash}).";
                await S.Jobs.RefreshPackageStatusAsync();
            }
            catch (Exception ex)
            {
                S._error = FriendlyPushError(ex.Message);
            }
            finally
            {
                S._busy = false;
            }
        }


        internal async Task OpenHistoryAsync(string historyUrl)
        {
            if (string.IsNullOrWhiteSpace(historyUrl)) return;
            try
            {
                await S.Js.InvokeVoidAsync("open", historyUrl, "_blank", "noopener,noreferrer");
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
        }


        /// <summary>Operator-facing rewrite — avoid config key dumps on Home.</summary>
        internal static string FriendlyPushError(string raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) return "Could not save revision.";
            if (s.Contains("not enabled", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("ProjectsRepoUrl", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Git:Token", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("must be configured", StringComparison.OrdinalIgnoreCase))
                return "Package backup is not set up on this server yet.";
            if (s.Contains("Nothing to push", StringComparison.OrdinalIgnoreCase))
                return "Nothing new to save yet.";
            // LibGit2Sharp / GitHub auth failures
            if (s.Contains("403", StringComparison.Ordinal) ||
                s.Contains("401", StringComparison.Ordinal) ||
                s.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("access denied", StringComparison.OrdinalIgnoreCase))
                return "Backup remote refused the push (403). Check the GitHub token has Contents: Write on PageToMovie/Projects, the org allows fine-grained tokens, and the token is authorized for that org/repo.";
            // First line only
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[..nl].Trim();
            return s.Length > 200 ? s[..200] + "…" : s;
        }

    }
}
