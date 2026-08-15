using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components;

/// <summary>
/// Shared OnInitialized project-list + active-project hydration used by Characters and Scenes.
/// </summary>
internal static class StudioPageBootstrap
{
    public readonly record struct LoadResult(string ProjectId, List<string> ProjectIds);

    /// <summary>
    /// Hydrate the session (best-effort), load the project list, pick the active project when it is
    /// in that list (else the first id), refresh readiness, then refresh capabilities. Invokes
    /// <paramref name="markGateChecked"/> after readiness and before capabilities so the original
    /// Characters/Scenes order is preserved.
    /// </summary>
    public static async Task<LoadResult> LoadActiveProjectAsync(
        EngineApiClient engine,
        AdminSessionService session,
        ActiveProjectState activeProject,
        StudioCapabilityState caps,
        Action markGateChecked)
    {
        try { await session.EnsureHydratedAsync(); } catch { /* optional */ }

        var projs = await engine.GetProjectsAsync();
        var projectIds = projs?.Projects.Select(p => p.Id ?? "").Where(s => s.Length > 0).ToList()
                         ?? new List<string>();
        string projectId;
        if (projs?.Active?.Id is { Length: > 0 } aid &&
            projectIds.Exists(id => string.Equals(id, aid, StringComparison.OrdinalIgnoreCase)))
        {
            projectId = aid;
            activeProject.Set(
                aid,
                projs.Active?.Label ?? projs.Active?.Title ?? aid,
                parentProjectId: projs.Active?.ParentProjectId,
                studioPath: projs.Active?.StudioPath ?? StudioPath.Full);
        }
        else if (projectIds.Count > 0)
            projectId = projectIds[0];
        else
            projectId = "";

        await activeProject.RefreshReadinessAsync(engine);
        markGateChecked();
        if (!string.IsNullOrEmpty(projectId))
            await caps.RefreshAsync(engine);

        return new LoadResult(projectId, projectIds);
    }
}
