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

    public string CharactersBlockedReason { get; private set; } = "Approve the screenplay first";
    public string ScenesBlockedReason { get; private set; } = "Finish the shot plan first";
    public string ReviewBlockedReason { get; private set; } = "Finish the shot plan first";
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
            var json = System.Text.Json.JsonSerializer.Serialize(statusObj);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            static bool PropBool(System.Text.Json.JsonElement el, string camel, string pascal)
            {
                if (el.TryGetProperty(camel, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    return v.GetBoolean();
                if (el.TryGetProperty(pascal, out v) && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    return v.GetBoolean();
                return false;
            }

            static int PropInt(System.Text.Json.JsonElement el, string camel, string pascal)
            {
                if (el.TryGetProperty(camel, out var v) && v.TryGetInt32(out var i)) return i;
                if (el.TryGetProperty(pascal, out v) && v.TryGetInt32(out i)) return i;
                return 0;
            }

            var screenplayReady = false;
            if (root.TryGetProperty("screenplay", out var sp) || root.TryGetProperty("Screenplay", out sp))
            {
                screenplayReady = PropBool(sp, "readyForShots", "ReadyForShots")
                    || PropBool(sp, "signed", "Signed");
            }
            if ((root.TryGetProperty("stage1", out var s1) || root.TryGetProperty("Stage1", out s1))
                && PropBool(s1, "present", "Present") && PropInt(s1, "sceneCount", "SceneCount") > 0)
                screenplayReady = true;

            var shotsReady = false;
            var stage2Stale = false;
            if (root.TryGetProperty("stage2", out var s2) || root.TryGetProperty("Stage2", out s2))
            {
                shotsReady = PropBool(s2, "stage2Ready", "Stage2Ready")
                    && PropInt(s2, "stage2Clips", "Stage2Clips") > 0;
                stage2Stale = PropBool(s2, "stage2Stale", "Stage2Stale");
            }

            var castReady = true;
            if (root.TryGetProperty("cast", out var ca) || root.TryGetProperty("Cast", out ca))
                castReady = PropBool(ca, "readyForShots", "ReadyForShots");

            CanCharacters = screenplayReady;
            CharactersBlockedReason = screenplayReady ? "" : "Approve the screenplay first";
            CanScenes = shotsReady;
            CanReview = shotsReady;
            CanEstimate = screenplayReady;
            EstimateBlockedReason = screenplayReady
                ? ""
                : "Finish importing the book and approve the screenplay first";

            if (shotsReady)
                ScenesBlockedReason = castReady
                    ? ""
                    : "Approve every character voice + locked image before generating video";
            else if (stage2Stale)
                ScenesBlockedReason = "Update the shot plan first";
            else if (!castReady)
                ScenesBlockedReason = "Finish characters (voice + locked image), then the shot plan";
            else
                ScenesBlockedReason = "Finish the shot plan first";
            ReviewBlockedReason = ScenesBlockedReason;
        }
        catch
        {
            ClearReadiness();
        }
    }

    private void ClearReadiness()
    {
        Status = null;
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
