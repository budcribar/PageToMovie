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

public static partial class SceneClipEndpoints
{
    public static IEndpointRouteBuilder MapSceneClipEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{id}/scenes/{scene:int}/history", GetProjectsIdScenesSceneHistory);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/revert/{commitHash}", PostProjectsIdScenesSceneRevertCommitHash);
        app.MapGet("/api/projects/{id}/edit-log", GetProjectsIdEditLog);
        app.MapPost("/api/projects/{id}/clips/review", PostProjectsIdClipsReview);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips", PostProjectsIdScenesSceneClips);
        app.MapPut("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}", PutProjectsIdScenesSceneClipsClip);
        app.MapDelete("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}", DeleteProjectsIdScenesSceneClipsClip);
        // <summary>Delete a whole scene from the shot plan (persisted — removes it from the blueprint and
        // deletes the scene's on-disk media). Owner/admin only.</summary>
        app.MapDelete("/api/projects/{id}/scenes/{scene:int}", DeleteProjectsIdScenesScene);
        // <summary>Reorder (renumber) whole scenes: body.order lists CURRENT scene numbers in their
        // new sequence. Renames every number-keyed file, permutes the screenplay's scene chunks,
        // appends the client rename manifest, one git commit. Owner/admin only.</summary>
        app.MapPost("/api/projects/{id}/scenes/reorder", PostProjectsIdScenesReorder);
        // <summary>Reorder (renumber) one scene's clips: body.order lists CURRENT clip numbers in
        // their new sequence (result is contiguous C01..CNN). Owner/admin only.</summary>
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/reorder", PostProjectsIdScenesSceneClipsReorder);
        // <summary>Rename-manifest entries with id &gt; after — the client's local media folder
        // replays these to catch up with server-side renumbering (reorder/insert).</summary>
        app.MapGet("/api/projects/{id}/media-renames", GetProjectsIdMediaRenames);
        // <summary>Append a new empty scene to the shot plan. Owner/admin only.</summary>
        app.MapPost("/api/projects/{id}/scenes", PostProjectsIdScenes);
        // <summary>One-click add a prefilled (editable) end-credits scene. Owner/admin only.</summary>
        app.MapPost("/api/projects/{id}/scenes/credits", PostProjectsIdScenesCredits);
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/approve", PostProjectsIdScenesSceneApprove);
        app.MapGet("/api/projects/{id}/clip-reviews", GetProjectsIdClipReviews);
        // <summary>Load or rebuild assets/review/index.json (one row per on-disk clip).</summary>
        app.MapGet("/api/projects/{id}/review/index", GetProjectsIdReviewIndex);
        // <summary>
        // Rebuild project-local ARTIFACTS.md + artifact_index.json (+ telemetry cost/models snapshots).
        // Use before manual whole-project review (Claude on the project folder). Zip export deferred.
        // </summary>
        app.MapPost("/api/projects/{id}/artifacts/index", PostProjectsIdArtifactsIndex);
        app.MapGet("/api/projects/{id}/artifacts/index", GetProjectsIdArtifactsIndex);
        // <summary>Load latest auto-review draft for a clip (if any).</summary>
        app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/auto-review", GetProjectsIdScenesSceneClipsClipAutoReview);
        // <summary>Trigger automated dialogue verification for a clip on demand. Accepts optional uploaded video file which is deleted immediately after API call.</summary>
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/verify-dialogue", PostProjectsIdScenesSceneClipsClipVerifyDialogue);
        // <summary>Upload local client clip MP4 file to server assets/video directory.</summary>
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/upload", PostProjectsIdScenesSceneClipsClipUpload);
        // Browser pushes a clip sidecar (.clip.json) the server no longer has — self-heal for a project
        // whose provider pointers went missing; the local media folder keeps a synced copy.
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/sidecar", PostProjectsIdScenesSceneClipsClipSidecar);
        // <summary>Write accepted suggestion fields (cast / clip prompt). Does not regen — client starts gen after.</summary>
        app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/auto-review/apply", PostProjectsIdScenesSceneClipsClipAutoReviewApply);
        app.MapGet("/api/projects/{id}/scenes", GetProjectsIdScenes);
        app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}", GetProjectsIdScenesSceneNumber);
        app.MapMethods("/api/projects/{id}/scenes/{sceneNumber:int}/clips/{clipNumber:int}/video",
            new[] { "GET", "HEAD" }, GetProjectsIdScenesSceneNumberClipsClipNumberVideo);

        app.MapGet("/api/projects/{id}/clips/fork-fallback-needed", GetProjectsIdClipsForkFallbackNeeded);
        app.MapPost("/api/projects/{id}/scenes/{sceneNumber:int}/clips/{clipNumber:int}/fork-fallback", PostProjectsIdScenesClipForkFallback);

        // <summary>Archived prompt (+ paired video, if the client's media folder still has it) versions for one clip.</summary>
        app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}/clips/{clipNumber:int}/prompt-history", GetProjectsIdScenesSceneNumberClipsClipNumberPromptHistory);
        app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}/composite", GetProjectsIdScenesSceneNumberComposite);
        // <summary>Load full movie AI review report.</summary>
        app.MapGet("/api/projects/{id}/review/movie", GetProjectsIdReviewMovie);
        // <summary>Run full movie AI review with scene group chunking.</summary>
        app.MapPost("/api/projects/{id}/review/movie", PostProjectsIdReviewMovie);
        return app;
    }

    private static async Task<IResult> GetProjectsIdScenesSceneHistory(string id,
    int scene,
    int? limit,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        await store.RequireProjectAsync(id, ct);
        var history = await store.GetSceneGitHistoryAsync(id, scene, limit ?? 20);
        return Results.Ok(new { ok = true, history });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneRevertCommitHash(string id,
    int scene,
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
        if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can revert scene changes.", ct) is { } forbidden)
            return forbidden;

        var success = await store.RevertSceneToCommitAsync(id, scene, commitHash, user.UserId);
        if (!success)
        {
            return Results.BadRequest(new { ok = false, error = $"Failed to revert Scene {scene} to commit {commitHash[..Math.Min(8, commitHash.Length)]}." });
        }
        return Results.Ok(new { ok = true, message = $"Successfully reverted Scene {scene} to commit {commitHash[..Math.Min(8, commitHash.Length)]}." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdEditLog(string id, EditLogService logs, CancellationToken ct)
    {
    try
    {
        var doc = await logs.LoadAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, editLog = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdClipsReview(string id, ClipReviewRequest body, EditLogService logs, CancellationToken ct)
    {
    try
    {
        body.ProjectId = id;
        await logs.SetClipReviewAsync(id, body.Scene, body.Clip, body.Status, body.Note, ct);
        return Results.Ok(new { ok = true, projectId = id, scene = body.Scene, clip = body.Clip, status = body.Status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult PostProjectsIdScenesSceneClips(string id, int scene, ClipEditRequest body, ProjectStore store)
    {
    try
    {
        body.ProjectId = id;
        body.Scene = scene;
        store.AddClip(id, scene, body);
        return Results.Ok(new { ok = true, projectId = id, scene, clip = body.Clip, added = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult PutProjectsIdScenesSceneClipsClip(string id, int scene, int clip, ClipEditRequest body, ProjectStore store)
    {
    try
    {
        body.ProjectId = id;
        body.Scene = scene;
        body.Clip = clip;
        store.UpdateClipFields(id, scene, clip, body);
        return Results.Ok(new { ok = true, projectId = id, scene, clip });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> DeleteProjectsIdScenesSceneClipsClip(string id, int scene, int clip, ProjectStore store, ReviewIndexService reviewIndex,
    EditLogService logs, CancellationToken ct)
    {
    try
    {
        var wasInBlueprint = store.DeleteClip(id, scene, clip);
        await reviewIndex.RemoveClipAsync(id, scene, clip, ct);
        await logs.RemoveClipReviewStateAsync(id, scene, clip, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            scene,
            clip,
            deleted = true,
            wasInBlueprint,
            message = $"Deleted S{scene:D2}C{clip:D2} — Play scene / Play WIP to refresh the assembled cut",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> DeleteProjectsIdScenesScene(string id, int scene, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts,
    ILockService locks, PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can edit the shot plan.", ct) is { } forbidden)
        return forbidden;
    // I9 / P6: cannot delete while scene:N lease or job lock is held
    var leaseKey = PageToMovie.Engine.Collaboration.ProjectLeaseKeys.Scene(scene);
    var sceneLease = await leases.GetAsync(id, leaseKey, ct);
    if (sceneLease is not null)
        return Results.Json(new {
            ok = false,
            error = "scene_locked",
            message = $"Scene {scene:D2} is locked by {sceneLease.HolderUserId}. Release the lease before deleting.",
            holderUserId = sceneLease.HolderUserId,
        }, statusCode: StatusCodes.Status423Locked);
    var jobLock = locks.Get(LockKeys.Scene(id, scene));
    if (jobLock is not null)
        return Results.Json(new {
            ok = false,
            error = "scene_locked",
            message = $"Scene {scene:D2} has an active job lock held by {jobLock.UserId}.",
            holderUserId = jobLock.UserId,
        }, statusCode: StatusCodes.Status423Locked);
    try
    {
        var removed = store.DeleteScene(id, scene);
        return Results.Ok(new { ok = true, projectId = id, scene, deleted = removed,
            message = $"Deleted Scene {scene:D2}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenes(string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can edit the shot plan.", ct) is { } forbidden)
        return forbidden;
    try
    {
        var sceneNo = store.AddScene(id);
        return Results.Ok(new { ok = true, projectId = id, scene = sceneNo, message = $"Added Scene {sceneNo:D2}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesCredits(string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can edit the shot plan.", ct) is { } forbidden)
        return forbidden;
    try
    {
        var sceneNo = store.AddCreditsScene(id);
        return Results.Ok(new { ok = true, projectId = id, scene = sceneNo, message = $"Added credits (Scene {sceneNo:D2}) — edit or generate it" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneApprove(string id, int scene, SceneApproveRequest? body, EditLogService logs, CancellationToken ct)
    {
    try
    {
        body ??= new SceneApproveRequest();
        await logs.MarkSceneApprovedAsync(id, scene, body.Note ?? "", ct);
        return Results.Ok(new { ok = true, projectId = id, scene, message = "Scene approved" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdClipReviews(string id, EditLogService logs, CancellationToken ct)
    {
    try
    {
        var map = await logs.GetClipReviewMapAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, reviews = map });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdReviewIndex(string id, bool? rebuild, ReviewIndexService reviewIndex, CancellationToken ct)
    {
    try
    {
        var doc = rebuild == true
            ? await reviewIndex.RebuildAsync(id, ct: ct)
            : await reviewIndex.LoadAsync(id, ct) ?? await reviewIndex.RebuildAsync(id, ct: ct);
        return Results.Ok(new { ok = true, index = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdArtifactsIndex(string id, ProjectArtifactIndexService artifacts, CancellationToken ct)
    {
    try
    {
        var doc = await artifacts.RebuildAsync(id, ct);
        return Results.Ok(new
        {
            ok = true,
            readyForManualFinalReview = doc.ReadyForManualFinalReview,
            missingRequired = doc.MissingRequired,
            index = doc,
            paths = new
            {
                artifactsMd = "ARTIFACTS.md",
                artifactIndexJson = "artifact_index.json",
                telemetry = "telemetry/",
            },
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdArtifactsIndex(string id, ProjectArtifactIndexService artifacts, bool? rebuild, CancellationToken ct)
    {
    try
    {
        if (rebuild == true)
        {
            var doc = await artifacts.RebuildAsync(id, ct);
            return Results.Ok(new { ok = true, index = doc });
        }

        var path = await artifacts.IndexJsonPathAsync(id, ct);
        if (!File.Exists(path))
        {
            var doc = await artifacts.RebuildAsync(id, ct);
            return Results.Ok(new { ok = true, index = doc, rebuilt = true });
        }

        var json = await File.ReadAllTextAsync(path, ct);
        var existing = System.Text.Json.JsonSerializer.Deserialize<ArtifactIndexDocument>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Results.Ok(new { ok = true, index = existing });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScenesSceneClipsClipAutoReview(string id, int scene, int clip, ClipAutoReviewService reviews, CancellationToken ct)
    {
    try
    {
        var draft = await reviews.LoadDraftAsync(id, scene, clip, ct);
        if (draft is null)
            return Results.NotFound(new { ok = false, error = "No auto-review draft yet." });
        return Results.Ok(new { ok = true, draft });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneClipsClipVerifyDialogue(string id, int scene, int clip, HttpContext httpContext, ClipDialogueVerificationService verifier, bool force = false, CancellationToken ct = default)
    {
    string? tempFilePath = null;
    try
    {
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile(ApiText.VideoFolder);
            if (file is { Length: > 0 })
            {
                tempFilePath = Path.Combine(Path.GetTempPath(), $"dialogue_verify_{Guid.NewGuid():N}.mp4");
                using (var stream = File.Create(tempFilePath))
                {
                    await file.CopyToAsync(stream, ct).ConfigureAwait(false);
                }
            }
        }

        var result = await verifier.VerifyClipDialogueAsync(id, scene, clip, overrideVideoPath: tempFilePath, force: force, ct: ct);
        return Results.Ok(new { ok = true, result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
    finally
    {
        if (!string.IsNullOrWhiteSpace(tempFilePath) && File.Exists(tempFilePath))
        {
            try
            {
                File.Delete(tempFilePath);
            }
            catch (Exception)
            {
                // Best-effort cleanup of a temp upload that may already be gone.
            }
        }
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneClipsClipSidecar(
        string id, int scene, int clip, HttpContext httpContext,
        [AsParameters] SidecarServices svc, CancellationToken ct)
    {
        if (AuthGate.RequireLogin(svc.User, svc.Opts) is { } denied)
            return denied;
        string body;
        using (var reader = new StreamReader(httpContext.Request.Body))
            body = await reader.ReadToEndAsync(ct);
        if (ValidateSidecarBody(body, scene, clip) is { } bad)
            return bad;
        return await RestoreSidecarAsync(id, scene, clip, body, svc.Store, ct);
    }

    private static async Task<IResult> PostProjectsIdScenesSceneClipsClipUpload(
        string id, int scene, int clip, string? kind, double? seconds,
        [AsParameters] ClipUploadServices svc, CancellationToken ct)
    {
    var httpContext = svc.HttpContext;
    var store = svc.Store;
    if (!httpContext.Request.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "Form data expected." });

    var form = await httpContext.Request.ReadFormAsync(ct);
    var file = form.Files.GetFile(ApiText.VideoFolder);
    if (file is null || file.Length < 1024)
        return Results.BadRequest(new { ok = false, error = "Valid MP4 file expected." });

    var projectDir = await store.GetProjectDirAsync(id, ct);

    // "extend-source": the browser-trimmed continuation input for video-extend. Media must not
    // live on the server, so relay the bytes through IVideoClient (catalog-routed) and keep
    // only the file_id (+ duration) in a small marker. Fakes / no stored-file upload fall
    // back to the on-disk file below.
    if (string.Equals(kind, "extend-source", StringComparison.OrdinalIgnoreCase)
        && await TryUploadExtendSourceAsync(id, scene, clip, seconds, file, svc, ct).ConfigureAwait(false) is { } uploaded)
        return uploaded;

    var destDir = Path.Combine(projectDir, ApiText.AssetsFolder, ApiText.VideoFolder);
    Directory.CreateDirectory(destDir);
    var fileName = ChooseUploadDestFileName(kind, file.FileName, scene, clip);
    var destPath = Path.Combine(destDir, fileName);

    using (var stream = File.Create(destPath))
    {
        await file.CopyToAsync(stream, ct).ConfigureAwait(false);
    }

    // Every other clip-writing path (generation, remux, stage2) invalidates the scene-list/dir-index
    // read cache after writing — this client-render upload path (credits card, extend-source) didn't,
    // so a clip written here could sit invisible to OnDisk/listing checks for the rest of the cache's
    // TTL. A generated clip's own multi-second API round trip usually outlasts that window; a fast
    // client-side canvas render (the credits card) does not, so it reliably hit the stale window.
    if (!string.Equals(kind, "extend-source", StringComparison.OrdinalIgnoreCase))
        store.InvalidateSceneListCache(id);

    return Results.Ok(new { ok = true, projectId = id, scene, clip, path = destPath });
}

    // Catalog-routed stored-file upload. Ok when a file_id is issued; null → on-disk fallback.
    private static async Task<IResult?> TryUploadExtendSourceAsync(
        string id, int scene, int clip, double? seconds,
        IFormFile file, ClipUploadServices svc, CancellationToken ct)
    {
        if (svc.Services.GetService(typeof(IVideoClient)) is not IVideoClient video || !video.IsConfigured)
            return null;

        var cfg = await svc.Store.GetConfigAsync(id, ct).ConfigureAwait(false);
        var modelId = CatalogApiKey.ResolveVideoModel(null, ProjectModelSelection.TryVideo(cfg));
        var providerId = CatalogApiKey.ProviderIdForVideo(modelId);
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var keys = svc.Services.GetService(typeof(IUserApiKeyProvider)) as IUserApiKeyProvider;
        var user = svc.Services.GetService(typeof(IUserContext)) as IUserContext;
        var key = await CatalogApiKey.GetKeyAsync(keys, user?.UserId, providerId, ct).ConfigureAwait(false);
        using (CatalogApiKey.PushKey(providerId, key))
        using (UserApiCallScope.Push(user?.UserId))
        {
            await using var src = file.OpenReadStream();
            var fileId = await video.TryUploadVideoStreamAsync(
                src, $"extend_src_s{scene:D2}c{clip:D2}.mp4", modelId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(fileId))
                return null;

            var projectDir = await svc.Store.GetProjectDirAsync(id, ct).ConfigureAwait(false);
            await WriteExtendSourceMarkerAsync(projectDir, scene, clip, fileId, seconds, file.Length, ct)
                .ConfigureAwait(false);
            return Results.Ok(new { ok = true, projectId = id, scene, clip, fileId, seconds });
        }
    }

    private static async Task WriteExtendSourceMarkerAsync(
        string projectDir, int scene, int clip, string fileId, double? seconds, long bytes, CancellationToken ct)
    {
        var markerDir = Path.Combine(projectDir, ApiText.AssetsFolder, ApiText.VideoFolder);
        Directory.CreateDirectory(markerDir);
        var marker = Path.Combine(markerDir, FilmJobService.ExtendSourceMarkerName(scene, clip));
        await File.WriteAllTextAsync(marker, System.Text.Json.JsonSerializer.Serialize(new
        {
            file_id = fileId,
            duration_seconds = seconds,
            uploaded_utc = DateTime.UtcNow.ToString("o"),
            bytes,
        }), ct).ConfigureAwait(false);
        TryDeleteIfExists(Path.Combine(markerDir, $"_extend_src_s{scene:D2}c{clip:D2}.mp4"));
    }

    private static void TryDeleteIfExists(string path)
    {
        if (!File.Exists(path))
            return;
        try { File.Delete(path); }
        catch { /* best effort */ }
    }

    // "extend-source": the client's tail-trimmed continuation input for video-extend (see
    // FilmJobService.GenerateOneClipAsync) — fixed name, ignores any client-supplied filename so
    // the server always finds it at the exact path it expects.
    private static string ChooseUploadDestFileName(string? kind, string? uploadedFileName, int scene, int clip)
    {
        if (string.Equals(kind, "extend-source", StringComparison.OrdinalIgnoreCase))
            return $"_extend_src_s{scene:D2}c{clip:D2}.mp4";
        if (!string.IsNullOrWhiteSpace(uploadedFileName) && uploadedFileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(uploadedFileName);
        return $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4";
    }

    private static async Task<IResult> PostProjectsIdScenesSceneClipsClipAutoReviewApply(string id, int scene, int clip, ApplyClipAutoReviewRequest? body, ClipAutoReviewService reviews, CancellationToken ct)
    {
    try
    {
        body ??= new ApplyClipAutoReviewRequest();
        body.ProjectId = id;
        body.Scene = scene;
        body.Clip = clip;
        await reviews.ApplySuggestionsAsync(id, scene, clip, body.Items, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            scene,
            clip,
            message = $"Applied {body.Items.Count} suggestion(s)",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScenes(string id,
    ProjectStore store,
    ILockService locks,
    IUserContext user,
    PageToMovie.Engine.Collaboration.IProjectLeaseService projectLeases,
    string? light,
    CancellationToken ct)
    {
    try
    {
        var probe = !string.Equals(light, "1", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(light, "true", StringComparison.OrdinalIgnoreCase);
        var scenes = (await store.ListScenesAsync(id, probeDurations: probe, ct)).ToList();
        var active = locks.ListActive();
        IReadOnlyList<PageToMovie.Engine.Collaboration.ProjectLease> leaseList;
        try { leaseList = await projectLeases.ListAsync(id, ct); }
        catch { leaseList = Array.Empty<PageToMovie.Engine.Collaboration.ProjectLease>(); }
        foreach (var s in scenes)
        {
            var key = LockKeys.Scene(id, s.SceneNumber);
            var held = active.FirstOrDefault(l =>
                string.Equals(l.Resource, key, StringComparison.OrdinalIgnoreCase));
            if (held is not null)
            {
                s.LockOwnerUserId = held.UserId;
                s.LockReason = held.Reason;
                s.LockedByOther = !string.Equals(held.UserId, user.UserId, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var leaseKey = PageToMovie.Engine.Collaboration.ProjectLeaseKeys.Scene(s.SceneNumber);
            var lease = leaseList.FirstOrDefault(l =>
                string.Equals(l.ResourceKey, leaseKey, StringComparison.OrdinalIgnoreCase));
            if (lease is null) continue;
            s.LockOwnerUserId = lease.HolderUserId;
            s.LockReason = "lease";
            s.LockedByOther = !string.Equals(lease.HolderUserId, user.UserId, StringComparison.OrdinalIgnoreCase);
        }
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            sceneCount = scenes.Count,
            clipCount = scenes.Sum(s => s.ClipCount),
            clipsOnDisk = scenes.Sum(s => s.ClipsOnDisk),
            callerUserId = user.UserId,
            light = !probe,
            scenes,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScenesSceneNumber(string id,
    int sceneNumber,
    ProjectStore store,
    string? light,
    CancellationToken ct)
    {
    try
    {
        var probe = !string.Equals(light, "1", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(light, "true", StringComparison.OrdinalIgnoreCase);
        var detail = await store.GetSceneDetailAsync(id, sceneNumber, probeDurations: probe, ct);
        if (detail is null)
            return Results.NotFound(new { ok = false, error = $"Scene {sceneNumber} not found" });
        return Results.Ok(new { ok = true, projectId = id, scene = detail, light = !probe });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScenesSceneNumberClipsClipNumberVideo(
        string id, int sceneNumber, int clipNumber,
        HttpRequest req,
        [AsParameters] ClipVideoServices svc,
        CancellationToken ct)
    {
    if (AuthGate.RequireLogin(svc.User, svc.Opts) is { } denied)
        return denied;
    try
    {
        return await ServeClipVideoAsync(id, sceneNumber, clipNumber, req, svc, ct);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<(string? Path, string? ParentId)> ResolveClipVideoPathWithParentAsync(
        ProjectStore store, string id, int sceneNumber, int clipNumber, CancellationToken ct)
    {
        var path = store.ResolveClipVideoPath(id, sceneNumber, clipNumber);
        if (path is not null)
            return (path, null);

        string? parentId = null;
        try
        {
            var proj = await store.GetProjectAsync(id, ct);
            parentId = proj?.ParentProjectId;
            if (!string.IsNullOrWhiteSpace(parentId))
                path = store.ResolveClipVideoPath(parentId, sceneNumber, clipNumber);
        }
        catch { /* fall through */ }
        return (path, parentId);
    }

    private static async Task MarkForkFallbackNeededAsync(
        ProjectStore store, string projectId, string? parentId, int scene, int clip, CancellationToken ct)
    {
        try
        {
            var dir = await store.GetProjectDirAsync(projectId, ct);
            ClipForkFallback.MarkNeeded(dir, scene, clip);
            if (!string.IsNullOrWhiteSpace(parentId)
                && !string.Equals(parentId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = await store.GetProjectDirAsync(parentId, ct);
                ClipForkFallback.MarkNeeded(parentDir, scene, clip);
            }
        }
        catch { /* never fail playback on a marker write */ }
    }

    private static async Task<IResult> GetProjectsIdClipsForkFallbackNeeded(
        string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
        if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
            return denied;
        try
        {
            var dir = await store.GetProjectDirAsync(id, ct);
            var items = ClipForkFallback.ListNeeded(dir)
                .Select(x => new { scene = x.Scene, clip = x.Clip })
                .ToList();
            return Results.Ok(new { ok = true, clips = items });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<IResult> PostProjectsIdScenesClipForkFallback(
        string id, int sceneNumber, int clipNumber,
        HttpRequest req,
        ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts,
        CancellationToken ct)
    {
        if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
            return denied;
        try
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { ok = false, error = "multipart form required (field: file)" });
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length < 1024)
                return Results.BadRequest(new { ok = false, error = "clip file required" });
            if (file.Length > 80L * 1024 * 1024)
                return Results.BadRequest(new { ok = false, error = "clip too large (max 80 MB)" });

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var dir = await store.GetProjectDirAsync(id, ct);
            // Hosted Railway copy: fallback when the provider file cannot be streamed.
            ClipForkFallback.WriteProtectedMp4(dir, sceneNumber, clipNumber, ms.ToArray());
            ClipForkFallback.ClearNeeded(dir, sceneNumber, clipNumber);
            return Results.Ok(new { ok = true, hosted = "railway" });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<IResult> GetProjectsIdScenesSceneNumberClipsClipNumberPromptHistory(string id, int sceneNumber, int clipNumber, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var projectDir = await store.GetProjectDirAsync(id, ct);
        string? currentPrompt = null;
        var currentMetaPath = Path.Combine(
            projectDir, ApiText.AssetsFolder, ApiText.VideoFolder, "prompts", $"S{sceneNumber:D2}C{clipNumber:D2}.meta.json");
        if (File.Exists(currentMetaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(currentMetaPath, ct));
                if (doc.RootElement.TryGetProperty("prompt", out var p))
                    currentPrompt = p.GetString();
            }
            catch { /* ignore unreadable current meta */ }
        }

        var history = await FilmJobService.ListClipPromptHistoryAsync(projectDir, sceneNumber, clipNumber, ct);
        return Results.Ok(new
        {
            ok = true,
            current = new
            {
                prompt = currentPrompt,
                videoRelativePath = MediaRegistryService.ClipRelativePath(sceneNumber, clipNumber),
            },
            history = history.Select(h => new
            {
                timestampUtc = h.TimestampUtc,
                prompt = h.Prompt,
                videoRelativePath = h.VideoRelativePath,
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsIdScenesSceneNumberComposite(string id, int sceneNumber, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var path = store.ResolveCompositePath(id, sceneNumber);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "composite not found" });
        return Results.File(path, SpecializedMimeType.VideoMp4.ToMimeTypeString(), enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdReviewMovie(string id,
    ProjectStore store,
    MovieAutoReviewService movieReview,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var report = await movieReview.LoadReportAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, report });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdReviewMovie(string id,
    MovieReviewRequest? body,
    ProjectStore store,
    MovieAutoReviewService movieReview,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var keyframes = body?.Keyframes ?? new List<MovieAutoReviewKeyframe>();
        var report = await movieReview.ReviewMovieAsync(id, keyframes, null, ct);
        return Results.Ok(new { ok = true, projectId = id, report });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesReorder(string id,
    ReorderRequest body,
    ProjectStore store,
    MediaRegistryService registry,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can reorder scenes.", ct) is { } forbidden)
        return forbidden;
    try
    {
        var result = store.ReorderScenes(id, body.Order ?? new List<int>(), user.UserId);
        await registry.RenamePathsAsync(id, result.MediaRenames, result.MediaDeletes, ct);
        return Results.Ok(new { ok = true, projectId = id, renamed = result.MediaRenames.Count, deleted = result.MediaDeletes.Count, manifestId = result.ManifestId });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScenesSceneClipsReorder(string id, int scene,
    ReorderRequest body,
    [AsParameters] ClipReorderServices svc,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(svc.User, svc.Opts) is { } denied)
        return denied;
    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, svc.Store, svc.User, "Only the project owner or an admin can reorder clips.", ct) is { } forbidden)
        return forbidden;
    try
    {
        var result = svc.Store.ReorderClips(id, scene, body.Order ?? new List<int>(), svc.User.UserId);
        await svc.Registry.RenamePathsAsync(id, result.MediaRenames, result.MediaDeletes, ct);
        return Results.Ok(new { ok = true, projectId = id, scene, renamed = result.MediaRenames.Count, deleted = result.MediaDeletes.Count, manifestId = result.ManifestId });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdMediaRenames(string id,
    long after,
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
        var entries = store.ReadRenameManifest(id, after);
        return Results.Text("{\"ok\":true,\"entries\":[" + string.Join(",", entries.Select(e => e.ToJsonString())) + "]}", "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}

/// <summary>Body for the scene/clip reorder endpoints: CURRENT numbers in their new sequence.</summary>
public sealed class ReorderRequest
{
    public List<int>? Order { get; set; }
}
