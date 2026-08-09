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
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private HomeJobs? _jobs;
    internal HomeJobs Jobs => _jobs ??= new HomeJobs(this);
    private HomeImport? _import;
    internal HomeImport Import => _import ??= new HomeImport(this);
    private HomeCheckpoints? _checkpointsDomain;
    internal HomeCheckpoints Checkpoints => _checkpointsDomain ??= new HomeCheckpoints(this);
    private HomeCosts? _costs;
    internal HomeCosts Costs => _costs ??= new HomeCosts(this);
    private HomeProjects? _projectsDomain;
    internal HomeProjects Projects => _projectsDomain ??= new HomeProjects(this);

    internal void EnsureDomains()
    {
        _ = Projects; _ = Import; _ = Checkpoints; _ = Jobs; _ = Costs;
    }


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


    internal static string ShortHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "";
        var h = hash.Trim();
        return h.Length <= 7 ? h : h[..7];
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


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
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


    private static string? FirstNonEmpty(params string?[] parts)
    {
        foreach (var p in parts)
        {
            if (!string.IsNullOrWhiteSpace(p))
                return p.Trim();
        }
        return null;
    }


    private static string TrimOneLine(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        if (s.Length > max) return s[..(max - 1)] + "…";
        return s;
    }


    private void OnCultureChanged(System.Globalization.CultureInfo culture) => _ = InvokeAsync(StateHasChanged);


    public async ValueTask DisposeAsync()
    {
        L.CultureChanged -= OnCultureChanged;
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        await Hub.DisposeAsync();
    }


    // ── Field forwarders (Host._x for markup children) ──
    internal JobSnapshot? _job
    {
        get => Jobs._job;
        set => Jobs._job = value;
    }
    internal bool _jobsExpanded
    {
        get => Jobs._jobsExpanded;
        set => Jobs._jobsExpanded = value;
    }
    internal bool? _jobsUserPreference
    {
        get => Jobs._jobsUserPreference;
        set => Jobs._jobsUserPreference = value;
    }
    internal List<JobSnapshot> _myJobs
    {
        get => Jobs._myJobs;
        set => Jobs._myJobs = value;
    }
    internal UncommittedStatusDto? _packageStatus
    {
        get => Jobs._packageStatus;
        set => Jobs._packageStatus = value;
    }
    internal bool _packageStatusLoading
    {
        get => Jobs._packageStatusLoading;
        set => Jobs._packageStatusLoading = value;
    }
    internal bool _backingUp
    {
        get => Import._backingUp;
        set => Import._backingUp = value;
    }
    internal IBrowserFile? _importFile
    {
        get => Import._importFile;
        set => Import._importFile = value;
    }
    internal string _importName
    {
        get => Import._importName;
        set => Import._importName = value;
    }
    internal bool _importing
    {
        get => Import._importing;
        set => Import._importing = value;
    }
    internal bool _showImport
    {
        get => Import._showImport;
        set => Import._showImport = value;
    }
    internal bool _checkpointBusy
    {
        get => Checkpoints._checkpointBusy;
        set => Checkpoints._checkpointBusy = value;
    }
    internal string _checkpointName
    {
        get => Checkpoints._checkpointName;
        set => Checkpoints._checkpointName = value;
    }
    internal List<CheckpointDto> _checkpoints
    {
        get => Checkpoints._checkpoints;
        set => Checkpoints._checkpoints = value;
    }
    internal bool _showCheckpoints
    {
        get => Checkpoints._showCheckpoints;
        set => Checkpoints._showCheckpoints = value;
    }
    internal double? _costActualUsd
    {
        get => Costs._costActualUsd;
        set => Costs._costActualUsd = value;
    }
    internal double? _costEstimateUsd
    {
        get => Costs._costEstimateUsd;
        set => Costs._costEstimateUsd = value;
    }
    internal bool _costLoading
    {
        get => Costs._costLoading;
        set => Costs._costLoading = value;
    }
    internal bool _costNeedsModels
    {
        get => Costs._costNeedsModels;
        set => Costs._costNeedsModels = value;
    }
    internal string? _costResolution
    {
        get => Costs._costResolution;
        set => Costs._costResolution = value;
    }
    internal double? _costVideoRate
    {
        get => Costs._costVideoRate;
        set => Costs._costVideoRate = value;
    }
    internal string _demoShowcaseHint
    {
        get => Costs._demoShowcaseHint;
        set => Costs._demoShowcaseHint = value;
    }
    internal List<DemoListItem> _publicDemos
    {
        get => Costs._publicDemos;
        set => Costs._publicDemos = value;
    }
    internal string _collaboratorsProjectId
    {
        get => Projects._collaboratorsProjectId;
        set => Projects._collaboratorsProjectId = value;
    }
    internal string _deleteConfirm
    {
        get => Projects._deleteConfirm;
        set => Projects._deleteConfirm = value;
    }
    internal string? _deleteId
    {
        get => Projects._deleteId;
        set => Projects._deleteId = value;
    }
    internal string _deleteLabel
    {
        get => Projects._deleteLabel;
        set => Projects._deleteLabel = value;
    }
    internal bool _fullStudioHome
    {
        get => Projects._fullStudioHome;
        set => Projects._fullStudioHome = value;
    }
    internal Dictionary<string, string> _historyUrls => Projects._historyUrls;
    internal bool _manageExpanded
    {
        get => Projects._manageExpanded;
        set => Projects._manageExpanded = value;
    }
    internal ElementReference _nameInputRef
    {
        get => Projects._nameInputRef;
        set => Projects._nameInputRef = value;
    }
    internal string _newName
    {
        get => Projects._newName;
        set => Projects._newName = value;
    }
    internal ProjectsDto? _projects
    {
        get => Projects._projects;
        set => Projects._projects = value;
    }
    internal string _renameName
    {
        get => Projects._renameName;
        set => Projects._renameName = value;
    }
    internal Dictionary<string, string> _revisionHashes => Projects._revisionHashes;
    internal bool _showCollaboratorsModal
    {
        get => Projects._showCollaboratorsModal;
        set => Projects._showCollaboratorsModal = value;
    }
    internal bool _showNew
    {
        get => Projects._showNew;
        set => Projects._showNew = value;
    }
    internal bool _showRename
    {
        get => Projects._showRename;
        set => Projects._showRename = value;
    }
}
