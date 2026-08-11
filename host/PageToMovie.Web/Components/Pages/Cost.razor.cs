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

    // I1–I4 multi-user / job guards
    private enum CollabRole { Unknown, Viewer, Editor, Owner }
    private CollabRole _collabRole = CollabRole.Unknown;
    private string? _scriptLeaseHolder;
    private string? _blockingJobKind;
    private string? _blockingJobId;
    private string? _collabNote;
    private double? _confirmEstimateSnapshot;
    private long? _lastPlanRev;
    private bool _collabHubHooked;

    [Inject] private IJSRuntime Js { get; set; } = null!;
    [Inject] private ProjectCollabHubClient CollabHub { get; set; } = null!;

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
            await EnsureCollabHubAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    /// <summary>I12: join project hub and re-load estimate when a collaborator PlanDirties.</summary>
    private async Task EnsureCollabHubAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId) || _disposed) return;
        try
        {
            if (!_collabHubHooked)
            {
                CollabHub.PlanDirty += OnPlanDirty;
                CollabHub.LeaseChanged += OnLeaseChanged;
                _collabHubHooked = true;
            }
            await CollabHub.EnsureJoinedAsync(_projectId, _pageCts.Token);
            _lastPlanRev = await Engine.GetProjectRevAsync(_projectId, _pageCts.Token);
        }
        catch { /* soft */ }
    }

    private void OnPlanDirty(string projectId, long rev, string? byUser)
    {
        if (_disposed) return;
        if (!string.Equals(projectId, _projectId, StringComparison.OrdinalIgnoreCase)) return;
        if (_lastPlanRev is long prev && rev <= prev) return;
        _lastPlanRev = rev;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            var who = string.IsNullOrWhiteSpace(byUser) ? "a collaborator" : byUser;
            _collabNote = $"Plan updated by {who} — refreshing estimate…";
            try { await LoadAsync(); }
            catch { /* */ }
            StateHasChanged();
        });
    }

    private void OnLeaseChanged(string projectId, string resource, string? holder)
    {
        if (_disposed) return;
        if (!string.Equals(projectId, _projectId, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(resource, "script", StringComparison.OrdinalIgnoreCase)
            && resource != "*") return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            try { await LoadCollabGuardsAsync(); } catch { /* */ }
            StateHasChanged();
        });
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
        try { await LoadCollabGuardsAsync(); } catch { /* optional */ }
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

    private bool IsOwner => _collabRole is CollabRole.Owner or CollabRole.Unknown;
    private bool IsEditorOrAbove => _collabRole is CollabRole.Owner or CollabRole.Editor or CollabRole.Unknown;
    private bool IsViewerOnly => _collabRole == CollabRole.Viewer;
    private bool PlanBusy =>
        !string.IsNullOrWhiteSpace(_scriptLeaseHolder)
        && !string.Equals(_scriptLeaseHolder, Session.UserId, StringComparison.OrdinalIgnoreCase);
    private bool GeneratingBusy => !string.IsNullOrWhiteSpace(_blockingJobKind);
    private bool CanStartFullMovie =>
        IsOwner && ActiveProject.CanEstimate && !PlanBusy && !GeneratingBusy && !IsViewerOnly;

    private async Task LoadCollabGuardsAsync()
    {
        _scriptLeaseHolder = null;
        _blockingJobKind = null;
        _blockingJobId = null;
        _collabNote = null;
        _collabRole = CollabRole.Unknown;
        if (string.IsNullOrWhiteSpace(_projectId)) return;

        var uid = (Session.UserId ?? "").Trim();
        if (string.IsNullOrEmpty(uid)) uid = "local";

        var acl = await Engine.GetProjectAclAsync(_projectId);
        if (acl is not null)
        {
            if (string.Equals(acl.OwnerUserId, uid, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(acl.OwnerUserId))
                _collabRole = CollabRole.Owner;
            else if (acl.Editors.Any(e => string.Equals(e, uid, StringComparison.OrdinalIgnoreCase)))
                _collabRole = CollabRole.Editor;
            else if (acl.Viewers.Any(v => string.Equals(v, uid, StringComparison.OrdinalIgnoreCase)))
                _collabRole = CollabRole.Viewer;
            else
                // Shared list membership may lag; treat as viewer-safe default if not listed
                _collabRole = string.IsNullOrWhiteSpace(acl.OwnerUserId) ? CollabRole.Owner : CollabRole.Viewer;
        }
        else
        {
            // Solo / ACL unavailable — full Owner powers (I1 soft fail-open)
            _collabRole = CollabRole.Owner;
        }

        // I3: script / plan lease
        var scriptLease = await Engine.GetProjectLeaseAsync(_projectId, "script");
        if (scriptLease is not null
            && scriptLease.ExpiresAt > DateTimeOffset.UtcNow
            && !string.IsNullOrWhiteSpace(scriptLease.HolderUserId))
        {
            _scriptLeaseHolder = scriptLease.HolderUserId;
        }

        // I2: job service — any active full-film-ish job on this project
        var jobs = await Engine.GetJobsAsync(mine: false, projectId: _projectId);
        var active = jobs?.Jobs?
            .Where(j =>
            {
                var st = (j.Status ?? "").Trim().ToLowerInvariant();
                if (st is not ("running" or "queued")) return false;
                var k = (j.Kind ?? "").Trim().ToLowerInvariant();
                // Full-movie / pipeline blockers (not single-scene edits)
                return k is "batch" or "stage2" or "stage1" or "book_import" or "book_prepare"
                    or "cast-extract" or "speak-batch";
            })
            .OrderByDescending(j => j.StartedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (active is not null)
        {
            _blockingJobKind = active.Kind ?? "job";
            _blockingJobId = active.JobId;
        }
    }

    private async Task ChooseGenerate()
    {
        if (IsViewerOnly)
        {
            _collabNote = "Viewers can watch estimates but cannot generate. Ask the project owner for Editor access.";
            return;
        }
        if (!IsOwner)
        {
            // I1: Editors generate scenes on Film, not whole movie from DecisionCard
            _preferPath = "generate";
            await PersistPrefAsync("preferPath", "generate");
            _collabNote = "Editors generate individual scenes on Film — only the Owner starts a full-movie pass from Estimate.";
            Nav.NavigateTo(ActiveProject.CanScenes
                ? (ActiveProject.IsSimpleVoice ? "scenes?simple=1" : "scenes")
                : "adaptation/shots?from=decision");
            return;
        }

        _preferPath = "generate";
        await PersistPrefAsync("preferPath", "generate");
        _busy = true;
        try
        {
            await LoadAsync(); // I4 re-fetch estimate + collab
            _confirmEstimateSnapshot = DisplayEstimateUsd;
            _phase = DecisionPhase.ConfirmGenerate;
        }
        finally { _busy = false; }
    }

    private void ChooseEdit()
    {
        if (IsViewerOnly)
        {
            _collabNote = "Viewers cannot edit the plan.";
            return;
        }
        _preferPath = "edit";
        _ = PersistPrefAsync("preferPath", "edit");
        _phase = DecisionPhase.EditFocus;
        _collabNote = null;
    }

    private void BackToCard()
    {
        _phase = DecisionPhase.Card;
        _shapeError = null;
        _collabNote = null;
    }

    private async Task ConfirmGenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        if (!IsOwner)
        {
            _collabNote = "Only the project Owner can start a full-movie generate from Estimate.";
            return;
        }
        _busy = true;
        try
        {
            try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* */ }
            // I4: always re-fetch estimate before navigating
            await LoadAsync();
            if (_report is null)
            {
                _error ??= "Could not refresh the estimate. Check your connection and try again.";
                return;
            }
            _confirmEstimateSnapshot = DisplayEstimateUsd;

            // I3 PlanBusy
            if (PlanBusy)
            {
                _collabNote = $"PlanBusy: {_scriptLeaseHolder} is editing the screenplay/plan. Wait until they finish, then try again. You can still generate unlocked scenes on Film.";
                return;
            }
            // I2 GeneratingBusy
            if (GeneratingBusy)
            {
                _collabNote = $"GeneratingBusy: a {_blockingJobKind} job is already running on this project. Monitor it instead of starting another full pass.";
                return;
            }

            _preferPath = "generate";
            await PersistPrefAsync("preferPath", "generate");

            // F1/F4: when shot plan ready, start resumable fill-holes batch then open Film (F2 watch)
            if (ActiveProject.CanScenes && !GeneratingBusy)
            {
                try
                {
                    var scenesDto = await Engine.GetScenesAsync(_projectId);
                    var nums = scenesDto?.Scenes?
                        .Where(s => !s.IsCredits && s.ClipCount > 0)
                        .Select(s => s.SceneNumber)
                        .OrderBy(n => n)
                        .ToList() ?? new List<int>();
                    if (nums.Count > 0)
                    {
                        await Engine.StartBatchGenAsync(_projectId, nums, onlyMissing: true, resolution: _draftRes);
                        Nav.NavigateTo(ActiveProject.IsSimpleVoice ? "scenes?simple=1&watch=1" : "scenes?watch=1");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Fall through to navigate without auto-start
                    _collabNote = "Could not auto-start generate: " + ex.Message + " — open Film to generate.";
                }
            }

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

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_collabHubHooked)
        {
            try { CollabHub.PlanDirty -= OnPlanDirty; } catch { /* */ }
            try { CollabHub.LeaseChanged -= OnLeaseChanged; } catch { /* */ }
            _collabHubHooked = false;
        }
        try { await CollabHub.LeaveAsync(); } catch { /* */ }
        try { _pageCts.Cancel(); } catch { /* */ }
        try { _pageCts.Dispose(); } catch { /* */ }
    }
}
