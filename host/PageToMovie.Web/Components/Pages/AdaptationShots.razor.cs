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

    /// <summary>B6: after Stage‑2, auto-start fill-holes batch and open Film.</summary>
    private bool AutoGen =>
        string.Equals(StudioDeepLinks.QueryValue(Nav, "autoGen"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(StudioDeepLinks.QueryValue(Nav, "autogen"), "1", StringComparison.OrdinalIgnoreCase);

    private string? AutoGenResolution =>
        StudioDeepLinks.QueryValue(Nav, "res");

    private bool _autoGenContinueStarted;
    private bool _autoGenKickStarted;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        if (!AutoGen || string.IsNullOrWhiteSpace(ProjectId)) return;
        await TryKickOrContinueAutoGenAsync();
    }

    public override async Task OnAdaptationJobTerminalAsync(JobSnapshot snap)
    {
        if (!AutoGen) return;
        if (!string.Equals(snap.Kind, "stage2", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(snap.Status, "done", StringComparison.OrdinalIgnoreCase)) return;
        await ContinueAutoGenAfterShotsAsync();
    }

    /// <summary>
    /// If shot plan already ready → fill-holes + Film.
    /// If stage2 idle and missing → start stage2 (Cost may have already started; reattach handles that).
    /// </summary>
    private async Task TryKickOrContinueAutoGenAsync()
    {
        try
        {
            // Stage2 already ready (e.g. user landed after rebuild)
            if (Status?.Stage2 is { Stage2Ready: true, Stage2Stale: false, Stage2Clips: > 0 })
            {
                await ContinueAutoGenAfterShotsAsync();
                return;
            }

            if (Jobs.JobRunning) return; // Cost already started stage2 — wait for OnAdaptationJobTerminalAsync

            if (_autoGenKickStarted) return;
            _autoGenKickStarted = true;
            Message = "Building shot plan, then starting movie generate…";
            await Pipeline.RunShotsAsync();
        }
        catch (Exception ex)
        {
            Error = "Auto-generate: " + ex.Message;
        }
    }

    private async Task ContinueAutoGenAfterShotsAsync()
    {
        if (_autoGenContinueStarted) return;
        _autoGenContinueStarted = true;
        Busy = true;
        try
        {
            try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* */ }
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
            Message = "Shot plan ready — generating missing clips…";
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
