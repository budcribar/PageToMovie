using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private AdminJobs? _jobs;
    internal AdminJobs Jobs => _jobs ??= new AdminJobs(this);
    private AdminArchive? _archive;
    internal AdminArchive Archive => _archive ??= new AdminArchive(this);
    private AdminTelemetry? _telemetry;
    internal AdminTelemetry Telemetry => _telemetry ??= new AdminTelemetry(this);
    private AdminState? _stateDomain;
    internal AdminState State => _stateDomain ??= new AdminState(this);
    private AdminUi? _ui;
    internal AdminUi Ui => _ui ??= new AdminUi(this);

    internal void EnsureDomains()
    {
        _ = Jobs; _ = Archive; _ = Telemetry; _ = State; _ = Ui;
    }

    // Shell-owned shared status
    internal string? _error;
    internal string? _actionMsg;
    internal bool _busy;

    // ── Field forwarders (Host._x for markup children) ──
    internal bool _hubLive
    {
        get => Jobs._hubLive;
        set => Jobs._hubLive = value;
    }
    internal List<AdminLockDto> _locks
    {
        get => Jobs._locks;
        set => Jobs._locks = value;
    }
    internal string? _logJobId
    {
        get => Jobs._logJobId;
        set => Jobs._logJobId = value;
    }
    internal string _logJobIdInput
    {
        get => Jobs._logJobIdInput;
        set => Jobs._logJobIdInput = value;
    }
    internal JobSnapshot? _jobLog
    {
        get => Jobs._jobLog;
        set => Jobs._jobLog = value;
    }
    internal string? _logError
    {
        get => Jobs._logError;
        set => Jobs._logError = value;
    }

    internal List<string> _projectOptions
    {
        get => Archive._projectOptions;
        set => Archive._projectOptions = value;
    }
    internal List<string> _userList
    {
        get => Archive._userList;
        set => Archive._userList = value;
    }
    internal string _exportProjectId
    {
        get => Archive._exportProjectId;
        set => Archive._exportProjectId = value;
    }
    internal string _augmentProjectId
    {
        get => Archive._augmentProjectId;
        set => Archive._augmentProjectId = value;
    }
    internal string _importPreferredId
    {
        get => Archive._importPreferredId;
        set => Archive._importPreferredId = value;
    }
    internal string _importTargetUserId
    {
        get => Archive._importTargetUserId;
        set => Archive._importTargetUserId = value;
    }
    internal bool _importOverwrite
    {
        get => Archive._importOverwrite;
        set => Archive._importOverwrite = value;
    }
    internal IBrowserFile? _importFile
    {
        get => Archive._importFile;
        set => Archive._importFile = value;
    }
    internal bool _archiveBusy
    {
        get => Archive._archiveBusy;
        set => Archive._archiveBusy = value;
    }
    internal string? _archiveAction
    {
        get => Archive._archiveAction;
        set => Archive._archiveAction = value;
    }
    internal string? _archiveMsg
    {
        get => Archive._archiveMsg;
        set => Archive._archiveMsg = value;
    }
    internal string? _archiveError
    {
        get => Archive._archiveError;
        set => Archive._archiveError = value;
    }
    internal int _synthesizeCurrent
    {
        get => Archive._synthesizeCurrent;
        set => Archive._synthesizeCurrent = value;
    }
    internal int _synthesizeTotal
    {
        get => Archive._synthesizeTotal;
        set => Archive._synthesizeTotal = value;
    }

    internal LoadSimLiveStateDto? _loadSim
    {
        get => Telemetry._loadSim;
        set => Telemetry._loadSim = value;
    }
    internal List<ProcessSampleDto> _processHistory
    {
        get => Telemetry._processHistory;
        set => Telemetry._processHistory = value;
    }
    internal EngineApiClient.TimingTelemetryTrendDto? _timingTelemetry
    {
        get => Telemetry._timingTelemetry;
        set => Telemetry._timingTelemetry = value;
    }
    internal string? _chartWarning
    {
        get => Telemetry._chartWarning;
        set => Telemetry._chartWarning = value;
    }
    internal List<EngineApiClient.GenerationErrorRowDto>? _genErrors
    {
        get => Telemetry._genErrors;
        set => Telemetry._genErrors = value;
    }
    internal bool _genErrorsBusy
    {
        get => Telemetry._genErrorsBusy;
        set => Telemetry._genErrorsBusy = value;
    }
    internal string _genErrorTypeFilter
    {
        get => Telemetry._genErrorTypeFilter;
        set => Telemetry._genErrorTypeFilter = value;
    }
    internal string _genErrorProjectFilter
    {
        get => Telemetry._genErrorProjectFilter;
        set => Telemetry._genErrorProjectFilter = value;
    }
    internal bool _seedingTiming
    {
        get => Telemetry._seedingTiming;
        set => Telemetry._seedingTiming = value;
    }

    internal AdminStateDto? _state
    {
        get => State._state;
        set => State._state = value;
    }
    internal int _apiInFlight
    {
        get => State._apiInFlight;
        set => State._apiInFlight = value;
    }
    internal int _capacityRejects
    {
        get => State._capacityRejects;
        set => State._capacityRejects = value;
    }
    internal int _lockConflicts
    {
        get => State._lockConflicts;
        set => State._lockConflicts = value;
    }
    internal PeriodicTimer? _timer
    {
        get => State._timer;
        set => State._timer = value;
    }
    internal CancellationTokenSource? _pollCts
    {
        get => State._pollCts;
        set => State._pollCts = value;
    }

    internal bool _showTestEmailModal
    {
        get => Ui._showTestEmailModal;
        set => Ui._showTestEmailModal = value;
    }
    internal bool _showJobsAndLocks
    {
        get => Ui._showJobsAndLocks;
        set => Ui._showJobsAndLocks = value;
    }
    internal bool _showProjectArchiving
    {
        get => Ui._showProjectArchiving;
        set => Ui._showProjectArchiving = value;
    }
    internal bool _showLoadSim
    {
        get => Ui._showLoadSim;
        set => Ui._showLoadSim = value;
    }
    internal bool _showTimingTelemetry
    {
        get => Ui._showTimingTelemetry;
        set => Ui._showTimingTelemetry = value;
    }
    internal bool _showGenErrors
    {
        get => Ui._showGenErrors;
        set => Ui._showGenErrors = value;
    }
    internal bool _showStorageAndCapacity
    {
        get => Ui._showStorageAndCapacity;
        set => Ui._showStorageAndCapacity = value;
    }
    internal bool _started
    {
        get => Ui._started;
        set => Ui._started = value;
    }

    internal static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "—";
        return id.Length <= 10 ? id : id[..8] + "…";
    }

    public ValueTask DisposeAsync()
    {
        Hub.AdminState -= OnAdminState;
        MediaFolder.Changed -= OnMediaFolderChanged;
        _stateDomain?.DisposePolling();
        // Do NOT Hub.DisposeAsync() here — JobHubClient is a shared, app-wide singleton (every
        // page's SignalR subscriptions, including ClientMediaFolderService's auto-save-on-
        // generate hook, ride the same connection). Disposing it on navigating away from /admin
        // killed that connection for the rest of the session: ClientMediaFolderService.
        // EnsureHubHookAsync() latches "_hubHooked" true on first call and never retries, so once
        // the underlying connection was torn down here it never came back — every job's generated
        // media (music, clips, anything) would stop reaching the local media folder app-wide,
        // silently, until a full page reload. This page merely stops listening to it.
        return ValueTask.CompletedTask;
    }
}
