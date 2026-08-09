using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Home
{
    /// <summary>Show Setup keys only while personal BYOK studio keys are missing.</summary>
    internal bool NeedsApiSetup =>
        ActiveProject.Status is { XaiConfigured: false };

    /// <summary>Default home = easy start. Full studio hides easy cards and shows project picker.</summary>
    internal bool ShowEasyHome => !_fullStudioHome;

    /// <summary>Only the real current step is highlighted. Setup (0) until keys exist, then pipeline.</summary>
    internal string HomeActiveStep
    {
        get
        {
            if (ActiveProject.Status is { XaiConfigured: false })
                return "setup";
            if (!ActiveProject.HasProject) return "book";
            if (!ActiveProject.CanCharacters) return "book";
            if (!ActiveProject.CanEstimate) return "cast";
            if (!ActiveProject.CanScenes) return "estimate";
            return "film";
        }
    }

    internal bool? _healthOk;
    internal bool _busy;
    internal string? _error;
    internal string? _message;
    internal ProjectsDto? _projects;
    internal bool _showCollaboratorsModal;
    internal string _collaboratorsProjectId = "";
    /// <summary>Last successful push history URL per project id (session-local).</summary>
    internal readonly Dictionary<string, string> _historyUrls =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last saved revision hash per project id (session-local).</summary>
    internal readonly Dictionary<string, string> _revisionHashes =
        new(StringComparer.OrdinalIgnoreCase);
    internal UncommittedStatusDto? _packageStatus;
    internal bool _packageStatusLoading;

    internal void OpenCollaboratorsModal(string projectId)
    {
        _collaboratorsProjectId = projectId;
        _showCollaboratorsModal = true;
    }

    internal void CloseCollaboratorsModal() => _showCollaboratorsModal = false;

    internal static string ShortHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "";
        var h = hash.Trim();
        return h.Length <= 7 ? h : h[..7];
    }

    internal string ActivePackageProjectId =>
        ActiveProject.ProjectId ?? _projects?.Active?.Id ?? "";

    internal string? PackageHistoryUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_packageStatus?.HistoryUrl))
                return _packageStatus!.HistoryUrl;
            var pid = ActivePackageProjectId;
            if (string.IsNullOrWhiteSpace(pid)) return null;
            return _historyUrls.TryGetValue(pid, out var hu) ? hu : null;
        }
    }

    internal bool CanSavePackageRevision
    {
        get
        {
            var pid = ActivePackageProjectId;
            if (string.IsNullOrWhiteSpace(pid) || !Session.IsLoggedIn) return false;
            if (Session.IsAdmin) return true;
            var owner = _projects?.Active?.OwnerUserId
                        ?? _projects?.Projects.FirstOrDefault(x =>
                            string.Equals(x.Id, pid, StringComparison.OrdinalIgnoreCase))?.OwnerUserId;
            return string.Equals(Session.UserId, owner, StringComparison.OrdinalIgnoreCase);
        }
    }
    internal JobSnapshot? _job;
    internal List<JobSnapshot> _myJobs = new();
    internal bool _showNew;
    internal bool _showRename;
    internal bool _showImport;
    internal bool _importing;
    internal bool _backingUp;
    internal string _importName = "";
    internal IBrowserFile? _importFile;
    internal bool _showCheckpoints;
    internal bool _checkpointBusy;
    internal string _checkpointName = "";
    internal List<CheckpointDto> _checkpoints = new();
    internal string _renameName = "";
    /// <summary>Home defaults to a one-row project picker; full project management
    /// (visibility, collaborate, delete, fork/sync) is opt-in via "Manage".</summary>
    internal bool _manageExpanded;
    /// <summary>When true, show project picker / active project; hide easy-start card.</summary>
    internal bool _fullStudioHome;

    internal string _newName = "";
    internal ElementReference _nameInputRef;
    internal string? _deleteId;
    internal string _deleteLabel = "";
    internal string _deleteConfirm = "";
    /// <summary>My jobs panel; collapsed by default, auto-opens when a job is running/queued.</summary>
    internal bool _jobsExpanded;
    /// <summary>Null = follow auto rules; otherwise honor last user click.</summary>
    internal bool? _jobsUserPreference;
    /// <summary>Titles of real public demos (never invented marketing names).</summary>
    internal string _demoShowcaseHint = "Loading gallery…";
    internal List<DemoListItem> _publicDemos = new();

    /// <summary>Planning estimate for the full film (draft path) from cost report.</summary>
    internal double? _costEstimateUsd;
    /// <summary>Ledger actual spend for this project (catalog list rates).</summary>
    internal double? _costActualUsd;
    internal string? _costResolution;
    internal double? _costVideoRate;
    internal bool _costLoading;
    /// <summary>True when no generation model is chosen yet, so an estimate can't be produced.</summary>
    internal bool _costNeedsModels;

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
        try { await Js.InvokeVoidAsync("localStorage.setItem", "ptm.homeMode", mode); }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Prefer full studio when the user already has projects (or last chose full),
    /// so creating a project and returning home still shows it in the list.
    /// </summary>
    internal async Task ResolveHomeModeAsync()
    {
        string? mode = null;
        try { mode = await Js.InvokeAsync<string?>("localStorage.getItem", "ptm.homeMode"); }
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
            StateHasChanged();
            await Task.Yield();
            try { await _nameInputRef.FocusAsync(); } catch { }
        }
    }

    /// <summary>"+ New" in the compact bar: opens Manage and the create-project form together.</summary>
    internal async Task OpenNewProjectAsync()
    {
        _fullStudioHome = true;
        await PersistHomeModeAsync("full");
        _manageExpanded = true;
        if (!_showNew)
            await ToggleNewProjectAsync();
        else
            StateHasChanged();
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

    internal static string FormatUsd(double? amount)
    {
        if (amount is null) return "—";
        return amount.Value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    }

    internal static string FormatRelativeUtc(DateTime utc)
    {
        var t = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : utc.ToUniversalTime();
        var span = DateTime.UtcNow - t;
        if (span.TotalSeconds < 45) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 36) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 14) return $"{(int)span.TotalDays}d ago";
        return t.ToString("MMM d");
    }

    internal async Task RefreshPackageStatusAsync()
    {
        var pid = ActiveProject.ProjectId ?? _projects?.Active?.Id;
        if (string.IsNullOrWhiteSpace(pid) || !Session.IsLoggedIn)
        {
            _packageStatus = null;
            _packageStatusLoading = false;
            return;
        }

        _packageStatusLoading = true;
        try
        {
            var env = await Engine.GetProjectUncommittedStatusAsync(pid);
            _packageStatus = env?.Status;
            if (!string.IsNullOrWhiteSpace(_packageStatus?.LastCommitHash))
                _revisionHashes[pid] = _packageStatus!.LastCommitHash!;
            if (!string.IsNullOrWhiteSpace(_packageStatus?.HistoryUrl))
                _historyUrls[pid] = _packageStatus!.HistoryUrl!;
        }
        catch
        {
            // non-fatal
        }
        finally
        {
            _packageStatusLoading = false;
        }
    }

    internal async Task RefreshProjectCostAsync()
    {
        var pid = ActiveProject.ProjectId ?? _projects?.Active?.Id;
        if (string.IsNullOrWhiteSpace(pid) || !Session.IsLoggedIn)
        {
            _costEstimateUsd = null;
            _costActualUsd = null;
            _costResolution = null;
            _costVideoRate = null;
            _costLoading = false;
            _packageStatus = null;
            return;
        }

        _costLoading = true;
        try
        {
            // Match film resolution so Home estimate tracks 480p / 720p / 1080p rates.
            string? draftRes = null;
            var hasModel = true; // assume yes unless the config clearly says no model is chosen
            try
            {
                var cfg = await Engine.GetConfigAsync(pid);
                if (cfg?.Config is not null)
                {
                    hasModel = cfg.Config.TryGetValue("model_name", out var mnEl)
                        && mnEl.ValueKind == System.Text.Json.JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(mnEl.GetString());
                    if (cfg.Config.TryGetValue("resolution", out var resEl) &&
                        resEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        draftRes = resEl.GetString();
                }
            }
            catch { /* config unavailable → assume a model may exist and let the fetch decide */ }

            if (!hasModel)
            {
                // No generation model chosen yet (set on the Configuration page). The estimate fails
                // fast by design — skip the call entirely so it never errors, and show a setup hint.
                _costNeedsModels = true;
                _costEstimateUsd = null;
                _costActualUsd = null;
                _costResolution = null;
                _costVideoRate = null;
                return;
            }
            _costNeedsModels = false;

            var dto = await Engine.GetCostAsync(pid, draftResolution: draftRes);
            var s = dto?.Cost?.Summary;
            _costEstimateUsd = s?.FullFilmAllDraftUsd;
            _costActualUsd = s?.ActualUsd;
            _costResolution = dto?.Cost?.DraftResolution ?? draftRes;
            _costVideoRate = dto?.Cost?.OutputRateDraft;
        }
        catch
        {
            // Keep prior numbers if refresh fails mid-session
        }
        finally
        {
            _costLoading = false;
        }

        await RefreshPackageStatusAsync();
    }

    internal bool CanConfirmDelete => _deleteId is not null;

    internal bool HasActiveJob =>
        _job is not null &&
        (string.Equals(_job.Status, "running", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(_job.Status, "queued", StringComparison.OrdinalIgnoreCase));

    internal void ToggleJobsExpanded()
    {
        _jobsExpanded = !_jobsExpanded;
        _jobsUserPreference = _jobsExpanded;
    }

    internal void SyncJobsExpandedFromJob()
    {
        if (_jobsUserPreference is bool prefer)
            _jobsExpanded = prefer;
        else
            _jobsExpanded = HasActiveJob; // collapsed when idle, open when work is running
    }

    protected override async Task OnInitializedAsync()
    {
        L.CultureChanged += OnCultureChanged;
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
        await LoadAsync();
        await LoadDemoShowcaseAsync();
        try
        {
            await Hub.StartAsync();
        }
        catch
        {
            // SignalR optional for browse
        }
    }

    internal void OnJobUpdated(JobSnapshot snap)
    {
        _job = snap;
        SyncJobsExpandedFromJob();
        _ = InvokeAsync(async () =>
        {
            try
            {
                var mine = await Engine.GetJobsAsync(mine: true);
                _myJobs = mine?.Jobs?
                    .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
                    .Take(12)
                    .ToList()
                    ?? new List<JobSnapshot>();
            }
            catch { /* ignore */ }
            SyncJobsExpandedFromJob();
            // Jobs write cost_ledger on completion — keep home spend fresh.
            if (snap.Status is "done" or "error" or "cancelled")
            {
                try { await RefreshProjectCostAsync(); }
                catch { /* ignore */ }
            }
            StateHasChanged();
        });
    }

    internal void OnJobLog(string line)
    {
        if (_job is not null)
        {
            _job.Message = line;
            // Keep rolling admin log so video API prompts appear live on Home
            _job.Log ??= new List<string>();
            _job.Log.Add(line);
            if (_job.Log.Count > 2000)
                _job.Log.RemoveRange(0, _job.Log.Count - 1500);
        }
        _ = InvokeAsync(StateHasChanged);
    }

    internal static bool JobLogHasVideoPayload(JobSnapshot j) =>
        j.Log.Any(l =>
            l.Contains("PROMPT BEGIN", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("[Grok] Submit", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("video-extend", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("reference-to-video", StringComparison.OrdinalIgnoreCase));


    /// <summary>Fill the home gallery blurb from real public demos only.</summary>
    internal async Task LoadDemoShowcaseAsync()
    {
        try
        {
            // ListDemos triggers throttled channel sync — cards are real YouTube uploads only.
            var all = await Engine.ListDemosAsync(12, "new");
            _publicDemos = all
                .Where(d => !string.IsNullOrWhiteSpace(d.YoutubeId) && !string.IsNullOrWhiteSpace(d.Title))
                .Take(4)
                .ToList();
            if (_publicDemos.Count == 0)
            {
                _demoShowcaseHint = "No films on the Page to Movie channel yet";
            }
            else
            {
                var titles = _publicDemos
                    .Select(d => (d.Title ?? d.ProjectId ?? "").Trim())
                    .Where(s => s.Length > 0)
                    .Take(4)
                    .ToList();
                if (titles.Count == 0)
                    _demoShowcaseHint = $"{_publicDemos.Count} film{(_publicDemos.Count == 1 ? "" : "s")} on the wall";
                else if (_publicDemos.Count > titles.Count)
                    _demoShowcaseHint = string.Join(", ", titles) + $" · +{_publicDemos.Count - titles.Count} more";
                else
                    _demoShowcaseHint = string.Join(", ", titles);
            }
        }
        catch
        {
            _demoShowcaseHint = "Community films made with PageToMovie";
        }
    }

    internal async Task LoadAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            try
            {
                await Engine.EnsureHealthyAsync();
                _healthOk = true;
            }
            catch
            {
                _healthOk = false;
                _projects = null;
                return;
            }

            // Project inventory requires sign-in — do not hit /api/projects while anonymous
            // (avoids noisy 401 in the browser console on Home / login returnUrl=/).
            if (!Session.IsLoggedIn)
            {
                _projects = new ProjectsDto { Ok = true, Projects = new List<ProjectInfo>() };
                _job = null;
                _myJobs = new List<JobSnapshot>();
                SyncJobsExpandedFromJob();
                return;
            }

            _projects = await Engine.GetProjectsAsync();
            await ActiveProject.RefreshFromServerAsync(Engine);
            await RefreshProjectCostAsync();
            await ResolveHomeModeAsync();

            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
            try
            {
                var mine = await Engine.GetJobsAsync(mine: true);
                _myJobs = mine?.Jobs?
                    .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
                    .Take(12)
                    .ToList()
                    ?? new List<JobSnapshot>();
            }
            catch { _myJobs = new List<JobSnapshot>(); }
            SyncJobsExpandedFromJob();
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
                _error = null;
            }
            else
            {
                _error = msg;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    internal void OnNewNameInput(ChangeEventArgs e)
    {
        _newName = e.Value?.ToString() ?? "";
    }

    internal async Task OnNewNameKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !_busy && !string.IsNullOrWhiteSpace(_newName))
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
        if (Session.IsAdmin) return true;
        var uid = (Session.UserId ?? "").Trim();
        if (uid.Length == 0) return false;
        if (!string.IsNullOrWhiteSpace(p.OwnerUserId) &&
            string.Equals(p.OwnerUserId.Trim(), uid, StringComparison.OrdinalIgnoreCase))
            return true;
        var pid = (p.Id ?? "").Replace('\\', '/').Trim('/');
        var slash = pid.IndexOf('/');
        return slash > 0 && string.Equals(pid[..slash], uid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full project backup: user-mode server export → the browser merges in local media-folder bytes
    /// (video/audio the server offloaded) → download one complete zip. Falls back to a server-only zip
    /// when the media folder isn't connected or the client merge is unavailable.
    /// </summary>
    internal async Task BackupProjectAsync()
    {
        var id = _projects?.Active?.Id ?? ActiveProject.ProjectId;
        if (string.IsNullOrWhiteSpace(id)) return;
        _backingUp = true;
        _busy = true;
        _error = null;
        try
        {
            // Offloaded video/audio lives in the browser media folder. If it isn't connected, prompt
            // to connect it now (the folder picker needs THIS click's user activation, so do it before
            // any network await), then sync so the folder is complete before the merge.
            if (!MediaFolder.IsConnected)
            {
                _message = "Connect your media folder to include all video in the backup…";
                var connected = await MediaFolder.ConnectFolderAsync();
                if (connected)
                {
                    _message = "Syncing media into the folder…";
                    StateHasChanged();
                    try { await MediaFolder.SyncProjectMediaToClientAsync(id); }
                    catch { /* best effort — merge takes whatever is present */ }
                }
                else
                {
                    _message = "Media folder not connected — backing up project files only.";
                    StateHasChanged();
                }
            }

            _message = "Building backup (project files + all media)…";
            StateHasChanged();
            var (resp, fileName) = await Engine.ExportProjectZipAsUserAsync(id);
            using (resp)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var streamRef = new Microsoft.JSInterop.DotNetStreamReference(stream);
                var result = await Js.InvokeAsync<System.Text.Json.JsonElement>(
                    "PageToMovieExport.mergeServerZipWithLocalMediaAsync", fileName, streamRef, id);
                if (result.TryGetProperty("success", out var ok) && ok.GetBoolean())
                {
                    _message = result.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                        ? m.GetString()
                        : $"Backed up {fileName}";
                }
                else
                {
                    var err = result.TryGetProperty("error", out var e) ? e.GetString() : "media merge failed";
                    _message = "Media merge unavailable — downloading project files only…";
                    StateHasChanged();
                    var (resp2, fileName2) = await Engine.ExportProjectZipAsUserAsync(id);
                    using (resp2)
                    {
                        await using var stream2 = await resp2.Content.ReadAsStreamAsync();
                        using var streamRef2 = new Microsoft.JSInterop.DotNetStreamReference(stream2);
                        var plain = await Js.InvokeAsync<System.Text.Json.JsonElement>(
                            "PageToMovieExport.downloadStreamAsync", fileName2, streamRef2);
                        if (plain.TryGetProperty("success", out var ok2) && ok2.GetBoolean())
                            _message = $"Backed up {fileName2} (project files only — connect your media folder to include video). Note: {err}";
                        else
                        {
                            _error = err;
                            _message = null;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _message = null;
        }
        finally
        {
            _backingUp = false;
            _busy = false;
        }
    }
    internal async Task ToggleCheckpointsAsync()
    {
        _showCheckpoints = !_showCheckpoints;
        _error = null;
        _message = null;
        if (_showCheckpoints)
        {
            _showRename = false;
            _showImport = false;
            await LoadCheckpointsAsync();
        }
    }

    internal async Task LoadCheckpointsAsync()
    {
        var id = _projects?.Active?.Id ?? ActiveProject.ProjectId;
        if (string.IsNullOrWhiteSpace(id)) return;
        _checkpoints = await Engine.ListCheckpointsAsync(id);
        StateHasChanged();
    }

    internal async Task CreateCheckpointAsync()
    {
        var id = _projects?.Active?.Id ?? ActiveProject.ProjectId;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(_checkpointName)) return;
        _checkpointBusy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.CreateCheckpointAsync(id, _checkpointName.Trim());
            if (res.Ok)
            {
                _message = $"Checkpoint “{_checkpointName.Trim()}” saved.";
                _checkpointName = "";
                await LoadCheckpointsAsync();
            }
            else
            {
                _error = res.Error ?? "Could not save checkpoint.";
            }
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _checkpointBusy = false; }
    }

    internal async Task RevertCheckpointAsync(CheckpointDto cp)
    {
        var id = _projects?.Active?.Id ?? ActiveProject.ProjectId;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cp.CommitHash)) return;
        _checkpointBusy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.RevertToCheckpointAsync(id, cp.CommitHash);
            if (res.Ok)
            {
                _message = res.Message ?? "Rolled back to the checkpoint (your clips are unchanged).";
                await LoadCheckpointsAsync();
                await LoadAsync();
                if (ActiveProject.HasProject)
                    await ActiveProject.RefreshFromServerAsync(Engine);
            }
            else
            {
                _error = res.Error ?? "Could not roll back.";
            }
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _checkpointBusy = false; }
    }

    internal void ToggleImport()
    {
        _showImport = !_showImport;
        if (_showImport)
        {
            _showNew = false;
            _showRename = false;
        }
        _importFile = null;
        _importName = "";
        _error = null;
        _message = null;
    }

    /// <summary>Stage 1 of import: a file was picked. Hold it and pre-fill an editable default name,
    /// then reveal the Import button — the actual import waits for the user to confirm.</summary>
    internal void OnImportFileSelected(InputFileChangeEventArgs e)
    {
        _importFile = e.File;
        _error = null;
        _message = null;
        if (_importFile is not null)
            _importName = DefaultNameFromFileName(_importFile.Name);
        StateHasChanged();
    }

    /// <summary>Friendly default project name from an export file name — strips the .zip extension,
    /// any owner/slug prefix, and the "PageToMovie_…_export"/timestamp decorations.</summary>
    internal static string DefaultNameFromFileName(string? fileName)
    {
        var name = (fileName ?? "").Trim();
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        name = name.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
        if (name.StartsWith("PageToMovie_", StringComparison.OrdinalIgnoreCase))
            name = name["PageToMovie_".Length..];
        name = System.Text.RegularExpressions.Regex.Replace(name, @"_\d{8}_\d{6}$", "");
        if (name.EndsWith("_export", StringComparison.OrdinalIgnoreCase))
            name = name[..^"_export".Length];
        name = name.Trim(' ', '_', '-');
        return name.Length > 80 ? name[..80] : name;
    }

    /// <summary>Stage 2 of import: user confirmed. Import the held file under the (editable) name.</summary>
    internal async Task HandleImportAsync()
    {
        var file = _importFile;
        if (file is null) return;
        _importing = true;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            // Match the server/Kestrel multipart cap (512 MB) — project zips carry video.
            const long maxBytes = 512L * 1024 * 1024;
            await using var stream = file.OpenReadStream(maxBytes);
            var res = await Engine.ImportProjectZipAsUserAsync(
                stream, file.Name, name: string.IsNullOrWhiteSpace(_importName) ? null : _importName.Trim());
            _showImport = false;
            _importFile = null;
            _importName = "";
            _message = res?.Message ?? $"Imported “{res?.ProjectId}”.";
            await LoadAsync();
            if (ActiveProject.HasProject)
                await ActiveProject.RefreshFromServerAsync(Engine);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _importing = false;
            _busy = false;
        }
    }

    internal void BeginRenameAsync()
    {
        _showRename = true;
        _manageExpanded = true;
        _showNew = false;
        _renameName = ActiveProject.Label
            ?? _projects?.Active?.Label
            ?? _projects?.Active?.Title
            ?? ActiveProject.ProjectId
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
        var id = _projects?.Active?.Id ?? ActiveProject.ProjectId;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(_renameName)) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var updated = await Engine.RenameProjectAsync(id, _renameName.Trim());
            _showRename = false;
            _message = $"Renamed to “{updated?.Label ?? _renameName.Trim()}”.";
            await LoadAsync();
            if (ActiveProject.HasProject)
                await ActiveProject.RefreshFromServerAsync(Engine);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task CreateProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(_newName)) return;
        if (!Session.IsLoggedIn)
        {
            _error = "Sign in required to create a project.";
            Nav.NavigateTo("/login?returnUrl=/");
            return;
        }
        _busy = true;
        _error = null;
        try
        {
            var name = _newName.Trim();
            var result = await Engine.CreateProjectAsync(name);
            // Prefer filtered GET list (create response may include unowned projects for admins).
            _projects = await Engine.GetProjectsAsync() ?? result;
            var created = _projects?.Active
                ?? _projects?.Projects?.FirstOrDefault(p =>
                    string.Equals(p.Title, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Label, name, StringComparison.OrdinalIgnoreCase))
                ?? result?.Active;
            if (created?.Id is { Length: > 0 } cid)
            {
                ActiveProject.Set(cid, created.Label ?? created.Title ?? cid, created.ParentProjectId, created.StudioPath);
                await ActiveProject.RefreshReadinessAsync(Engine);
                await RefreshProjectCostAsync();
            }
            else
                await ActiveProject.RefreshFromServerAsync(Engine);

            // Stay in full studio so going Home still shows the new project in the picker.
            _fullStudioHome = true;
            await PersistHomeModeAsync("full");
            _message = $"Created “{name}” — ready to import a book.";
            _newName = "";
            _showNew = false;
            // Go to import (book) rather than a generic adaptation shell so the project is clearly "started".
            Nav.NavigateTo("adaptation/import");
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task SelectProjectAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (string.Equals(id, _projects?.Active?.Id, StringComparison.OrdinalIgnoreCase))
            return;

        _busy = true;
        _error = null;
        _message = null;
        _deleteId = null;
        _deleteConfirm = "";
        try
        {
            _projects = await Engine.GetProjectsAsync();
            var a = _projects?.Active ?? _projects?.Projects.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            await ActiveProject.SelectAsync(Engine, id, a?.Label ?? a?.Title ?? id, a?.ParentProjectId, a?.StudioPath);
            await RefreshProjectCostAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task OpenProjectAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        await SelectProjectAsync(id);
        Nav.NavigateTo("adaptation");
    }

    internal Task BeginDeleteAsync(string id, string label)
    {
        _deleteId = id;
        _deleteLabel = label;
        _deleteConfirm = "";
        _error = null;
        _message = null;
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
        _busy = true;
        _error = null;
        try
        {
            var result = await Engine.DeleteProjectAsync(id);
            _projects = result ?? await Engine.GetProjectsAsync();
            await ActiveProject.RefreshFromServerAsync(Engine);
            _message = $"Deleted “{_deleteLabel}”";
            CancelDelete();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task CancelAsync()
    {
        _busy = true;
        try
        {
            await Engine.CancelJobAsync();
            _message = "Cancel requested";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal static string FriendlyStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "…" : status!;

    private static string FriendlyKind(string? kind) => kind switch
    {
        "character" => "Portrait",
        "character-plates" => "Book pictures",
        "stage1" => "Screenplay",
        "stage2" => "Shot plan",
        "video" or "clip" => "Clip",
        _ => string.IsNullOrWhiteSpace(kind) ? "Job" : kind!,
    };

    /// <summary>Short operator line — prefer real error text when the job failed.</summary>
    private static string FriendlyJobLine(JobSnapshot j)
    {
        if (string.Equals(j.Status, "error", StringComparison.OrdinalIgnoreCase))
            return FriendlyOperatorError(j);
        if (string.Equals(j.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            return "Cancelled.";
        if (string.Equals(j.Status, "done", StringComparison.OrdinalIgnoreCase))
            return "Done.";
        // Running / queued: prefer a short outcome phrase over raw Message (which may be technical)
        if (string.Equals(j.Kind, "character", StringComparison.OrdinalIgnoreCase))
            return "Creating portrait…";
        if (string.Equals(j.Kind, "character-plates", StringComparison.OrdinalIgnoreCase))
            return "Matching book pictures…";
        if (string.Equals(j.Kind, "book_import", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(j.Kind, "book_prepare", StringComparison.OrdinalIgnoreCase))
        {
            var running = j.Message ?? "Importing book…";
            return TrimOneLine(running, 160);
        }
        var msg = j.Message ?? "";
        return TrimOneLine(msg, 120);
    }

    /// <summary>
    /// Operator-facing error. Prefer the job's Error/Message (what failed) with light cleanup,
    /// instead of a useless generic "Something went wrong."
    /// </summary>
    private static string FriendlyOperatorError(JobSnapshot j)
    {
        var raw = FirstNonEmpty(j.Error, j.Message);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var cleaned = SanitizeOperatorError(raw);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
        }

        // Fallbacks only when the job carried no useful text
        if (string.Equals(j.Kind, "character", StringComparison.OrdinalIgnoreCase))
            return "Portrait generation failed. Check your image API key and try again.";
        if (string.Equals(j.Kind, "character-plates", StringComparison.OrdinalIgnoreCase))
            return "Could not match book pictures. Ensure the book was prepared and your AI provider is connected.";
        if (string.Equals(j.Kind, "book_import", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(j.Kind, "book_prepare", StringComparison.OrdinalIgnoreCase))
            return "Story import failed. Check Configuration to ensure your AI provider is connected and try again.";
        if (string.Equals(j.Kind, "stage1", StringComparison.OrdinalIgnoreCase))
            return "Screenplay draft failed. Check your connected AI provider and story text.";
        if (string.Equals(j.Kind, "stage2", StringComparison.OrdinalIgnoreCase))
            return "Shot plan failed. Check your connected AI provider and screenplay.";
        return "Job failed. Open the project and check Configuration or job details.";
    }

    private static string? FirstNonEmpty(params string?[] parts)
    {
        foreach (var p in parts)
        {
            if (!string.IsNullOrWhiteSpace(p))
                return p.Trim();
        }
        return null;
    }

    internal async Task ChangeVisibilityAsync(string projectId, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        _busy = true;
        _error = null;
        try
        {
            await Engine.SetProjectVisibilityModeAsync(projectId, mode);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Strip stack/path noise; keep the actionable sentence.</summary>
    private static string SanitizeOperatorError(string raw)
    {
        var s = raw.Replace("\r\n", "\n").Trim();
        // First line only (stacks often follow)
        var nl = s.IndexOf('\n');
        if (nl > 0) s = s[..nl].Trim();

        // Drop leading "System.X: " type prefixes
        if (s.StartsWith("System.", StringComparison.Ordinal))
        {
            var colon = s.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && colon < 80)
                s = s[(colon + 2)..].Trim();
        }

        // Known high-value rewrites (clearer than raw API blobs)
        if (s.Contains("XAI_API_KEY", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("API key missing", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Connect your AI", StringComparison.OrdinalIgnoreCase))
            return "No AI provider connected. Open Configuration to connect your AI provider.";

        if (s.Contains("No page images", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Could not extract or render page images", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Page render failed", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("libpdfium", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("libSkiaSharp", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("DllNotFoundException", StringComparison.OrdinalIgnoreCase))
            return "Could not process PDF pages. Check your uploaded file format or try re-uploading.";

        if (s.Contains("No PDF and no book_full", StringComparison.OrdinalIgnoreCase))
            return "No story file found on the server. Re-upload the source file, then import again.";

        if (s.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "Story import timed out. Please try importing again.";

        if (s.Contains("could not be decrypted", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("encryption keys changed", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("DataProtector", StringComparison.OrdinalIgnoreCase))
            return "Saved credentials need to be re-entered. Open Configuration and save your settings again.";

        if (s.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Incorrect API key", StringComparison.OrdinalIgnoreCase))
            return "Service connection rejected credentials (401). Open Configuration and save valid credentials.";

        if (s.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "Service rate limit hit. Please wait a minute and try again.";

        if (s.Contains("Could not build a usable screenplay", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Book adapt", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("chunk", StringComparison.OrdinalIgnoreCase) && s.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return "Screenplay generation failed after book text was ready. " + TrimOneLine(s, 200);

        return TrimOneLine(s, 280);
    }

    private static string TrimOneLine(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        if (s.Length > max) return s[..(max - 1)] + "…";
        return s;
    }

    internal async Task SyncOriginAsync(string projectId, string parentProjectId)
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.SyncOriginAsync(projectId, parentProjectId);
            if (res is { Ok: true } || res is { Success: true })
            {
                if (res.HasConflicts)
                {
                    _error = $"Sync from origin returned conflicts: {res.Message}";
                }
                else
                {
                    _message = $"Successfully synced from parent project {parentProjectId}!";
                }
            }
            else
            {
                _error = res?.Error ?? res?.Message ?? "Sync from origin failed";
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task SaveRevisionAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.PushProjectAsync(projectId, commitFirst: true, message: "Project update");
            if (res is null || res.Ok == false)
            {
                _error = FriendlyPushError(res?.Error ?? res?.Message ?? "Could not save revision.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(res.CommitHash))
                _revisionHashes[projectId] = res.CommitHash!;
            if (!string.IsNullOrWhiteSpace(res.HistoryUrl))
                _historyUrls[projectId] = res.HistoryUrl!;

            var shortHash = ShortHash(res.CommitHash);
            _message = string.IsNullOrEmpty(shortHash)
                ? "Revision saved."
                : $"Revision saved ({shortHash}).";
            await RefreshPackageStatusAsync();
        }
        catch (Exception ex)
        {
            _error = FriendlyPushError(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task OpenHistoryAsync(string historyUrl)
    {
        if (string.IsNullOrWhiteSpace(historyUrl)) return;
        try
        {
            await Js.InvokeVoidAsync("open", historyUrl, "_blank", "noopener,noreferrer");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    /// <summary>Operator-facing rewrite — avoid config key dumps on Home.</summary>
    private static string FriendlyPushError(string raw)
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

    private void OnCultureChanged(System.Globalization.CultureInfo culture) => _ = InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        L.CultureChanged -= OnCultureChanged;
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        await Hub.DisposeAsync();
    }
}

