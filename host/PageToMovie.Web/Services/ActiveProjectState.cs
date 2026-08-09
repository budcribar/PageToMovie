using PageToMovie.Core.Models;

namespace PageToMovie.Web.Services;

/// <summary>
/// Scoped active project for nav gating, readiness flags, and a shared load lifecycle
/// (<see cref="IsLoading"/> / <see cref="IsReady"/> / <see cref="EnsureLoadedAsync"/>).
/// Nav gates are evaluated by <see cref="StudioStateMachine"/> (single source of truth).
/// </summary>
public sealed class ActiveProjectState
{
    public string? ProjectId { get; private set; }
    public string? Label { get; private set; }
    public string? ParentProjectId { get; private set; }
    public string StudioPath { get; private set; } = ProjectStudioPaths.Full;
    public AdaptationStatus? Status { get; private set; }

    /// <summary>Latest derived pipeline phase from <see cref="StudioStateMachine.DeterminePhase"/>.</summary>
    public StudioPhase CurrentPhase { get; private set; } = StudioPhase.ImportRequired;

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectId);
    public bool IsLoading { get; private set; }
    public bool IsReady { get; private set; }
    public string? LoadError { get; private set; }

    public bool IsSimpleVoice => ProjectStudioPaths.IsSimpleVoice(StudioPath);

    public bool CanCharacters { get; private set; }
    public bool CanScenes { get; private set; }
    public bool CanReview { get; private set; }
    public bool CanEstimate { get; private set; }

    public string CharactersBlockedReason { get; private set; } = "Approve the screenplay first";
    public string ScenesBlockedReason { get; private set; } = "Finish the shot plan first";
    public string ReviewBlockedReason { get; private set; } = "Finish the shot plan first";
    public string EstimateBlockedReason { get; private set; } = "Finish importing the book and approve the screenplay first";

    public event Action? Changed;

    public void Set(
        string? projectId,
        string? label = null,
        string? parentProjectId = null,
        string? studioPath = null)
    {
        var id = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        var lbl = string.IsNullOrWhiteSpace(label) ? id : label.Trim();
        var parentId = string.IsNullOrWhiteSpace(parentProjectId) ? null : parentProjectId.Trim();
        var path = ProjectStudioPaths.Normalize(studioPath);
        if (string.Equals(ProjectId, id, StringComparison.Ordinal)
            && string.Equals(Label, lbl, StringComparison.Ordinal)
            && string.Equals(ParentProjectId, parentId, StringComparison.Ordinal)
            && string.Equals(StudioPath, path, StringComparison.Ordinal))
            return;

        var projectChanged = !string.Equals(ProjectId, id, StringComparison.Ordinal);
        ProjectId = id;
        Label = lbl;
        ParentProjectId = parentId;
        StudioPath = path;
        if (id is null)
        {
            ClearReadiness();
            IsLoading = false;
            IsReady = false;
            LoadError = null;
        }
        else if (projectChanged)
        {
            IsReady = false;
            LoadError = null;
            ClearReadiness();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        ProjectId = null;
        Label = null;
        ParentProjectId = null;
        StudioPath = ProjectStudioPaths.Full;
        IsLoading = false;
        IsReady = false;
        LoadError = null;
        ClearReadiness();
        Changed?.Invoke();
    }

    public async Task RefreshFromServerAsync(EngineApiClient engine, CancellationToken ct = default)
    {
        try
        {
            // Hydrate the active-project pointer from the workspace when it isn't set yet. Direct
            // navigation to a studio step (e.g. /adaptation/shots) or a page refresh lands with no
            // project selected; EnsureLoadedAsync only refreshes readiness for an already-selected
            // project, so without this the page shows "No project selected" though one is active.
            if (!HasProject)
            {
                var projs = await engine.GetProjectsAsync(ct).ConfigureAwait(false);
                var active = projs?.Active;
                if (active?.Id is { Length: > 0 } id)
                    Set(id, active.Label ?? active.Title ?? id);
            }
            await EnsureLoadedAsync(engine, force: true, ct: ct).ConfigureAwait(false);
        }
        catch
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The one way to switch the active project: persist the choice on the server, update local
    /// state, and load its readiness. Callers that pick a project (project dropdown, "open story"
    /// fork, post-create) should use this instead of hand-rolling ActivateProjectAsync + Set +
    /// RefreshReadinessAsync — that drift is how the active project ended up set inconsistently.
    /// Pass the metadata you already have (label/parent/studioPath); nulls fall back to the id.
    /// </summary>
    public async Task SelectAsync(
        EngineApiClient engine,
        string projectId,
        string? label = null,
        string? parentProjectId = null,
        string? studioPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        var id = projectId.Trim();
        await engine.ActivateProjectAsync(id, ct).ConfigureAwait(false);
        Set(id, label, parentProjectId, studioPath);
        await RefreshReadinessAsync(engine, ct).ConfigureAwait(false);
    }

    public async Task RefreshReadinessAsync(EngineApiClient engine, CancellationToken ct = default)
    {
        if (!HasProject || ProjectId is null)
        {
            ClearReadiness();
            IsReady = false;
            Changed?.Invoke();
            return;
        }

        try
        {
            var dto = await engine.GetAdaptationAsync(ProjectId, ct);
            // The endpoint returns the AdaptationDto wrapper ({ ok, projectId, adaptation }); the
            // readiness flags + Status live on the nested .Adaptation, not the wrapper root.
            ApplyFromStatusPayload(dto?.Adaptation);
            IsReady = true;
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }

        Changed?.Invoke();
    }

    public async Task EnsureLoadedAsync(EngineApiClient engine, bool force = false, CancellationToken ct = default)
    {
        if (!HasProject || ProjectId is null)
        {
            IsLoading = false;
            IsReady = false;
            LoadError = null;
            Changed?.Invoke();
            return;
        }

        if (IsReady && !force && !IsLoading)
            return;

        if (IsLoading && !force)
        {
            for (var i = 0; i < 50 && IsLoading; i++)
                await Task.Delay(50, ct).ConfigureAwait(false);
            if (IsReady)
                return;
        }

        IsLoading = true;
        LoadError = null;
        Changed?.Invoke();
        try
        {
            await RefreshReadinessAsync(engine, ct).ConfigureAwait(false);
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Stores the adaptation status and maps it into nav gate flags via
    /// <see cref="StudioStateMachine"/> (phase + <see cref="StudioStateMachine.CanNavigateTo"/>).
    /// </summary>
    private void ApplyFromStatusPayload(AdaptationStatus? statusObj)
    {
        Status = statusObj;
        if (statusObj is null)
        {
            ClearReadiness();
            return;
        }

        var phase = StudioStateMachine.DeterminePhase(statusObj);
        CurrentPhase = phase;

        var castReady = statusObj.Cast.ReadyForShots;
        var stage2Stale = statusObj.Stage2.Stage2Stale;

        var cast = StudioStateMachine.CanNavigateTo(StudioStep.Cast, phase);
        CanCharacters = cast.Allowed;
        CharactersBlockedReason = cast.Allowed ? "" : cast.BlockedReason;

        var estimate = StudioStateMachine.CanNavigateTo(StudioStep.Estimate, phase);
        CanEstimate = estimate.Allowed;
        EstimateBlockedReason = estimate.Allowed ? "" : estimate.BlockedReason;

        var film = StudioStateMachine.CanNavigateTo(
            StudioStep.Film, phase, castReady: castReady, isStage2Stale: stage2Stale);
        CanScenes = film.Allowed;
        ScenesBlockedReason = film.Allowed ? "" : film.BlockedReason;

        var review = StudioStateMachine.CanNavigateTo(
            StudioStep.Review, phase, castReady: castReady, isStage2Stale: stage2Stale);
        CanReview = review.Allowed;
        ReviewBlockedReason = review.Allowed ? "" : review.BlockedReason;
    }

    private void ClearReadiness()
    {
        Status = null;
        CurrentPhase = StudioPhase.ImportRequired;
        CanCharacters = false;
        CanScenes = false;
        CanReview = false;
        CanEstimate = false;
        CharactersBlockedReason = "Approve the screenplay first";
        ScenesBlockedReason = "Finish the shot plan first";
        ReviewBlockedReason = "Finish the shot plan first";
        EstimateBlockedReason = "Finish importing the book and approve the screenplay first";
    }
}
