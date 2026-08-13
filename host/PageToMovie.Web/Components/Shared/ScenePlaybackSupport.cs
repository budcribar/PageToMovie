using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components;

/// <summary>
/// Shared page plumbing for the two per-scene audio review pages (Dialogue timing, Voice capture):
/// the identical film-picker/active-project resolution run in OnInitialized, and the identical
/// stitch-and-cache of a scene's playable URL.
/// </summary>
internal static class ScenePlaybackSupport
{
    /// <summary>
    /// Hydrates the session, loads the film list, and resolves the starting project id (active
    /// client/server project, else the first film). Returns the picker list, the chosen id, and any
    /// project-load error (null on success).
    /// </summary>
    public static async Task<(List<ProjectInfo> Projects, string? ProjectId, string? Error)> ResolveProjectSelectionAsync(
        AdminSessionService session, EngineApiClient engine, ActiveProjectState activeProject)
    {
        string? error = null;
        List<ProjectInfo> projects = new();

        // Ensure the login session is loaded before any identity-gated call (direct URL loads can
        // reach here before hydration, which made /api/projects come back empty).
        try { await session.EnsureHydratedAsync(); } catch { /* ignore */ }

        try { var pr = await engine.GetProjectsAsync(); projects = pr?.Projects ?? new(); }
        catch (Exception ex) { error = ex.Message; }

        var projectId = activeProject.ProjectId;
        if (string.IsNullOrEmpty(projectId))
        {
            try { await activeProject.RefreshFromServerAsync(engine); } catch { /* ignore */ }
            projectId = activeProject.ProjectId;
        }
        if (string.IsNullOrEmpty(projectId) && projects.Count > 0)
            projectId = projects[0].Id;

        return (projects, projectId, error);
    }

    /// <summary>
    /// Returns a single playable URL for a scene — the lone clip when there is one, else the stitched
    /// concat — caching the result per scene so a scene is only stitched once. Null when the scene has
    /// no clips or the stitch fails.
    /// </summary>
    public static async Task<string?> GetSceneUrlAsync(
        ClientVideoStitchService stitch, string projectId, int scene, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(scene, out var cached)) return cached;
        var clipUrls = await stitch.CollectClipUrlsAsync(projectId, scene);
        if (clipUrls.Count == 0) return null;
        string url;
        if (clipUrls.Count == 1) url = clipUrls[0];
        else
        {
            var stitched = await stitch.ConcatAsync(clipUrls);
            var stitchedUrl = stitched.Url;
            if (!stitched.Success || string.IsNullOrWhiteSpace(stitchedUrl)) return null;
            url = stitchedUrl;
        }
        cache[scene] = url;
        return url;
    }
}
