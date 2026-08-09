using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace PageToMovie.Web.Components.Pages;

/// <summary>Shared project / job / status logic for Adaptation step pages.</summary>
public abstract partial class AdaptationPageBase : ComponentBase, IAsyncDisposable
{
    [Inject] protected EngineApiClient Engine { get; set; } = null!;
    [Inject] protected JobHubClient Hub { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected ActiveProjectState ActiveProject { get; set; } = null!;

    public bool Busy;
    /// <summary>Short operator-facing label while <see cref="Busy"/> (shown with progress bar).</summary>
    public string? BusyMessage;
    public string? Error;
    public string? Message;
    public string ProjectId = "";
    /// <summary>Display name for the active project (set on Home; read-only here).</summary>
    public string ProjectLabel = "";
    public AdaptationStatus? Status;

    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private AdaptationJobs? _jobs;
    public AdaptationJobs Jobs => _jobs ??= new AdaptationJobs(this);
    private AdaptationPipeline? _pipeline;
    public AdaptationPipeline Pipeline => _pipeline ??= new AdaptationPipeline(this);

    // ── Field / property forwarders (public API unchanged for inheriting pages) ──
    public JobSnapshot? Job
    {
        get => Jobs.Job;
        set => Jobs.Job = value;
    }

    public int ProgressIndex
    {
        get => Jobs.ProgressIndex;
        set => Jobs.ProgressIndex = value;
    }

    public int ProgressTotal
    {
        get => Jobs.ProgressTotal;
        set => Jobs.ProgressTotal = value;
    }

    public IBrowserFile? PendingFile
    {
        get => Pipeline.PendingFile;
        set => Pipeline.PendingFile = value;
    }

    public int TotalMinutes
    {
        get => Pipeline.TotalMinutes;
        set => Pipeline.TotalMinutes = value;
    }

    public int ChunkPages
    {
        get => Pipeline.ChunkPages;
        set => Pipeline.ChunkPages = value;
    }

    public string Model
    {
        get => Pipeline.Model;
        set => Pipeline.Model = value;
    }

    public bool Resume
    {
        get => Pipeline.Resume;
        set => Pipeline.Resume = value;
    }

    public bool JobRunning => Jobs.JobRunning;

    /// <summary>
    /// Hide “Next” / stale status alerts while a pipeline step is still running
    /// (e.g. Import writing the draft after book prepare finished).
    /// </summary>
    public bool SuppressGuidanceBanners => Busy || JobRunning;

    /// <summary>import | screenplay | shots</summary>
    public abstract string StepKey { get; }

    public bool CanRunOutline => Pipeline.CanRunOutline;

    protected override async Task OnInitializedAsync()
    {
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        try
        {
            await ActiveProject.RefreshFromServerAsync(Engine);
            if (!ActiveProject.HasProject)
            {
                Error = "No project selected. Create or choose one on Studio.";
                return;
            }
            ProjectId = ActiveProject.ProjectId!;
            ProjectLabel = ActiveProject.Label ?? ProjectId;

            try { await Hub.StartAsync(); } catch { /* optional */ }

            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void OnJobUpdated(JobSnapshot snap) => Jobs.OnJobUpdated(snap);

    private void OnJobLog(string line) => Jobs.OnJobLog(line);

    protected void AbsorbProgressFromSnapshot(JobSnapshot snap) => Jobs.AbsorbProgressFromSnapshot(snap);

    protected void AbsorbProgressFromLine(string? line) => Jobs.AbsorbProgressFromLine(line);

    public virtual async Task LoadAsync()
    {
        Busy = true;
        Error = null;
        try
        {
            var dto = await Engine.GetAdaptationAsync(ProjectId);
            Status = dto?.Adaptation;
            ApplyDefaultsFromStatus();
            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = null;
        }
        finally { Busy = false; }
    }

    public async Task SoftLoadAsync()
    {
        try
        {
            var dto = await Engine.GetAdaptationAsync(ProjectId);
            Status = dto?.Adaptation;
            ApplyDefaultsFromStatus();
        }
        catch { /* ignore */ }
    }

    protected void ApplyDefaultsFromStatus()
    {
        if (Status?.Book.TargetRuntimeMinutes is int tmin && tmin > 0)
            TotalMinutes = Math.Clamp(tmin, 2, 180);
        else if (Status?.Book.NaturalRuntimeMinutes is int nmin && nmin > 0)
            TotalMinutes = Math.Clamp(nmin, 2, 180);
        else if (Status?.Book.SuggestedTotalMinutes is int m && m > 0)
            TotalMinutes = Math.Clamp(m, 2, 180);
        if (Status?.Book.SuggestedChunkPages is int c && c > 0)
            ChunkPages = Math.Clamp(c, 5, 30);
        if (!string.IsNullOrWhiteSpace(Status?.PlanningModel))
            Model = Status.PlanningModel;
    }

    public void OnFileSelected(InputFileChangeEventArgs e) => Pipeline.OnFileSelected(e);

    public Task UploadAsync() => Pipeline.UploadAsync();

    public Task PrepareBookAsync(bool forceVision) => Pipeline.PrepareBookAsync(forceVision);

    /// <summary>
    /// Book → Fountain draft (and approve for shot build). Uses prompts/book_to_fountain.txt only.
    /// </summary>
    public Task RunOutlineAsync() => Pipeline.RunOutlineAsync();

    public Task RunShotsAsync() => Pipeline.RunShotsAsync();

    protected void StartJobPolling() => Jobs.StartJobPolling();

    public Task CancelAsync() => Jobs.CancelAsync();

    protected Task EnsureHubAsync() => Jobs.EnsureHubAsync();

    // ── Static helpers (forward to domain / StepUi so call sites stay unchanged) ──

    public static bool IsJobInFlightMessage(string? message) =>
        AdaptationJobs.IsJobInFlightMessage(message);

    /// <summary>
    /// Operator-facing live job line (no provider / path / mechanism jargon).
    /// </summary>
    public static string OperatorJobRunningMessage(JobSnapshot snap) =>
        AdaptationJobs.OperatorJobRunningMessage(snap);

    public static string NextStepLabel(string step) => AdaptationStepUi.NextStepLabel(step);

    /// <summary>Short operator copy when a background job finishes (no OCR/engine jargon).</summary>
    public static string OperatorJobDoneMessage(JobSnapshot snap) =>
        AdaptationStepUi.OperatorJobDoneMessage(snap);

    public static string NextStepAlertClass(string step) => AdaptationStepUi.NextStepAlertClass(step);

    public static string JobKindLabel(string? kind) => AdaptationStepUi.JobKindLabel(kind);

    /// <summary>Suggested path for /adaptation redirect.</summary>
    public static string SuggestedStepPath(AdaptationStatus? status) =>
        AdaptationStepUi.SuggestedStepPath(status);

    /// <summary>Step strip: Screenplay tab unlocks once a draft/outline exists in some form.</summary>
    public static bool OutlineEnabled(AdaptationStatus? status) =>
        AdaptationStepUi.OutlineEnabled(status);

    /// <summary>Step strip: Characters/Shot-plan tabs unlock once the screenplay is signed off.</summary>
    public static bool ShotsEnabled(AdaptationStatus? status) =>
        AdaptationStepUi.ShotsEnabled(status);

    /// <summary>
    /// Compact progress + Cancel while adaptation jobs run (operators and admin).
    /// Import page never shows this card (progress lives in the Import card).
    /// Admin can expand log after the job finishes; operators never see raw logs here.
    /// </summary>
    public static bool ShowJobPanel(bool isAdmin, JobSnapshot? job, string step) =>
        AdaptationStepUi.ShowJobPanel(isAdmin, job, step);

    /// <summary>Merges job-reported and locally-tracked (log-scraped) progress into one index/total/waiting triple.</summary>
    public static (int Index, int Total, bool Waiting, int DisplayIndex) ComputeJobProgress(
        JobSnapshot job, int progressIndex, int progressTotal, bool jobRunning) =>
        AdaptationStepUi.ComputeJobProgress(job, progressIndex, progressTotal, jobRunning);

    /// <summary>
    /// Progress-bar percent for a running job — never 0% or 100% while still running.
    /// When Total is missing or a long adapt call is in-flight, soft-crawls so the bar
    /// does not freeze at a single placeholder (old Total=0 → hard 35%).
    /// </summary>
    public static int ComputeProgressPercent(int displayIndex, int total, bool waiting, bool jobRunning) =>
        AdaptationStepUi.ComputeProgressPercent(displayIndex, total, waiting, jobRunning);

    public static int ComputeProgressPercent(
        int displayIndex, int total, bool waiting, bool jobRunning, DateTimeOffset? startedAt) =>
        AdaptationStepUi.ComputeProgressPercent(displayIndex, total, waiting, jobRunning, startedAt);

    /// <summary>Asymptotic crawl floor→ceiling with half-life ~tauSeconds.</summary>
    public static int SoftCrawlPercent(DateTimeOffset? startedAt, int floor, int ceiling, double tauSeconds) =>
        AdaptationStepUi.SoftCrawlPercent(startedAt, floor, ceiling, tauSeconds);

    /// <summary>
    /// Hide the "Next" banner when the current step already is the next action
    /// (avoids "Next: import…" on the Import page).
    /// </summary>
    public static bool ShowNextStepBanner(AdaptationStatus? status, bool suppressGuidanceBanners, string step) =>
        AdaptationStepUi.ShowNextStepBanner(status, suppressGuidanceBanners, step);

    public virtual async ValueTask DisposeAsync()
    {
        Jobs.DisposePolling();
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        await Task.CompletedTask;
    }
}
