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

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/jobs/{jobId}/cancel", PostJobsJobIdCancel);
        // Phase F: multi-job list only — bare GET is 400 (no single-job shim)
        app.MapGet("/api/jobs", GetJobs);
        app.MapGet("/api/jobs/{jobId}", GetJobsJobId);
        app.MapPost("/api/jobs/gen-scene", PostJobsGenScene);
        app.MapPost("/api/jobs/gen-batch", PostJobsGenBatch);
        // <summary>
        // Batch TTS for re-voice (keys stay on server). Progress + per-line audio handoff over SignalR
        // (<c>Kind = speak-batch</c>, <c>ClientMediaUrl</c> / <c>ClientRelativePath</c>).
        // </summary>
        app.MapPost("/api/jobs/speak-batch", PostJobsSpeakBatch);
        // <summary>
        // Movie-wide voice substitution: walk every clip, associate each dialogue line with its speaker,
        // synthesize the character's cloned voice per line, and maintain the persisted speech alignment.
        // Tracked job (<c>Kind = voice-substitution</c>); per-line audio handoff over SignalR.
        // </summary>
        app.MapPost("/api/jobs/voice-substitution", PostJobsVoiceSubstitution);
        // <summary>
        // Cancel active jobs. Non-admin: caller's jobs only.
        // Admin: same unless <c>?all=true</c> (cancel every user's jobs).
        // Prefer <c>POST /api/jobs/{jobId}/cancel</c> when a specific id is known.
        // </summary>
        app.MapPost("/api/jobs/cancel", PostJobsCancel);
        app.MapGet("/api/stage2-status", GetStage2Status);
        // <summary>
        // Prompt-based edit of an already-generated clip (xAI /v1/videos/edits) — human-triggered,
        // per-clip, spends real provider money. Job-queue family (like character-variants), NOT the
        // synchronous "media" endpoint family lip-sync uses: edit processing time is not guaranteed short
        // just because the input clip is short, so this must never block the HTTP request — the client
        // polls/subscribes the returned job the same way it already does for scene generation.
        // </summary>
        app.MapPost("/api/jobs/video-edit", PostJobsVideoEdit);
        app.MapPost("/api/jobs/character-variants", PostJobsCharacterVariants);
        app.MapPost("/api/jobs/location-variants", PostJobsLocationVariants);
        // <summary>
        // Background enrich of the full-length screenplay (visual action from the book). Prefer this
        // over the synchronous POST /adaptation/embellish — Odyssey-scale drafts take minutes.
        // </summary>
        app.MapPost("/api/jobs/embellish", PostJobsEmbellish);
        // <summary>
        // Batch: 3 looks per used-in-plan cast face + location, vision auto-locks best (operator can override).
        // </summary>
        app.MapPost("/api/jobs/plan-looks", PostJobsPlanLooks);
        // <summary>
        // Film-pipeline voice sample job: short video (voice style + dialogue) kept as MP4 (no ffmpeg extract).
        // Use Force=true after editing the profile to regenerate.
        // </summary>
        app.MapPost("/api/jobs/voice-preview", PostJobsVoicePreview);
        // <summary>Same as extract-cast but under /api/jobs for consistency with other long AI ops.</summary>
        app.MapPost("/api/jobs/extract-cast", PostJobsExtractCast);
        // <summary>
        // Job: Grok vision sorts book images onto characters → scenes.json design_reference_images.
        // Progress via SignalR; cancel with /api/jobs/cancel.
        // </summary>
        app.MapPost("/api/jobs/sort-character-plates", PostJobsSortCharacterPlates);
        app.MapPost("/api/jobs/book-prepare", PostJobsBookPrepare);
        // <summary>Prepare (optional) + book→Fountain draft as one background job.</summary>
        app.MapPost("/api/jobs/book-import", PostJobsBookImport);
        app.MapPost("/api/jobs/stage1", PostJobsStage1);
        app.MapPost("/api/jobs/stage2", PostJobsStage2);
        app.MapPost("/api/jobs/youtube-upload", PostJobsYoutubeUpload);
        // <summary>Queue AI auto-review for one clip (prev tail + current → draft suggestions).</summary>
        app.MapPost("/api/jobs/clip-auto-review", PostJobsClipAutoReview);
        // <summary>Batch AI auto-review for on-disk clips (onlyMissing default true). Rebuilds assets/review/index.json.</summary>
        app.MapPost("/api/jobs/clip-auto-review-batch", PostJobsClipAutoReviewBatch);
        // <summary>
        // Queue background-music generation for a scene (client-side job: mirrors clip gen — the
        // server never spawns ffmpeg or persists audio bytes; segments proxy straight to the client).
        // </summary>
        app.MapPost("/api/jobs/scene-music", PostJobsSceneMusic);
        return app;
    }

    private static async Task<IResult> PostJobsJobIdCancel(string jobId, FilmJobService jobService, IUserContext user,
    PageToMovie.Engine.Collaboration.IProjectAclService acl, CancellationToken ct)
    {
    var job = jobService.GetJob(jobId);
    if (job is null)
        return Results.NotFound(new { ok = false, error = "job not found" });
    var isStarter = string.Equals(job.UserId, user.UserId, StringComparison.OrdinalIgnoreCase);
    var isOwner = false;
    if (!user.IsAdmin && !isStarter && !string.IsNullOrWhiteSpace(job.ProjectId))
    {
        // I10: project Owner may cancel any job on their project
        isOwner = await acl.CanAccessAsync(job.ProjectId, user.UserId ?? "",
            PageToMovie.Engine.Collaboration.ProjectAccessLevel.Owner, ct);
    }
    if (!user.IsAdmin && !isStarter && !isOwner)
        return Results.Json(new { ok = false, error = "not your job" },
            statusCode: StatusCodes.Status403Forbidden);
    await jobService.CancelAsync(jobId);
    return Results.Ok(new { ok = true, job = jobService.GetJob(jobId) });
}

    private static IResult GetJobs(FilmJobService jobService, IUserContext user, string? mine, string? projectId, string? userId)
    {
    var wantMine = string.Equals(mine, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mine, "true", StringComparison.OrdinalIgnoreCase);
    if (!wantMine && string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new
        {
            ok = false,
            error = "Specify mine=1, projectId, or userId. Single-job GET /api/jobs was removed (Phase F).",
            examples = new[]
            {
                "/api/jobs?mine=1",
                "/api/jobs?projectId=MyStory",
                "/api/jobs/{jobId}",
            },
        });
    }

    var filterUser = wantMine ? user.UserId : userId;
    if (!user.IsAdmin && !string.IsNullOrWhiteSpace(filterUser) && !string.Equals(filterUser, user.UserId, StringComparison.OrdinalIgnoreCase))
    {
        filterUser = user.UserId;
    }
    var list = jobService.ListJobs(filterUser, projectId, take: 50);
    return Results.Ok(new
    {
        ok = true,
        running = list.Any(j =>
            string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase)),
        jobs = list,
        count = list.Count,
        userId = user.UserId,
    });
}

    private static IResult GetJobsJobId(string jobId, FilmJobService jobService, IUserContext user)
    {
    var job = jobService.GetJob(jobId);
    if (job is null)
        return Results.NotFound(new { ok = false, error = "job not found" });
    if (!user.IsAdmin &&
        !string.IsNullOrWhiteSpace(job.UserId) &&
        !string.Equals(job.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new { ok = false, error = "not your job" },
            statusCode: StatusCodes.Status403Forbidden);
    }
    return Results.Ok(new { ok = true, job });
}

    private static async Task<IResult> PostJobsGenScene(StartSceneGenRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb,
    PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
    CancellationToken ct)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        if (body.Scene <= 0)
            return Results.BadRequest(new { ok = false, error = "scene required" });
        // I7: project lease scene:N — block if another editor holds it
        if (!string.IsNullOrWhiteSpace(body.ProjectId) && !string.IsNullOrWhiteSpace(user.UserId))
        {
            var (ok, lease) = await leases.TryAcquireAsync(
                body.ProjectId, PageToMovie.Engine.Collaboration.ProjectLeaseKeys.Scene(body.Scene),
                user.UserId, PageToMovie.Api.Collaboration.CollaborationEndpoints.DefaultLeaseTtl, ct);
            if (!ok)
                return Results.Json(new {
                    ok = false,
                    error = "scene_locked",
                    message = $"Scene {body.Scene:D2} is locked by {lease.HolderUserId}.",
                    holderUserId = lease.HolderUserId,
                }, statusCode: StatusCodes.Status423Locked);
        }
        var job = await jobService.StartSceneGenAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == ApiText.QueuedStatus
                ? $"Queued scene {body.Scene} (waiting for lock/worker)"
                : $"Started scene {body.Scene}",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsGenBatch(StartBatchGenRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        var hasClips = body.Clips is { Count: > 0 };
        if ((body.Scenes is null || body.Scenes.Count == 0) && !hasClips)
            return Results.BadRequest(new { ok = false, error = "scenes or clips required" });
        var job = await jobService.StartBatchGenAsync(body);
        var count = hasClips ? body.Clips!.Count : body.Scenes?.Count ?? 0;
        var unit = hasClips ? "clip" : "scene";
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == ApiText.QueuedStatus
                ? $"Queued batch for {count} {unit}(s)"
                : $"Started batch for {count} {unit}(s)",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsSpeakBatch(StartSpeakBatchRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        if (string.IsNullOrWhiteSpace(body.CharKey))
            body.CharKey = "Character_Narrator";
        var job = await jobService.StartSpeakBatchAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == ApiText.QueuedStatus
                ? "Queued speak-batch (waiting for lock/worker)"
                : "Started speak-batch",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsVoiceSubstitution(StartVoiceSubstitutionRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        if (string.IsNullOrWhiteSpace(body.CharKey))
            body.CharKey = "Character_Narrator";
        var job = await jobService.StartVoiceSubstitutionAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == ApiText.QueuedStatus
                ? "Queued voice substitution (waiting for lock/worker)"
                : "Started voice substitution",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsCancel(FilmJobService jobService,
    IUserContext user,
    bool? all)
    {
    var cancelAllUsers = user.IsAdmin && all == true;
    if (all == true && !user.IsAdmin)
    {
        return Results.Json(
            new { ok = false, error = "admin role required to cancel all users' jobs" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var cancelled = await jobService.CancelAsync(
        jobId: null,
        userId: cancelAllUsers ? null : user.UserId,
        cancelAllUsers: cancelAllUsers);

    return Results.Ok(new
    {
        ok = true,
        cancelled,
        scope = cancelAllUsers ? "all" : "user",
        userId = cancelAllUsers ? null : user.UserId,
        job = await jobService.GetSnapshotAsync(),
    });
}

    private static async Task<IResult> GetStage2Status(ProjectStore store, IUserContext user, UserDatabaseService userDb, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var userActiveId = await userDb.GetUserActiveProjectAsync(user.UserId, ct);
    // Per-user only — store.ActiveProjectId is process-wide and must not be used here.
    var id = userActiveId;
    if (string.IsNullOrWhiteSpace(id))
        return Results.Ok(new { ok = true, stage2_ready = false });
    // Drop if this user cannot see the project (stale id from another account).
    if (!user.IsAdmin)
    {
        try
        {
            var info = await store.GetProjectAsync(id, ct);
            if (info is null)
                return Results.Ok(new { ok = true, stage2_ready = false });
            UserEntity? me = null;
            try
            {
                me = await userDb.GetUserByIdAsync(user.UserId, ct).ConfigureAwait(false)
                     ?? await userDb.GetUserByUsernameAsync(user.UserId, ct).ConfigureAwait(false);
            }
            catch { /* user lookup is best-effort */ }
            var aliases = ProjectOwnership.CollectAliases(
                user.UserId, canonicalUserId: me?.UserId, username: me?.Username, email: me?.Email);
            if (!ProjectOwnership.IsOwnedBy(info, aliases))
                return Results.Ok(new { ok = true, stage2_ready = false });
        }
        catch { /* treat as no stage2 */ }
    }
    if (string.IsNullOrEmpty(id))
        return Results.Ok(new { ok = true, stage2_ready = false });
    var bp = await store.FindBlueprintPathAsync(id, ct);
    var ready = bp is not null && File.Exists(bp);
    var scenes = 0;
    var clips = 0;
    if (ready)
    {
        try
        {
            using var doc = await store.LoadBlueprintAsync(id, ct);
            if (doc is not null &&
                doc.RootElement.TryGetProperty("scenes", out var sc) &&
                sc.ValueKind == JsonValueKind.Array)
            {
                scenes = sc.GetArrayLength();
                foreach (var s in sc.EnumerateArray())
                {
                    if (s.TryGetProperty("veo_clips", out var vc) &&
                        vc.ValueKind == JsonValueKind.Array)
                        clips += vc.GetArrayLength();
                }
            }
        }
        catch { /* ignore */ }
    }
    return Results.Ok(new
    {
        ok = true,
        stage2_ready = ready && clips > 0,
        stage2_scenes = scenes,
        stage2_clips = clips,
        blueprint_path = bp,
        project_id = id,
    });
}

    private static async Task<IResult> PostJobsVideoEdit(StartVideoEditRequest body, FilmJobService jobService)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || body.Scene <= 0 || body.Clip <= 0)
            return Results.BadRequest(new { ok = false, error = "projectId, scene, and clip required" });
        if (string.IsNullOrWhiteSpace(body.Prompt))
            return Results.BadRequest(new { ok = false, error = "prompt required" });
        var job = await jobService.StartVideoEditAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued AI edit for S{body.Scene:D2}C{body.Clip:D2}",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsCharacterVariants(StartCharacterVariantsRequest body, FilmJobService jobService)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || string.IsNullOrWhiteSpace(body.CharKey))
            return Results.BadRequest(new { ok = false, error = "projectId and charKey required" });
        var job = await jobService.StartCharacterVariantsAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued portrait generation for {body.CharKey}",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsLocationVariants(StartLocationVariantsRequest body, FilmJobService jobService)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(body.LocKey))
            return Results.BadRequest(new { ok = false, error = "locKey required" });
        var job = await jobService.StartLocationVariantsAsync(body);
        return Results.Ok(new
        {
            ok = true,
            jobId = job.JobId,
            message = $"Queued set plate generation for {body.LocKey}",
            job,
        });
    }
    catch (LockConflictException ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, resource = ex.Resource, owner = ex.OwnerUserId });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostJobsEmbellish(StartEmbellishRequest? body, FilmJobService jobService)
    {
    try
    {
        var projectId = body?.ProjectId ?? "";
        if (string.IsNullOrWhiteSpace(projectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        var job = await jobService.StartEmbellishAsync(projectId);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            jobId = job.JobId,
            message = "Queued screenplay enrich",
            job,
        });
    }
    catch (LockConflictException ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, resource = ex.Resource, owner = ex.OwnerUserId });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsPlanLooks(StartPlanLooksRequest body, FilmJobService jobService)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        var job = await jobService.StartPlanLooksAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued looks for plan cast + places (AI auto-lock best)",
            job,
        });
    }
    catch (LockConflictException ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, resource = ex.Resource, owner = ex.OwnerUserId });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsVoicePreview(StartVoicePreviewRequest body, FilmJobService jobService)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || string.IsNullOrWhiteSpace(body.CharKey))
            return Results.BadRequest(new { ok = false, error = "projectId and charKey required" });
        var job = await jobService.StartVoicePreviewAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.Force
                ? $"Queued voice regenerate for {body.CharKey}"
                : $"Queued voice sample for {body.CharKey}",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsExtractCast(ExtractCastRequest? body, FilmJobService jobService, ProjectStore store)
    {
    try
    {
        body ??= new ExtractCastRequest();
        var id = string.IsNullOrWhiteSpace(body.ProjectId) ? store.ActiveProjectId : body.ProjectId;
        var job = await jobService.StartExtractCastAsync(id, force: body.Force, model: body.Model);
        return Results.Ok(new { ok = true, job });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostJobsSortCharacterPlates(AttachCharacterPlatesRequest body,
    FilmJobService jobService)
    {
    try
    {
        body.Force = true; // explicit user/job start always re-sorts
        if (body.MaxImages <= 0) body.MaxImages = 32;
        var job = await jobService.StartSortCharacterPlatesAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.UseGrok
                ? "Queued Grok vision character plate sort"
                : "Queued heuristic character plate sort",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsBookPrepare(StartBookPrepareRequest body,
    FilmJobService jobService,
    IUserContext user,
    UserDatabaseService userDb,
    IUserApiKeyProvider keys,
    IOptions<PageToMovieOptions> opts)
    {
    // PDF extract / plain text needs no AI key. Vision OCR only if requested or auto-selected later.
    if (AuthGate.RequireLogin(user, opts) is { } deniedLogin)
        return deniedLogin;
    if (body.ForceVision &&
        await AuthGate.RequirePersonalGrokKeyAsync(user, userDb, opts, ApiRuntime.UseFakes, keys, requireVisionKey: true) is { } deniedVision)
        return deniedVision;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        var job = await jobService.StartBookPrepareAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued book prepare (C# PDF extract / vision OCR)",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsBookImport(StartBookImportRequest body,
    FilmJobService jobService,
    IUserContext user,
    UserDatabaseService userDb,
    IUserApiKeyProvider keys,
    IOptions<PageToMovieOptions> opts)
    {
    if (await AuthGate.RequirePersonalGrokKeyAsync(user, userDb, opts, ApiRuntime.UseFakes, keys) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        var job = await jobService.StartBookImportAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.SkipPrepare
                ? "Queued screenplay draft from book"
                : "Queued book import (prepare + screenplay)",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsStage1(StartStage1Request body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        var job = await jobService.StartStage1Async(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued Stage 1 (C# Grok chat)",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsStage2(StartStage2Request body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        var job = await jobService.StartStage2Async(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued Stage 2 (C# planner)",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsYoutubeUpload(HttpRequest request,
    FilmJobService jobService,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    // Shared channel OAuth lives on the server — clients only upload via this UI/API path.
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    try
    {
        string? projectId = null;
        string? title = null;
        string? description = null;
        string? privacyStatus = null;
        IFormFile? file = null;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            projectId = form[ApiText.ProjectIdKey].ToString();
            title = form["title"].ToString();
            description = form[ApiText.DescriptionKey].ToString();
            privacyStatus = form["privacyStatus"].ToString();
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        else
        {
            var body = await request.ReadFromJsonAsync<StartYouTubeUploadRequest>(cancellationToken: ct);
            if (body is not null)
            {
                projectId = body.ProjectId;
                title = body.Title;
                description = body.Description;
                privacyStatus = body.PrivacyStatus;
            }
        }

        if (string.IsNullOrWhiteSpace(projectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });

        if (file is not null && file.Length > 0)
        {
            var pDir = await store.GetProjectDirAsync(projectId, ct);
            var videoDir = Path.Combine(pDir, ApiText.AssetsFolder, ApiText.VideoFolder);
            Directory.CreateDirectory(videoDir);
            var savePath = Path.Combine(videoDir, "wip_movie.mp4");
            await using var stream = File.Create(savePath);
            await file.CopyToAsync(stream, ct);
        }

        var req = new StartYouTubeUploadRequest
        {
            ProjectId = projectId,
            Title = title,
            Description = description,
            PrivacyStatus = privacyStatus ?? "unlisted",
        };

        var job = await jobService.StartYouTubeUploadAsync(req);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued YouTube upload",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsClipAutoReview(StartClipAutoReviewRequest body, FilmJobService jobService)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || body.Scene <= 0 || body.Clip <= 0)
            return Results.BadRequest(new { ok = false, error = "projectId, scene, clip required" });
        var job = await jobService.StartClipAutoReviewAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued AI review S{body.Scene:D2}C{body.Clip:D2}",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsClipAutoReviewBatch(StartClipAutoReviewBatchRequest body, FilmJobService jobService)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        var job = await jobService.StartClipAutoReviewBatchAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.Scene is int sn && sn > 0
                ? $"Queued batch AI review S{sn:D2}"
                : "Queued batch AI review (all scenes)",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}

    private static async Task<IResult> PostJobsSceneMusic(StartSceneMusicGenRequest? body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = ApiText.ProjectIdRequired });
        if (body.Scene <= 0)
            return Results.BadRequest(new { ok = false, error = "scene required" });
        var job = await jobService.StartSceneMusicGenAsync(body.ProjectId.Trim(), body.Scene, body.Model, body.IsVocal);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued background music for Scene {body.Scene:D2}",
            job,
        });
    }
    catch (Exception ex)
    {
        return ApiEndpointHelpers.JobStartError(ex, jobService);
    }
}
}
