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

public partial class Cost : IAsyncDisposable
{
    /// <summary>Pre-gen hub: DecisionCard → optional EditFocus → ConfirmGenerate.</summary>
    private enum DecisionPhase
    {
        Card,
        EditFocus,
        ConfirmGenerate,
    }

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

    private DecisionPhase _phase = DecisionPhase.Card;
    /// <summary>cost | duration | both | craft — last edit focus (session + localStorage).</summary>
    private string? _editFocus;
    /// <summary>generate | edit — preferred DecisionCard emphasis.</summary>
    private string _preferPath = "generate";
    private bool _prefsLoaded;

    [Inject] private IJSRuntime Js { get; set; } = null!;

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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _prefsLoaded || _disposed) return;
        _prefsLoaded = true;
        try
        {
            var path = await Js.InvokeAsync<string?>("localStorage.getItem", PrefKey("preferPath"));
            if (string.Equals(path, "edit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "generate", StringComparison.OrdinalIgnoreCase))
                _preferPath = path.ToLowerInvariant();

            var focus = await Js.InvokeAsync<string?>("localStorage.getItem", PrefKey("editFocus"));
            if (focus is "cost" or "duration" or "both" or "craft")
                _editFocus = focus;

            StateHasChanged();
        }
        catch
        {
            /* localStorage optional (SSR / privacy) */
        }
    }

    private string PrefKey(string name) =>
        $"ptm.decision.{(string.IsNullOrEmpty(_projectId) ? "global" : _projectId)}.{name}";

    private async Task PersistPrefAsync(string name, string? value)
    {
        try
        {
            if (string.IsNullOrEmpty(value))
                await Js.InvokeVoidAsync("localStorage.removeItem", PrefKey(name));
            else
                await Js.InvokeVoidAsync("localStorage.setItem", PrefKey(name), value);
        }
        catch { /* ignore */ }
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

    // —— DecisionCard helpers ——

    private bool HasShotPlan =>
        _report is not null
        && string.Equals(_report.EstimateBasis, "shot_plan", StringComparison.OrdinalIgnoreCase);

    private string EstimateBasisLabel =>
        _report is null ? "—"
        : HasShotPlan ? "shot plan"
        : string.Equals(_report.EstimateBasis, "screenplay", StringComparison.OrdinalIgnoreCase) ? "screenplay (projected)"
        : string.IsNullOrWhiteSpace(_report.EstimateBasis) ? "projected"
        : _report.EstimateBasis;

    private int DisplayTargetMinutes
    {
        get
        {
            if ((_filmRuntime?.TargetMinutes ?? 0) > 0)
                return _filmRuntime!.TargetMinutes;
            return Math.Max(1, _filmRuntime?.NaturalMinutes ?? 1);
        }
    }

    private double DisplayEstimateUsd
    {
        get
        {
            if (_report is null) return 0;
            return HasShotPlan
                ? _report.Summary.FullFilmAllDraftUsd
                : ProjectedEstimateUsd(DisplayTargetMinutes);
        }
    }

    private string DurationLabel
    {
        get
        {
            var natural = _filmRuntime?.NaturalMinutes;
            var target = _filmRuntime?.TargetMinutes ?? 0;
            if (target > 0 && natural is > 0 && target != natural)
                return $"~{target} min target (natural ~{natural})";
            if (target > 0)
                return $"~{target} min";
            if (natural is > 0)
                return $"~{natural} min";
            return "duration TBD";
        }
    }

    private void ChooseGenerate()
    {
        _preferPath = "generate";
        _ = PersistPrefAsync("preferPath", "generate");
        _phase = DecisionPhase.ConfirmGenerate;
    }

    private void ChooseEdit()
    {
        _preferPath = "edit";
        _ = PersistPrefAsync("preferPath", "edit");
        _phase = DecisionPhase.EditFocus;
    }

    private void BackToCard()
    {
        _phase = DecisionPhase.Card;
    }

    private async Task ConfirmGenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        // Fresh estimate before leaving
        await LoadAsync();
        if (!string.IsNullOrEmpty(_error) && _report is null) return;

        _preferPath = "generate";
        await PersistPrefAsync("preferPath", "generate");
        Nav.NavigateTo(ResolveGenerateHref());
    }

    /// <summary>
    /// Generate path: Film when shot plan ready; else shot plan (may run as next step); never dead-end.
    /// </summary>
    private string ResolveGenerateHref()
    {
        if (ActiveProject.CanScenes)
            return ActiveProject.IsSimpleVoice ? "scenes?simple=1" : "scenes";
        if (ActiveProject.CanCharacters)
            return "adaptation/shots";
        // Screenplay exists but cast not open — still allow shot plan attempt or cast
        if (ActiveProject.CanEstimate)
            return "adaptation/shots";
        return "adaptation/screenplay";
    }

    private string GenerateBlockedReason
    {
        get
        {
            if (!ActiveProject.CanEstimate)
                return ActiveProject.EstimateBlockedReason ?? "Finish the screenplay first.";
            if (ActiveProject.CanScenes)
                return "";
            if (!string.IsNullOrWhiteSpace(ActiveProject.ScenesBlockedReason))
                return ActiveProject.ScenesBlockedReason + " Generate will open the next setup step.";
            return "Shot plan not ready yet — continue will open planning, then Film.";
        }
    }

    private async Task SelectEditFocusAsync(string focus)
    {
        _editFocus = focus;
        await PersistPrefAsync("editFocus", focus);
        Nav.NavigateTo(ResolveEditFocusHref(focus));
    }

    private static string ResolveEditFocusHref(string focus) => focus switch
    {
        "cost" => "adaptation/screenplay?tool=fit",
        "duration" => "adaptation/screenplay?tool=fit",
        "both" => "adaptation/screenplay?tool=fit",
        "craft" => "characters",
        _ => "adaptation/screenplay?tool=fit",
    };

    private string EditFocusHint(string focus) => focus switch
    {
        "cost" => "Trim length and resolution — shorter usually costs less. Then return here to refresh the estimate.",
        "duration" => "Set target runtime and fit/trim the screenplay. Estimate updates when you come back.",
        "both" => "Start with runtime (fit length); cost usually follows. Return here for a new quote.",
        "craft" => "Cast looks, voices, and locations. Then come back to Estimate for an updated forecast.",
        _ => "",
    };

    // ——— Minutes-based estimate projection (pre-shot-plan) ———
    private const double ProjectionBaseUsd = 0.80;
    private const double ProjectionPerMinFallbackUsd = 1.40;

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
