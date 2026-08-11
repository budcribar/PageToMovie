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

    /// <summary>
    /// Hide “Next” / stale status alerts while a pipeline step is still running
    /// (e.g. Import writing the draft after book prepare finished).
    /// </summary>
    public bool SuppressGuidanceBanners => Busy || Jobs.JobRunning;

    /// <summary>import | screenplay | shots</summary>
    public abstract string StepKey { get; }

    /// <summary>Called after SoftLoad when an adaptation job finishes (stage1/stage2/…).</summary>
    public virtual Task OnAdaptationJobTerminalAsync(JobSnapshot snap) => Task.CompletedTask;

    protected override async Task OnInitializedAsync()
    {
        Hub.JobUpdated += Jobs.OnJobUpdated;
        Hub.JobLog += Jobs.OnJobLog;
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
            Jobs.Job = jobs?.Job;
            Jobs.TryReattachRunningJob();

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

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
            Jobs.Job = jobs?.Job;
            Jobs.TryReattachRunningJob();
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
            Pipeline.TotalMinutes = Math.Clamp(tmin, 2, 180);
        else if (Status?.Book.NaturalRuntimeMinutes is int nmin && nmin > 0)
            Pipeline.TotalMinutes = Math.Clamp(nmin, 2, 180);
        else if (Status?.Book.SuggestedTotalMinutes is int m && m > 0)
            Pipeline.TotalMinutes = Math.Clamp(m, 2, 180);
        if (Status?.Book.SuggestedChunkPages is int c && c > 0)
            Pipeline.ChunkPages = Math.Clamp(c, 5, 30);
        if (!string.IsNullOrWhiteSpace(Status?.PlanningModel))
            Pipeline.Model = Status.PlanningModel;
    }

    public virtual async ValueTask DisposeAsync()
    {
        Jobs.DisposePolling();
        Hub.JobUpdated -= Jobs.OnJobUpdated;
        Hub.JobLog -= Jobs.OnJobLog;
        await Task.CompletedTask;
    }
}
