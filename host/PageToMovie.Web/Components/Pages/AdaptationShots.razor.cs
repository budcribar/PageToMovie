using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationShots
{

    public override string StepKey => "shots";

    /// <summary>Landed from Estimate DecisionCard Generate path (?from=decision).</summary>
    private bool FromDecisionCard =>
        string.Equals(StudioDeepLinks.QueryValue(Nav, "from"), "decision", StringComparison.OrdinalIgnoreCase);

    /// <summary>B6: after Stage‑2 (+ plan looks), auto-start fill-holes batch and open Film.</summary>
    private bool AutoGen =>
        string.Equals(StudioDeepLinks.QueryValue(Nav, "autoGen"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(StudioDeepLinks.QueryValue(Nav, "autogen"), "1", StringComparison.OrdinalIgnoreCase);

    private string? AutoGenResolution =>
        StudioDeepLinks.QueryValue(Nav, "res");

    private bool _autoGenContinueStarted;
    private bool _autoGenKickStarted;
    private bool _autoGenLooksStarted;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        if (!AutoGen || string.IsNullOrWhiteSpace(ProjectId)) return;
        await TryKickOrContinueAutoGenAsync();
    }

    public override async Task OnAdaptationJobTerminalAsync(JobSnapshot snap)
    {
        if (!AutoGen) return;
        if (!string.Equals(snap.Status, "done", StringComparison.OrdinalIgnoreCase)) return;

        var kind = snap.Kind ?? "";
        if (string.Equals(kind, "stage2", StringComparison.OrdinalIgnoreCase))
        {
            // Shot plan ready → wait briefly for engine auto-chain, then ensure looks, then fill-holes.
            Message = "Shot plan ready — generating looks for cast + places…";
            await Task.Delay(700); // engine defers plan_looks ~500ms after stage2
            await EnsurePlanLooksThenContinueAsync();
            return;
        }

        if (string.Equals(kind, "plan_looks", StringComparison.OrdinalIgnoreCase))
        {
            Message = "Looks ready — starting movie generate…";
            await ContinueAutoGenAfterShotsAsync();
        }
    }

    /// <summary>
    /// If shot plan already ready → plan looks (if needed) → fill-holes + Film.
    /// If stage2 idle and missing → start stage2 (Cost may have already started; reattach handles that).
    /// </summary>
    private async Task TryKickOrContinueAutoGenAsync()
    {
        try
        {
            // Something already running (stage2 from Cost, or plan_looks chain) — wait for terminal.
            if (Jobs.JobRunning) return;

            // Stage2 already ready (e.g. user landed after rebuild or Cost finished before mount)
            if (Status?.Stage2 is { Stage2Ready: true, Stage2Stale: false, Stage2Clips: > 0 })
            {
                await EnsurePlanLooksThenContinueAsync();
                return;
            }

            if (_autoGenKickStarted) return;
            _autoGenKickStarted = true;
            Message = "Building shot plan, then looks, then movie generate…";
            await Pipeline.RunShotsAsync();
        }
        catch (Exception ex)
        {
            Error = "Auto-generate: " + ex.Message;
        }
    }

    /// <summary>
    /// Queue plan_looks when any used plate is missing; otherwise continue to fill-holes.
    /// Engine also auto-queues looks after Stage 2 — if that job is already running we wait.
    /// </summary>
    private async Task EnsurePlanLooksThenContinueAsync()
    {
        try
        {
            // Engine may already have chained plan_looks after stage2.
            if (Jobs.JobRunning)
            {
                if (string.Equals(Jobs.Job?.Kind, "plan_looks", StringComparison.OrdinalIgnoreCase))
                {
                    Message = "Generating looks for cast + places…";
                    return; // wait for plan_looks terminal
                }
                // Unexpected other job — don't fight it
                return;
            }

            if (_autoGenLooksStarted)
            {
                // Looks already kicked and finished (or empty) — go to film gen
                await ContinueAutoGenAfterShotsAsync();
                return;
            }

            _autoGenLooksStarted = true;
            Message = "Generating looks for used cast + places (3 each, AI picks best)…";
            await Engine.StartPlanLooksAsync(new StartPlanLooksRequest
            {
                ProjectId = ProjectId,
                Count = 3,
                SkipAlreadyLocked = true,
                IncludeCast = true,
                IncludeLocations = true,
            });
            var jobs = await Engine.GetJobAsync();
            Jobs.Job = jobs?.Job;
            Jobs.AbsorbProgressFromSnapshot(Jobs.Job ?? new JobSnapshot());
            Jobs.StartJobPolling();

            // If the job finished instantly (nothing to do), continue now.
            if (Jobs.Job is { IsFinished: true, Status: "done" })
                await ContinueAutoGenAfterShotsAsync();
        }
        catch (Exception ex)
        {
            // Looks are best-effort — still try to generate video in draft/full.
            Error = "Looks: " + ex.Message + " — continuing to generate clips.";
            await ContinueAutoGenAfterShotsAsync();
        }
    }

    private async Task ContinueAutoGenAfterShotsAsync()
    {
        if (_autoGenContinueStarted) return;
        _autoGenContinueStarted = true;
        Busy = true;
        try
        {
            try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* readiness refresh is best-effort */ }
            var res = AutoGenResolution;
            if (string.IsNullOrWhiteSpace(res))
            {
                try
                {
                    var cfg = await Engine.GetConfigAsync(ProjectId);
                    if (cfg?.Config is not null
                        && cfg.Config.TryGetValue("resolution", out var el)
                        && el.ValueKind == System.Text.Json.JsonValueKind.String)
                        res = el.GetString();
                }
                catch { /* optional */ }
            }

            var scenesDto = await Engine.GetScenesAsync(ProjectId);
            var nums = scenesDto?.Scenes?
                .Where(s => !s.IsCredits && s.ClipCount > 0)
                .Select(s => s.SceneNumber)
                .OrderBy(n => n)
                .ToList() ?? new List<int>();

            if (nums.Count == 0)
            {
                Message = "Shot plan ready — open Film to generate clips.";
                return;
            }

            await Engine.StartBatchGenAsync(
                ProjectId,
                nums,
                onlyMissing: true,
                resolution: res,
                takeTrigger: VideoTakeKinds.FillHoles);
            Message = "Looks ready — generating missing clips…";
            Nav.NavigateTo("scenes?watch=1");
        }
        catch (Exception ex)
        {
            Error = "Shot plan ready, but generate failed to start: " + ex.Message
                + " — open Film to generate.";
            _autoGenContinueStarted = false; // allow retry from UI if needed
        }
        finally
        {
            Busy = false;
        }
    }
}
