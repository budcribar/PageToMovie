using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Api;

public static class GitVersionEndpoints
{
    public static IEndpointRouteBuilder MapGitVersionEndpoints(this IEndpointRouteBuilder app)
    {
        // <summary>
        // Manually commit a project's current text/metadata state to its own Git repository
        // (owner or admin only). Not called automatically on every edit — see host/docs/issues for
        // why auto-commit-on-save needs a decision about where user projects live relative to any
        // Git repo the app itself is checked out into before it's safe to wire in as a background hook.
        // </summary>
        app.MapPost("/api/projects/{id}/commit", PostProjectsIdCommit);
        app.MapGet("/api/projects/{id}/git/history", GetProjectsIdGitHistory);
        app.MapPost("/api/projects/{id}/git/undo", PostProjectsIdGitUndo);
        app.MapPost("/api/projects/{id}/git/revert/{commitHash}", PostProjectsIdGitRevertCommitHash);
        app.MapGet("/api/projects/{id}/git/status", GetProjectsIdGitStatus);
        app.MapPost("/api/projects/{id}/git/commit", PostProjectsIdGitCommit);
        app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions", GetProjectsIdScenesSceneClipsClipVersions);
        // <summary>
        // Whether the *active* clip file's bytes currently live on server disk, are only registered as
        // synced to the client, or both — with the registered sha256/size. Lets the client decide
        // whether a local blob it already has is still current before trusting it for playback, instead
        // of assuming "file exists locally" means "file is current" (it may be an older take that was
        // never overwritten locally after a later regen/promote).
        // </summary>
        app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/media-status", GetProjectsIdScenesSceneClipsClipMediaStatus);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/{versionId}/promote", PostProjectsIdScenesSceneClipsClipVersionsVersionIdPromote);
        app.MapDelete("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/{versionId}", DeleteProjectsIdScenesSceneClipsClipVersionsVersionId);
        app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/trash", GetProjectsIdScenesSceneClipsClipVersionsTrash);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/{versionId}/restore", PostProjectsIdScenesSceneClipsClipVersionsVersionIdRestore);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/trash/empty", PostProjectsIdScenesSceneClipsClipVersionsTrashEmpty);
        // Scene audio takes — mirrors the clip-versions endpoints above (GetMusicVersionsAsync etc.),
        // keyed by scene + takeId instead of scene/clip + versionId since one take is a group of segments.
        app.MapGet("/api/projects/{id}/scenes/{scene:int}/music-versions", GetProjectsIdScenesSceneMusicVersions);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/music-versions/{takeId}/promote", PostProjectsIdScenesSceneMusicVersionsTakeIdPromote);
        app.MapDelete("/api/projects/{id}/scenes/{scene:int}/music-versions/{takeId}", DeleteProjectsIdScenesSceneMusicVersionsTakeId);
        app.MapGet("/api/projects/{id}/scenes/{scene:int}/music-versions/trash", GetProjectsIdScenesSceneMusicVersionsTrash);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/music-versions/{takeId}/restore", PostProjectsIdScenesSceneMusicVersionsTakeIdRestore);
        // <summary>
        // Push the project's text package (video excluded) to the configured Projects remote.
        // Owner/admin only. Optional body.commitFirst + message creates a local commit first.
        // Returns historyUrl when the remote is GitHub. See docs/archive/github-projects-backup-checklist.md.
        // </summary>
        app.MapPost("/api/projects/{id}/push", PostProjectsIdPush);
        // <summary>
        // Merge another project's committed state into this one (owner or admin of the target project).
        // Real LibGit2Sharp 3-way merge — reports <c>hasConflicts</c> rather than auto-resolving; the
        // caller must inspect and commit manually when that happens (no conflict-resolution UI yet).
        // </summary>
        app.MapPost("/api/projects/{id}/sync-origin", PostProjectsIdSyncOrigin);
        // <summary>
        // Computes structured visual diffs between a project and its origin parent project.
        // </summary>
        app.MapGet("/api/projects/{id}/contribution-diff", GetProjectsIdContributionDiff);
        app.MapPost("/api/projects/{id}/contribution-sync-media", PostProjectsIdContributionSyncMedia);
        app.MapGet("/api/projects/{projectId}/scenes/{sceneKey}/versions", GetProjectsProjectIdScenesSceneKeyVersions);
        app.MapPost("/api/projects/{projectId}/scenes/{sceneKey}/versions", PostProjectsProjectIdScenesSceneKeyVersions);
        app.MapPost("/api/projects/{projectId}/scenes/{sceneKey}/versions/{versionId}/restore", PostProjectsProjectIdScenesSceneKeyVersionsVersionIdRestore);
        return app;
    }

    private static async Task<IResult> PostProjectsIdCommit(string id,
    CommitProjectApiRequest? body,
    ProjectStore store,
    ProjectGitRepositoryService git,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can commit it.", ct) is { } forbidden)
            return forbidden;

        var info = await git.CommitProjectStateAsync(
            await store.GetProjectDirAsync(id, ct), user.UserId ?? "PageToMovie", body?.Message ?? "Project update");
        return Results.Ok(new { ok = true, commit = info });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdGitHistory(string id,
    int? limit,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var history = await store.GetProjectGitHistoryAsync(id, limit ?? 20);
        return Results.Ok(new { ok = true, history });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdGitUndo(string id,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can undo project changes.", ct) is { } forbidden)
            return forbidden;

        var result = await store.UndoLastProjectChangeAsync(id, user.UserId);
        if (result is null)
        {
            return Results.BadRequest(new { ok = false, error = "No prior commit to undo to." });
        }
        return Results.Ok(new { ok = true, commit = result, message = "Successfully reverted project to previous commit state." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdGitRevertCommitHash(string id,
    string commitHash,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can revert project state.", ct) is { } forbidden)
            return forbidden;

        var result = await store.RevertProjectToCommitAsync(id, commitHash, user.UserId);
        if (result is null)
        {
            return Results.BadRequest(new { ok = false, error = $"Failed to revert to commit {commitHash}." });
        }
        return Results.Ok(new { ok = true, commit = result, message = $"Successfully reverted project to commit {commitHash[..Math.Min(8, commitHash.Length)]}." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdGitStatus(string id,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var status = await store.GetProjectUncommittedStatusAsync(id);
        return Results.Ok(new { ok = true, status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdGitCommit(string id,
    CommitProjectApiRequest? body,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var msg = body?.Message ?? "Manual scene/clip updates";
        var result = await store.CommitProjectChangesAsync(id, msg, user.UserId, forceCommit: body?.ForceCommit ?? false);
        return Results.Ok(new { ok = true, commit = result, message = "Successfully committed project changes." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScenesSceneClipsClipVersions(string id,
    int scene,
    int clip,
    ProjectStore store,
    MediaProxyTicketStore tickets,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetClipVersionsAsync(id, scene, clip);
        // A take that lives only at the provider gets a short-lived proxy URL the browser can play
        // (and slice, when the provider copy is a combined extend video).
        var videoDir = Path.Combine(await store.GetProjectDirAsync(id, ct), "assets", "video");
        foreach (var v in versions)
        {
            if (string.IsNullOrWhiteSpace(v.SourceUrl) && string.IsNullOrWhiteSpace(v.SourceFileId))
                continue;
            var onServer = File.Exists(Path.Combine(videoDir, v.Mp4FileName)) || File.Exists(Path.Combine(videoDir, "history", v.Mp4FileName));
            if (onServer || v.ClientOnly) continue;
            v.ProviderPlaybackUrl = $"/api/media/proxy/{tickets.Issue(v.SourceUrl ?? "", TimeSpan.FromMinutes(45), v.SourceFileId)}";
        }
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScenesSceneClipsClipMediaStatus(string id,
    int scene,
    int clip,
    ProjectStore store,
    MediaSyncLocator locator,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var status = await locator.GetClipStatusAsync(id, await store.GetProjectDirAsync(id, ct), scene, clip, ct);
        return Results.Ok(new
        {
            ok = true,
            onServer = status.OnServer,
            onClient = status.OnClient,
            sha256 = status.Sha256,
            clientSizeBytes = status.ClientSizeBytes,
            serverSizeBytes = status.ServerSizeBytes,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneClipsClipVersionsVersionIdPromote(string id,
    int scene,
    int clip,
    string versionId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
        await ApiEndpointHelpers.RunProjectVersionActionAsync(id, store, user, opts,
        () => store.PromoteClipVersionAsync(id, scene, clip, versionId, user.UserId),
        "Failed to promote clip version.",
        $"Promoted clip version {versionId} to active clip.", ct);

    private static async Task<IResult> DeleteProjectsIdScenesSceneClipsClipVersionsVersionId(string id,
    int scene,
    int clip,
    string versionId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
        await ApiEndpointHelpers.RunProjectVersionActionAsync(id, store, user, opts,
        () => store.SoftDeleteClipVersionAsync(id, scene, clip, versionId),
        "Failed to delete clip version.",
        $"Soft-deleted clip version {versionId}.", ct);

    private static async Task<IResult> GetProjectsIdScenesSceneClipsClipVersionsTrash(string id,
    int scene,
    int clip,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetTrashClipVersionsAsync(id, scene, clip);
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneClipsClipVersionsVersionIdRestore(string id,
    int scene,
    int clip,
    string versionId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
        await ApiEndpointHelpers.RunProjectVersionActionAsync(id, store, user, opts,
        () => store.RestoreSoftDeletedClipVersionAsync(id, scene, clip, versionId),
        "Failed to restore clip version from trash.",
        $"Restored clip version {versionId} from trash.", ct);

    private static async Task<IResult> PostProjectsIdScenesSceneClipsClipVersionsTrashEmpty(string id,
    int scene,
    int clip,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var count = await store.EmptyClipTrashAsync(id, scene, clip);
        return Results.Ok(new { ok = true, purgedCount = count, message = $"Permanently purged {count} take(s)." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScenesSceneMusicVersions(string id,
    int scene,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetMusicVersionsAsync(id, scene);
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneMusicVersionsTakeIdPromote(string id,
    int scene,
    string takeId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
        await ApiEndpointHelpers.RunProjectVersionActionAsync(id, store, user, opts,
        () => store.PromoteMusicVersionAsync(id, scene, takeId),
        "Failed to promote audio take.",
        $"Promoted audio take {takeId} to active.", ct);

    private static async Task<IResult> DeleteProjectsIdScenesSceneMusicVersionsTakeId(string id,
    int scene,
    string takeId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
        await ApiEndpointHelpers.RunProjectVersionActionAsync(id, store, user, opts,
        () => store.SoftDeleteMusicVersionAsync(id, scene, takeId),
        "Failed to delete audio take.",
        $"Soft-deleted audio take {takeId}.", ct);

    private static async Task<IResult> GetProjectsIdScenesSceneMusicVersionsTrash(string id,
    int scene,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetTrashMusicVersionsAsync(id, scene);
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneMusicVersionsTakeIdRestore(string id,
    int scene,
    string takeId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
        await ApiEndpointHelpers.RunProjectVersionActionAsync(id, store, user, opts,
        () => store.RestoreSoftDeletedMusicVersionAsync(id, scene, takeId),
        "Failed to restore audio take from trash.",
        $"Restored audio take {takeId} from trash.", ct);

    private static async Task<IResult> PostProjectsIdPush(string id,
    PushProjectApiRequest? body,
    ProjectStore store,
    ProjectGitRepositoryService git,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var proj = await store.RequireProjectAsync(id, ct);
        if (!await store.CanUserPublishDemoAsync(id, user.UserId, user.IsAdmin, ct))
        {
            return Results.Json(new { ok = false, error = "Only the project owner or an admin can push it." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var dir = await store.GetProjectDirAsync(id, ct);
        GitCommitInfo? commit = null;
        if (body?.CommitFirst == true)
        {
            commit = await git.CommitProjectStateAsync(
                dir, user.UserId ?? "PageToMovie", body.Message ?? "Project update");
        }

        var push = await git.PushProjectAsync(dir, proj.Id);
        if (!push.Success)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = push.Message,
                branch = push.Branch,
                commitHash = push.CommitHash ?? commit?.CommitHash,
                historyUrl = push.HistoryUrl,
                commit,
            });
        }

        return Results.Ok(new
        {
            ok = true,
            branch = push.Branch,
            commitHash = push.CommitHash,
            historyUrl = push.HistoryUrl,
            message = push.Message,
            commit,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdSyncOrigin(string id,
    SyncOriginApiRequest body,
    ProjectStore store,
    ProjectGitRepositoryService git,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (string.IsNullOrWhiteSpace(body?.ParentProjectId))
        return Results.BadRequest(new { ok = false, error = "parentProjectId required" });
    try
    {
        var target = await store.RequireProjectAsync(id, ct);
        await store.RequireProjectAsync(body.ParentProjectId, ct);
        if (!await store.CanUserPublishDemoAsync(id, user.UserId, user.IsAdmin, ct))
        {
            return Results.Json(new { ok = false, error = "Only the project owner or an admin can sync it." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var targetDir = await store.GetProjectDirAsync(id, ct);
        var originDir = await store.GetProjectDirAsync(body.ParentProjectId, ct);
        PageToMovie.Engine.GitMergeResult res;
        if (!string.IsNullOrWhiteSpace(body.AutoResolveStrategy)
            && Enum.TryParse<PageToMovie.Engine.Collaboration.AutoTextMerger.Strategy>(
                body.AutoResolveStrategy, ignoreCase: true, out var strategy))
        {
            res = await git.SyncForkFromOriginWithAutoResolveAsync(
                targetDir, originDir, strategy);
        }
        else
        {
            res = await git.SyncForkFromOriginAsync(
                targetDir, originDir);
        }
        if (res.Success)
        {
            // The merge rewrote project files on disk behind the store's caches — drop them or the
            // Film/Screenplay pages keep serving the pre-merge plan. Use the NORMALIZED id: the
            // route id may still carry %2F, which would miss the cache keys.
            store.InvalidateSceneListCache(target.Id);
            store.InvalidateReadCaches(target.Id);
        }
        return Results.Ok(new
        {
            ok = res.Success,
            hasConflicts = res.HasConflicts,
            commitHash = res.CommitHash,
            message = res.Message,
            autoResolvedCount = res.AutoResolvedCount,
            remainingConflictPaths = res.RemainingConflictPaths,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdContributionDiff(string id,
    string? originProjectId,
    ProjectStore store,
    ProjectContributionService contribService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    var parentId = originProjectId;
    if (string.IsNullOrWhiteSpace(parentId))
    {
        var proj = await store.GetProjectAsync(id, ct);
        parentId = proj?.ParentProjectId;
    }

    if (string.IsNullOrWhiteSpace(parentId))
        return Results.BadRequest(new { ok = false, error = "originProjectId or parent project required for diff" });

    try
    {
        var targetDir = await store.GetProjectDirAsync(id, ct);
        var originDir = await store.GetProjectDirAsync(parentId, ct);
        var diff = await contribService.ComputeDiffAsync(id, parentId, targetDir, originDir, ct);
        return Results.Ok(diff);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdContributionSyncMedia(string id,
    SyncOriginApiRequest req,
    ProjectStore store,
    ProjectContributionService contribService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    IHttpClientFactory httpFactory,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    var parentId = req?.ParentProjectId;
    if (string.IsNullOrWhiteSpace(parentId))
    {
        var proj = await store.GetProjectAsync(id, ct);
        parentId = proj?.ParentProjectId;
    }

    if (string.IsNullOrWhiteSpace(parentId))
        return Results.BadRequest(new { ok = false, error = "parentProjectId required for media sync" });

    try
    {
        var targetDir = await store.GetProjectDirAsync(id, ct);
        var originDir = await store.GetProjectDirAsync(parentId, ct);
        var result = await contribService.SyncContributionMediaAsync(
            targetDir, originDir, httpFactory.CreateClient("media-proxy"), ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsProjectIdScenesSceneKeyVersions(string projectId,
    string sceneKey,
    PageToMovie.Engine.Collaboration.SceneVersionStore versions,
    CancellationToken ct)
    {
    var list = await versions.ListHistoryAsync(projectId, sceneKey, ct);
    return Results.Ok(new { ok = true, versions = list });
}

    private static async Task<IResult> PostProjectsProjectIdScenesSceneKeyVersions(string projectId,
    string sceneKey,
    PageToMovie.Engine.Collaboration.SceneVersionStore versions,
    HttpRequest req,
    CancellationToken ct)
    {
    string? note = null;
    string? createdBy = null;
    string? sceneStateJson = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("note", out var n)) note = n.GetString();
        if (root.TryGetProperty("createdBy", out var c)) createdBy = c.GetString();
        if (root.TryGetProperty("sceneStateJson", out var s)) sceneStateJson = s.GetString();
        else if (root.TryGetProperty("sceneState", out var s2)) sceneStateJson = s2.GetRawText();
    }
    catch (Exception)
    {
        // Optional JSON body — missing/invalid payload still snapshots with null note.
        note = null;
    }

    var info = await versions.SnapshotAsync(projectId, sceneKey, sceneStateJson, null, note, createdBy, ct);
    return Results.Ok(new { ok = true, version = info });
}

    private static async Task<IResult> PostProjectsProjectIdScenesSceneKeyVersionsVersionIdRestore(string projectId,
    string sceneKey,
    string versionId,
    PageToMovie.Engine.Collaboration.SceneVersionStore versions,
    CancellationToken ct)
    {
    var result = await versions.RestoreAsync(projectId, sceneKey, versionId, null, ct);
    if (!result.Ok)
        return Results.BadRequest(new { ok = false, error = result.Error });

    return Results.Ok(new
    {
        ok = true,
        version = result.Version,
        sceneStateJson = result.SceneStateJson,
        restoredFiles = result.RestoredFiles
    });
}
}
