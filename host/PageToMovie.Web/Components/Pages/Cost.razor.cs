using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using System.Globalization;
using static PageToMovie.Web.Components.CostFormatting;

namespace PageToMovie.Web.Components.Pages;

public partial class Cost
{

    private bool _disposed;
    private CancellationTokenSource _pageCts = new();
    private bool _busy;
    private string? _error;
    private string _projectId = "";
    private CostReport? _report;
    private FilmRuntimeDto? _filmRuntime;
    private string _draftRes = "480p";
    private string _heroRes = "720p";
    private double _retries = 0.5;

    
    protected override async Task OnParametersSetAsync()
    {
        if (_disposed) return;
        try
        {
            await ActiveProject.EnsureLoadedAsync(Engine, ct: _pageCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed) return;
        await base.OnParametersSetAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _projectId = await CostFormatting.ResolveActiveProjectIdAsync(Engine);
            if (string.IsNullOrEmpty(_projectId)) return;

            (_draftRes, _retries) = await CostFormatting.ReadResolutionAndRetriesAsync(Engine, _projectId, _draftRes, _retries);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task SetDraftResolutionAsync(string res)
    {
        var applied = await CostFormatting.TrySetDraftResolutionAsync(Engine, _projectId, res, _draftRes, _report is not null);
        if (applied is null) return;
        _draftRes = applied;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        // Hard timeout so a stalled request can never leave the page (and the length control it
        // disables via Disabled="@_busy") stuck loading.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var dto = await Engine.GetCostAsync(_projectId, _draftRes, _heroRes, _retries, cts.Token);
            _report = dto?.Cost;
            if (_report is not null && !string.IsNullOrWhiteSpace(_report.DraftResolution))
                _draftRes = _report.DraftResolution;
            try
            {
                _filmRuntime = await Engine.GetFilmRuntimeAsync(_projectId, cts.Token);
            }
            catch
            {
                _filmRuntime = null;
            }
        }
        catch (OperationCanceledException)
        {
            _error = "Loading the estimate timed out — check your connection and refresh.";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _report = null;
            _filmRuntime = null;
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// Confirm the estimate and go to the next incomplete stage.
    /// Only navigates to Scenes/Film when <see cref="ActiveProjectState.CanScenes"/> is true
    /// (shot plan present); otherwise Cast or shot plan — never a gated empty Scenes page.
    /// </summary>
    private void AgreeAndContinueAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        Nav.NavigateTo(ResolveAgreeNextHref());
    }

    private string AgreeButtonLabel => ActiveProject.CanScenes
        ? "Agree & Continue to Film →"
        : ActiveProject.CanCharacters
            ? "Agree & Continue to shot plan →"
            : "Agree & Continue to cast →";

    private string AgreeNextStageHint
    {
        get
        {
            if (ActiveProject.CanScenes)
                return "Shot plan is ready — open Film to generate clips.";
            if (ActiveProject.CanCharacters)
                return string.IsNullOrWhiteSpace(ActiveProject.ScenesBlockedReason)
                    ? "Next: build the shot plan, then generate clips."
                    : ActiveProject.ScenesBlockedReason;
            return string.IsNullOrWhiteSpace(ActiveProject.CharactersBlockedReason)
                ? "Next: finish cast (voices + locked looks), then the shot plan."
                : ActiveProject.CharactersBlockedReason;
        }
    }

    private string ResolveAgreeNextHref()
    {
        // Film only when shot plan exists (CanScenes).
        if (ActiveProject.CanScenes)
            return ActiveProject.IsSimpleVoice ? "scenes?simple=1" : "scenes";
        // Screenplay ready → characters/cast is the usual next setup step; shot plan after cast tooling.
        // Prefer shot plan when cast is already considered ready enough for browsing plans.
        if (ActiveProject.CanCharacters)
        {
            // If scenes are blocked specifically on shot plan, go there; if on cast details, characters.
            var reason = ActiveProject.ScenesBlockedReason ?? "";
            if (reason.Contains("shot plan", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Update the shot", StringComparison.OrdinalIgnoreCase))
                return "adaptation/shots";
            if (reason.Contains("character", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("voice", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("cast", StringComparison.OrdinalIgnoreCase))
                return "characters";
            return "adaptation/shots";
        }
        return "adaptation/screenplay";
    }

    // ——— Minutes-based estimate projection (pre-shot-plan) ———
    // Video scales with the selected resolution's list rate ($/sec) × target length; plus a small base
    // for cast + planning. Deliberately simple so the number responds to the length + resolution the
    // user picks. Tune these as we learn from real runs / user selections.
    private const double ProjectionBaseUsd = 0.80;
    private const double ProjectionPerMinFallbackUsd = 1.40; // used only if no live video rate yet

    private double ProjectedEstimateUsd(int targetMinutes)
    {
        var minutes = Math.Max(1, targetMinutes);
        var perSec = _report?.OutputRateDraft ?? 0;
        var videoUsd = perSec > 0
            ? perSec * minutes * 60.0
            : ProjectionPerMinFallbackUsd * minutes;
        return ProjectionBaseUsd + videoUsd;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        try { _pageCts.Cancel(); } catch { /* */ }
        try { _pageCts.Dispose(); } catch { /* */ }
        return ValueTask.CompletedTask;
    }
}
