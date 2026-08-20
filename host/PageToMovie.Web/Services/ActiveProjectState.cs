using System.Text.Json;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Services;

/// <summary>
/// Scoped active project for nav gating, readiness flags, and a shared load lifecycle
/// (<see cref="IsLoading"/> / <see cref="IsReady"/> / <see cref="EnsureLoadedAsync"/>).
/// </summary>
public sealed class ActiveProjectState
{
    public string? ProjectId { get; private set; }
    public string? Label { get; private set; }
    public string? ParentProjectId { get; private set; }
    public StudioPath StudioPath { get; private set; } = StudioPath.Full;
    public PageToMovie.Core.Models.AdaptationStatus? Status { get; private set; }

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectId);
    public bool IsLoading { get; private set; }
    public bool IsReady { get; private set; }
    public string? LoadError { get; private set; }

    public bool IsSimpleVoice => StudioPath == StudioPath.SimpleVoice;

    public bool CanCharacters { get; private set; }
    public bool CanScenes { get; private set; }
    public bool CanReview { get; private set; }
    public bool CanEstimate { get; private set; }

    private const string ScreenplayNotApprovedReason = "Approve the screenplay first";
    public string CharactersBlockedReason { get; private set; } = ScreenplayNotApprovedReason;
    private const string ShotPlanBlockedReason = "Finish the shot plan first";
    public string ScenesBlockedReason { get; private set; } = ShotPlanBlockedReason;
    public string ReviewBlockedReason { get; private set; } = ShotPlanBlockedReason;
    /// <summary>Film is reachable but something needs attention there (stale shot plan, cast not locked).</summary>
    public string? ScenesWarning { get; private set; }
    public string EstimateBlockedReason { get; private set; } = "Finish importing the book and approve the screenplay first";

    public event Action? Changed;

    public void Set(
        string? projectId,
        string? label = null,
        string? parentProjectId = null,
        StudioPath studioPath = StudioPath.Full)
    {
        var id = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        var lbl = string.IsNullOrWhiteSpace(label) ? id : label.Trim();
        var parentId = string.IsNullOrWhiteSpace(parentProjectId) ? null : parentProjectId.Trim();
        var path = ProjectStudioPaths.Normalize(studioPath);
        if (string.Equals(ProjectId, id, StringComparison.Ordinal)
            && string.Equals(Label, lbl, StringComparison.Ordinal)
            && string.Equals(ParentProjectId, parentId, StringComparison.Ordinal)
            && StudioPath == path)
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
                // Identity first: an identity-less /api/projects answers for the anonymous user and
                // falls back to the first project in the list — the wrong project, silently.
                await engine.EnsureSessionHydratedAsync().ConfigureAwait(false);
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
        StudioPath studioPath = StudioPath.Full,
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
    /// Stores the adaptation status and maps it into nav gate flags. Gating is read via JSON probe
    /// (tolerant of camel/Pascal casing); <see cref="Status"/> itself is set so consumers
    /// (StudioProcessStrip model badge, Home BYOK prompts, Characters book substep) see real data.
    /// </summary>
    private void ApplyFromStatusPayload(AdaptationStatus? statusObj)
    {
        Status = statusObj;
        if (statusObj is null)
        {
            ClearReadiness();
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(statusObj);
            using var doc = JsonDocument.Parse(json);
            ApplyReadinessFromJson(doc.RootElement);
        }
        catch
        {
            ClearReadiness();
        }
    }

    private void ApplyReadinessFromJson(JsonElement root)
    {
        var screenplayReady = ReadScreenplayReady(root);
        var (shotsReady, stage2Stale) = ReadStage2(root);
        var castReady = ReadCastReady(root);

        CanCharacters = screenplayReady;
        CharactersBlockedReason = screenplayReady ? "" : ScreenplayNotApprovedReason;
        // Film opens on approval: in the Manual workflow the shot plan is built FROM Film, so gating
        // it on the plan would lock the user out of the page that builds it. Stale plan / cast not
        // ready are surfaced as a warning on the step (ScenesWarning), not a block.
        CanScenes = screenplayReady;
        ScenesBlockedReason = screenplayReady ? "" : ScreenplayNotApprovedReason;
        ScenesWarning = ScenesWarningFor(screenplayReady, shotsReady, stage2Stale, castReady);
        // Review needs something to review: a current (non-stale) shot plan.
        CanReview = screenplayReady && shotsReady && !stage2Stale;
        ReviewBlockedReason = ReviewBlockedReasonFor(screenplayReady, shotsReady, stage2Stale);
        CanEstimate = screenplayReady;
        EstimateBlockedReason = screenplayReady
            ? ""
            : "Finish importing the book and approve the screenplay first";
    }

    private static (bool ShotsReady, bool Stage2Stale) ReadStage2(JsonElement root)
    {
        var shotsReady = false;
        var stage2Stale = false;
        if (TryGetCamelOrPascal(root, "stage2", "Stage2", out var s2))
        {
            shotsReady = PropBool(s2, "stage2Ready", "Stage2Ready")
                && PropInt(s2, "stage2Clips", "Stage2Clips") > 0;
            stage2Stale = PropBool(s2, "stage2Stale", "Stage2Stale");
        }
        return (shotsReady, stage2Stale);
    }

    private static bool ReadCastReady(JsonElement root)
    {
        var castReady = true;
        if (TryGetCamelOrPascal(root, "cast", "Cast", out var ca))
            castReady = PropBool(ca, "readyForShots", "ReadyForShots");
        return castReady;
    }

    private static string ReviewBlockedReasonFor(bool screenplayReady, bool shotsReady, bool stage2Stale)
    {
        if (!screenplayReady) return ScreenplayNotApprovedReason;
        if (!shotsReady) return "Build the shot plan first";
        if (stage2Stale) return "Update the shot plan first";
        return "";
    }

    /// <summary>Non-blocking heads-up shown on the Film step (null when nothing to warn about).</summary>
    internal static string? ScenesWarningFor(bool screenplayReady, bool shotsReady, bool stage2Stale, bool castReady)
    {
        if (!screenplayReady) return null;
        if (shotsReady && stage2Stale) return "Screenplay changed — update the shot plan before making video";
        if (shotsReady && !castReady) return "Lock every character look + voice before generating video";
        return null;
    }

    private static bool ReadScreenplayReady(JsonElement root)
    {
        var screenplayReady = false;
        if (TryGetCamelOrPascal(root, "screenplay", "Screenplay", out var sp))
        {
            screenplayReady = PropBool(sp, "readyForShots", "ReadyForShots")
                || PropBool(sp, "signed", "Signed");
        }
        if (TryGetCamelOrPascal(root, "stage1", "Stage1", out var s1)
            && PropBool(s1, "present", "Present") && PropInt(s1, "sceneCount", "SceneCount") > 0)
            screenplayReady = true;
        return screenplayReady;
    }

    private static bool TryGetCamelOrPascal(JsonElement el, string camel, string pascal, out JsonElement value)
    {
        if (el.TryGetProperty(camel, out value)) return true;
        if (el.TryGetProperty(pascal, out value)) return true;
        value = default;
        return false;
    }

    private static bool PropBool(JsonElement el, string camel, string pascal) =>
        TryPropBool(el, camel) ?? TryPropBool(el, pascal) ?? false;

    private static bool? TryPropBool(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return v.GetBoolean();
        return null;
    }

    private static int PropInt(JsonElement el, string camel, string pascal) =>
        TryPropInt(el, camel) ?? TryPropInt(el, pascal) ?? 0;

    private static int? TryPropInt(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)) return i;
        return null;
    }

    private void ClearReadiness()
    {
        Status = null;
        CanCharacters = false;
        CanScenes = false;
        CanReview = false;
        CanEstimate = false;
        CharactersBlockedReason = ScreenplayNotApprovedReason;
        ScenesBlockedReason = ShotPlanBlockedReason;
        ReviewBlockedReason = ShotPlanBlockedReason;
        ScenesWarning = null;
        EstimateBlockedReason = "Finish importing the book and approve the screenplay first";
    }
}
