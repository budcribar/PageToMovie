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
        /// <summary>In-page cost/duration toolkit; re-estimates without leaving Estimate.</summary>
        Shaping,
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
    private string? _shapeMessage;
    private string? _shapeError;
    private int? _maxSpeakingCast;
    private string _castCapInput = "";

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

            try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* optional */ }
            await LoadAsync();
            ApplyPhaseQuery();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    /// <summary>Deep links: /cost?phase=edit|confirm|shape&focus=cost|duration|both|craft</summary>
    private void ApplyPhaseQuery()
    {
        try
        {
            var focus = StudioDeepLinks.QueryValue(Nav, "focus");
            if (focus is "cost" or "duration" or "both" or "craft")
                _editFocus = focus;

            var phase = StudioDeepLinks.QueryValue(Nav, "phase");
            if (string.IsNullOrWhiteSpace(phase))
            {
                // ?focus= alone opens shaping / craft
                if (_editFocus is "cost" or "duration" or "both")
                    _phase = DecisionPhase.Shaping;
                else if (_editFocus == "craft")
                    _phase = DecisionPhase.EditFocus;
                return;
            }
            if (phase.Equals("edit", StringComparison.OrdinalIgnoreCase))
                _phase = DecisionPhase.EditFocus;
            else if (phase.Equals("shape", StringComparison.OrdinalIgnoreCase)
                     || phase.Equals("shaping", StringComparison.OrdinalIgnoreCase))
                _phase = DecisionPhase.Shaping;
            else if (phase.Equals("confirm", StringComparison.OrdinalIgnoreCase)
                     || phase.Equals("generate", StringComparison.OrdinalIgnoreCase))
                _phase = DecisionPhase.ConfirmGenerate;

            if (_phase == DecisionPhase.Shaping && _editFocus is null or "craft")
                _editFocus = "both";
        }
        catch { /* ignore */ }
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

        try { await LoadCastCapAsync(); } catch { /* optional */ }
    }

    private async Task LoadCastCapAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        var dto = await Engine.GetConfigAsync(_projectId);
        if (dto?.Config is null) return;
        if (dto.Config.TryGetValue("adaptation_max_speaking_cast", out var el))
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetInt32(out var n))
            {
                _maxSpeakingCast = n;
                _castCapInput = n.ToString();
                return;
            }
            if (el.ValueKind == System.Text.Json.JsonValueKind.String
                && int.TryParse(el.GetString(), out var ns))
            {
                _maxSpeakingCast = ns;
                _castCapInput = ns.ToString();
                return;
            }
        }
        _maxSpeakingCast = null;
        if (string.IsNullOrEmpty(_castCapInput))
            _castCapInput = "";
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
        _shapeError = null;
    }

    private async Task ConfirmGenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        _busy = true;
        try
        {
            try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* */ }
            // Fresh estimate before leaving
            await LoadAsync();
            if (_report is null)
            {
                _error ??= "Could not refresh the estimate. Check your connection and try again.";
                return;
            }

            _preferPath = "generate";
            await PersistPrefAsync("preferPath", "generate");
            Nav.NavigateTo(ResolveGenerateHref());
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Generate path: Film when shot plan ready; else shot plan (may run as next step); never dead-end.
    /// </summary>
    private string ResolveGenerateHref()
    {
        // B6: if shot plan missing, open shot plan as first step toward gen (not cast maze).
        if (ActiveProject.CanScenes)
            return ActiveProject.IsSimpleVoice ? "scenes?simple=1" : "scenes";
        if (ActiveProject.CanEstimate)
            return "adaptation/shots?from=decision";
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
        _shapeMessage = null;
        _shapeError = null;
        await PersistPrefAsync("editFocus", focus);
        if (focus == "craft")
        {
            // Craft stays a hub of links on EditFocus (or jump to cast)
            _phase = DecisionPhase.EditFocus;
            // stay — craft links shown when focus is craft via expanded UI
            // Actually open craft as shaping-like panel: use Shaping with craft tools
            _phase = DecisionPhase.Shaping;
            return;
        }
        _phase = DecisionPhase.Shaping;
    }

    private async Task DoneShapingAsync()
    {
        await LoadAsync();
        try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* */ }
        _phase = DecisionPhase.Card;
        _shapeMessage = "Forecast refreshed for the current plan.";
    }

    private async Task RunFitTrimAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        _busy = true;
        _shapeError = null;
        _shapeMessage = null;
        try
        {
            var result = await Engine.TrimScreenplayAsync(_projectId);
            _shapeMessage = result?.Message ?? "Screenplay fitted to target length.";
            if ((_filmRuntime?.TargetMinutes ?? 0) > 0)
                await PersistPrefAsync("lastRuntimeTargetMin", _filmRuntime!.TargetMinutes.ToString());
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _shapeError = ex.Message;
        }
        finally { _busy = false; }
    }

    private async Task SaveCastCapAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        _busy = true;
        _shapeError = null;
        _shapeMessage = null;
        try
        {
            int? value = null;
            if (!string.IsNullOrWhiteSpace(_castCapInput))
            {
                if (!int.TryParse(_castCapInput.Trim(), out var n) || n < 1 || n > 40)
                {
                    _shapeError = "Speaking cast cap must be 1–40 (or empty for default).";
                    return;
                }
                value = n;
            }
            var updates = new Dictionary<string, object?>
            {
                ["adaptation_max_speaking_cast"] = value,
            };
            await Engine.SaveConfigAsync(_projectId, updates);
            _maxSpeakingCast = value;
            _shapeMessage = value is null
                ? "Speaking cast cap cleared (project default)."
                : $"Speaking cast cap set to {value}. Rebuild cast / re-adapt for it to fully apply.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _shapeError = ex.Message;
        }
        finally { _busy = false; }
    }

    private bool ShowDurationToolkit =>
        _editFocus is "cost" or "duration" or "both" or null;

    private bool ShowCostToolkit =>
        _editFocus is "cost" or "both";

    private bool ShowCraftToolkit =>
        _editFocus == "craft";

    private string EditFocusHint(string focus) => focus switch
    {
        "cost" => "Resolution, runtime trim, and speaking-cast cap — shorter / fewer speakers usually costs less. Forecast updates here.",
        "duration" => "Set target minutes and fit the screenplay. Estimate refreshes on this page.",
        "both" => "Set runtime first, then cost levers (resolution, cast cap). One plan, two intents.",
        "craft" => "Cast looks, voices, locations, and script — then return for an updated forecast.",
        _ => "",
    };

    private string ShapingTitle => _editFocus switch
    {
        "cost" => "Shape plan · lower cost",
        "duration" => "Shape plan · runtime",
        "both" => "Shape plan · runtime & cost",
        "craft" => "Craft · cast, locations, script",
        _ => "Shape plan",
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
