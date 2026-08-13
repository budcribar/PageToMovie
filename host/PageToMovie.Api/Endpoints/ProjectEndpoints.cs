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

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        // <summary>Download project folder as zip (logged in user / operator).</summary>
        app.MapGet("/api/projects/{id}/export", GetProjectsIdExport);
        // <summary>
        // User-mode import: any signed-in user imports a project zip into their OWN namespace. The owner is
        // forced to the caller (the zip's original owner is ignored) so a user can't import into — or
        // overwrite — someone else's project. Multipart field <c>file</c>; optional <c>overwrite</c>=true.
        // </summary>
        app.MapPost("/api/projects/import", PostProjectsImport);
        app.MapGet("/api/projects", GetProjects);
        app.MapPost("/api/projects/{id}/activate", PostProjectsIdActivate);
        // <summary>Create a new project folder under projects/ and make it active.</summary>
        app.MapPost("/api/projects", PostProjects);
        // <summary>Delete a project folder under projects/.</summary>
        app.MapDelete("/api/projects/{id}", DeleteProjectsId);
        app.MapGet("/api/projects/{id}/config", GetProjectsIdConfig);
        app.MapPut("/api/projects/{id}/config", PutProjectsIdConfig);
        app.MapGet("/api/projects/{projectId}/book-images/{fileName}", GetProjectsProjectIdBookImagesFileName);
        app.MapPost("/api/projects/{id}/visibility", PostProjectsIdVisibility);
        // <summary>Persist product path: full | simple-voice (library book + narrator voice).</summary>
        app.MapPost("/api/projects/{id}/studio-path", PostProjectsIdStudioPath);
        app.MapPost("/api/projects/{id}/rename", PostProjectsIdRename);
        // <summary>
        // Create a real, persisted, single-use invite (48h) for a project and email the recipient a
        // /join link. Owner or admin only. Never reveals whether a target email has an account.
        // </summary>
        app.MapPost("/api/projects/{id}/invites", PostProjectsIdInvites);
        // <summary>Public forkable movies (visibility "Open"/"PublicForkable") — the source list for the
        // Easy Start "story in your voice" picker. Any signed-in user can see them to fork.</summary>
        app.MapGet("/api/projects/forkable", GetProjectsForkable);
        // <summary>1-click community fork endpoint for Open (Public Forkable) projects.</summary>
        app.MapPost("/api/projects/{id}/fork", PostProjectsIdFork);
        // <summary>
        // Non-destructively augments blueprint.clips.grok.json with AI-composed scene music prompts across all scenes.
        // </summary>
        app.MapPost("/api/projects/{id}/augment-music", PostProjectsIdAugmentMusic);
        // <summary>H8 — project-scoped takes stats for Cost page (editors; aggregates only for this project).</summary>
        app.MapGet("/api/projects/{id}/takes-telemetry", GetProjectsIdTakesTelemetry);
        // <summary>Resolution already used by this project's on-disk clips, if consistent — null once no clips exist yet.</summary>
        app.MapGet("/api/projects/{id}/resolution-lock", GetProjectsIdResolutionLock);
        // <summary>Structured end-credits card content (title/author/software/site) — the client renders these
        // exact strings deterministically instead of asking a generative model to draw text.</summary>
        app.MapGet("/api/projects/{id}/credits-content", GetProjectsIdCreditsContent);
        // <summary>Stream or download the WIP full movie for client external editor / playback.</summary>
        app.MapGet("/api/projects/{id}/movie", GetProjectsIdMovie);
        // <summary>Stream the WIP full movie (authenticated). Public share uses /api/share/{{token}}.</summary>
        app.MapGet("/api/projects/{id}/movie/wip", GetProjectsIdMovieWip);
        // <summary>Create or reuse a public share link for the WIP movie (login required).</summary>
        app.MapPost("/api/projects/{id}/movie/wip/share", PostProjectsIdMovieWipShare);
        // All dialogue lines (every speaker) per scene, straight from the blueprint — the "script" side of
        // the dialogue-timing review. No STT here; the client runs that pass and posts the result below.
        app.MapGet("/api/projects/{id}/dialogue/lines", GetProjectsIdDialogueLines);
        // Cached dialogue-timing review (STT vs script per scene). Computed once per scene by the client.
        app.MapGet("/api/projects/{id}/dialogue/timing", GetProjectsIdDialogueTiming);
        // Merge one analyzed/edited scene into the cache (scenes are reviewed independently).
        app.MapPost("/api/projects/{id}/dialogue/timing/scene", PostProjectsIdDialogueTimingScene);
        // <summary>Most recent YouTube upload for this project's WIP movie, if any.</summary>
        app.MapGet("/api/projects/{id}/movie/youtube", GetProjectsIdMovieYoutube);
        app.MapGet("/api/projects/{id}/movie/wip/meta", GetProjectsIdMovieWipMeta);
        // <summary>
        // Register a stitched studio cut: film_build.v1 (EDL + studio.sha256) on project disk + stage commit.
        // Client stitch should POST after producing the WIP blob; server may also call when bytes land.
        // </summary>
        app.MapPost("/api/projects/{id}/film-build", PostProjectsIdFilmBuild);
        app.MapGet("/api/projects/{id}/film-build", GetProjectsIdFilmBuild);
        // <summary>Create a learning package from current project artifacts (Stage‑1 + film_build + publish).</summary>
        app.MapPost("/api/projects/{id}/learning-package", PostProjectsIdLearningPackage);
        app.MapGet("/api/projects/{id}/learning-packages", GetProjectsIdLearningPackages);
        return app;
    }

    private static async Task<IResult> GetProjectsIdExport(string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    ProjectArchiveService archives,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var exp = await archives.ExportAsync(id, ct);
        return Results.File(
            exp.Stream,
            exp.ContentType,
            exp.FileName,
            enableRangeProcessing: false);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsImport(HttpRequest req,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    ProjectArchiveService archives,
    ProjectStore store,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (string.IsNullOrWhiteSpace(user.UserId))
        return Results.Json(new { ok = false, error = "sign in required" },
            statusCode: StatusCodes.Status401Unauthorized);
    if (!req.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form with file required" });

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "file required (project zip)" });

    // Optional target name — import under a name of the caller's choosing instead of the zip's slug
    // (forceOwnerUserId still re-namespaces it under the caller, so only the slug is taken from this).
    var name = form["name"].ToString();
    if (string.IsNullOrWhiteSpace(name)) name = form[ApiText.ProjectIdKey].ToString();

    var overwrite = string.Equals(form[ApiText.OverwriteKey].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(form[ApiText.OverwriteKey].ToString(), "1", StringComparison.OrdinalIgnoreCase);

    try
    {
        await using var stream = file.OpenReadStream();
        var result = await archives.ImportAsync(
            stream,
            preferredId: string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            overwrite: overwrite,
            targetUserId: user.UserId,
            forceOwnerUserId: user.UserId,
            ct: ct);

        // A custom name only re-slugged the folder/id above; also set the display title to match so
        // the imported project shows the chosen name, not the zip's original title.
        var active = result.Project;
        if (result.Ok && !string.IsNullOrWhiteSpace(name))
        {
            try { active = await store.RenameProjectAsync(result.ProjectId, name.Trim(), ct); }
            catch { /* id/slug already correct; title is best-effort */ }
        }

        return Results.Ok(new
        {
            ok = true,
            projectId = result.ProjectId,
            active,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjects(ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb,
    CancellationToken ct)
    {
    // Project inventory is not public — requires sign-in (prevents anonymous enumeration).
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var all = await store.ListProjectsAsync(ct);

    IReadOnlyList<ProjectInfo> list;
    if (user.IsAdmin)
    {
        list = all;
    }
    else
    {
        // Resolve all known identities for this account so projects created under a
        // previous handle / email-shaped id (folder budcribarmsn_com vs budcribar) still appear.
        UserEntity? me = null;
        try
        {
            me = await userDb.GetUserByIdAsync(user.UserId, ct).ConfigureAwait(false)
                 ?? await userDb.GetUserByUsernameAsync(user.UserId, ct).ConfigureAwait(false);
        }
        catch { /* offline */ }

        var aliases = ProjectOwnership.CollectAliases(
            user.UserId,
            canonicalUserId: me?.UserId,
            username: me?.Username,
            email: me?.Email);
        list = all.Where(p => ProjectOwnership.IsOwnedBy(p, aliases)).ToList();

        // Self-heal: if folder/owner field used a stale alias, rewrite ownerUserId to canonical id
        // so future filters and admin tools stay consistent. Best-effort; never delete.
        var canonical = !string.IsNullOrWhiteSpace(me?.UserId) ? me.UserId.Trim() : user.UserId.Trim();
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            foreach (var p in list)
            {
                if (string.Equals(p.OwnerUserId, canonical, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    await store.RepairProjectOwnerAsync(p.Id, canonical, ct).ConfigureAwait(false);
                    p.OwnerUserId = canonical;
                }
                catch { /* non-fatal */ }
            }
        }
    }

    var userActiveId = await userDb.GetUserActiveProjectAsync(user.UserId, ct);
    // Per-user active only — never fall back to process-wide store.ActiveProjectId
    // (that is the last project any account activated and leaks across logins).
    var active = ProjectOwnership.PickActiveInList(list, userActiveId);
    if (!string.IsNullOrWhiteSpace(userActiveId)
        && (active is null
            || !string.Equals(active.Id, userActiveId, StringComparison.OrdinalIgnoreCase)))
    {
        // Stale pointer (deleted project or another account's id) — clear so next login is clean.
        try { await userDb.SetUserActiveProjectAsync(user.UserId, active?.Id, ct); }
        catch { /* non-fatal */ }
    }
    return Results.Ok(new { ok = true, active, projects = list });
}

    private static async Task<IResult> PostProjectsIdActivate(string id,
    ProjectStore store,
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        // Non-admins may only activate projects they own.
        if (!user.IsAdmin)
        {
            UserEntity? me = null;
            try
            {
                me = await userDb.GetUserByIdAsync(user.UserId, ct).ConfigureAwait(false)
                     ?? await userDb.GetUserByUsernameAsync(user.UserId, ct).ConfigureAwait(false);
            }
            catch { /* offline */ }
            var aliases = ProjectOwnership.CollectAliases(
                user.UserId, canonicalUserId: me?.UserId, username: me?.Username, email: me?.Email);
            var info = await store.GetProjectAsync(id, ct).ConfigureAwait(false);
            if (info is null)
                return Results.NotFound(new { ok = false, error = "Project not found" });
            if (!ProjectOwnership.IsOwnedBy(info, aliases))
                return Results.Json(new { ok = false, error = "Not your project" },
                    statusCode: StatusCodes.Status403Forbidden);
        }

        var p = await store.ActivateAsync(id, ct);
        await userDb.SetUserActiveProjectAsync(user.UserId, p.Id, ct);
        return Results.Ok(new { ok = true, active = p });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjects(CreateProjectRequest? body,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb,
    CancellationToken ct)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        var name = body?.Name ?? body?.Id ?? body?.Title ?? "";
        var title = body?.Title;
        // Prefer stable DB UserId so folder + ownerUserId stay consistent across re-login.
        var ownerId = user.UserId;
        try
        {
            var me = await userDb.GetUserByIdAsync(user.UserId, ct).ConfigureAwait(false)
                     ?? await userDb.GetUserByUsernameAsync(user.UserId, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(me?.UserId))
                ownerId = me.UserId.Trim();
        }
        catch { /* use JWT id */ }

        var p = await store.CreateProjectAsync(
            name, title, ct, ownerUserId: ownerId, studioPath: body?.StudioPath ?? StudioPath.Full);
        await userDb.SetUserActiveProjectAsync(user.UserId, p.Id, ct);
        var all = await store.ListProjectsAsync(ct);
        var aliases = ProjectOwnership.CollectAliases(ownerId, user.UserId);
        var list = user.IsAdmin
            ? all
            : all.Where(x => ProjectOwnership.IsOwnedBy(x, aliases)).ToList();
        return Results.Ok(new
        {
            ok = true,
            active = p,
            projects = list,
            message = $"Created project “{p.Label ?? p.Title ?? p.Id}”",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> DeleteProjectsId(string id,
    ProjectStore store,
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.DeleteProjectAsync(id, ct);

        // Same non-public-inventory + per-user active-project rules as GET /api/projects —
        // this response used to leak every user's projects and whichever project any other
        // user last activated process-wide.
        var all = await store.ListProjectsAsync(ct);
        IReadOnlyList<ProjectInfo> list;
        if (user.IsAdmin)
        {
            list = all;
        }
        else
        {
            UserEntity? me = null;
            try
            {
                me = await userDb.GetUserByIdAsync(user.UserId, ct).ConfigureAwait(false)
                     ?? await userDb.GetUserByUsernameAsync(user.UserId, ct).ConfigureAwait(false);
            }
            catch { /* offline */ }
            var aliases = ProjectOwnership.CollectAliases(
                user.UserId, canonicalUserId: me?.UserId, username: me?.Username, email: me?.Email);
            list = all.Where(p => ProjectOwnership.IsOwnedBy(p, aliases)).ToList();
        }

        var userActiveId = await userDb.GetUserActiveProjectAsync(user.UserId, ct);
        var active = ProjectOwnership.PickActiveInList(list, userActiveId);
        if (!string.IsNullOrWhiteSpace(userActiveId)
            && (active is null
                || !string.Equals(active.Id, userActiveId, StringComparison.OrdinalIgnoreCase)))
        {
            try { await userDb.SetUserActiveProjectAsync(user.UserId, active?.Id, ct); }
            catch { /* non-fatal */ }
        }
        return Results.Ok(new
        {
            ok = true,
            deleted = id,
            active,
            projects = list,
            message = $"Deleted project “{id}”",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdConfig(string id, ProjectStore store, CancellationToken ct)
    {
    try
    {
        var cfg = await store.GetConfigAsync(id, ct);
        var projectDir = await store.GetProjectDirAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, projectDir, config = cfg });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PutProjectsIdConfig(string id,
    HttpRequest req,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        if (!user.IsAdmin)
        {
            string[] modelKeys =
            [
                "video_model_name", "image_model_name", "planning_model_name", "vision_model_name",
                "video_review_model_name", "audio_model_name", "voice_model_name", "tts_model_name"
            ];
            foreach (var key in modelKeys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                    continue;
                var mid = el.GetString();
                if (SupportedModelCatalog.IsLabModel(mid))
                {
                    return Results.BadRequest(new
                    {
                        ok = false,
                        error = $"Model '{mid}' is lab-mode (admin-only). Choose a production catalog model.",
                    });
                }
            }
        }
        var saved = await store.SaveConfigAsync(id, doc.RootElement, ct);
        return Results.Ok(new { ok = true, projectId = id, config = saved });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsProjectIdBookImagesFileName(HttpContext ctx, string projectId, string fileName, ProjectStore store, CancellationToken ct)
    {
    var projectDir = await store.GetProjectDirAsync(projectId, ct);
    var dir = Path.Combine(projectDir, "source", "book_images");
    var file = Path.GetFileName(fileName);
    var path = Path.Combine(dir, file);
    return ApiEndpointHelpers.ServeCachedFile(ctx, path, immutable: true);
}

    private static async Task<IResult> PostProjectsIdVisibility(string id,
    ProjectVisibilityRequest req,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can change visibility mode.", ct) is { } forbidden)
        return forbidden;

    var proj = await store.SetProjectVisibilityModeAsync(id, req.VisibilityMode, ct);
    await books.SetProjectVisibilityAsync(proj.OwnerUserId ?? user.UserId, id, proj.VisibilityMode.ToString(), ct);
    return Results.Ok(new { ok = true, projectId = proj.Id, visibilityMode = proj.VisibilityMode.ToString() });
}

    private static async Task<IResult> PostProjectsIdStudioPath(string id,
    SetStudioPathRequest? body,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can change studio path.", ct) is { } forbidden)
        return forbidden;

    var proj = await store.SetProjectStudioPathAsync(id, body?.StudioPath ?? StudioPath.Full, ct);
    return Results.Ok(new { ok = true, projectId = proj.Id, studioPath = proj.StudioPath });
}

    private static async Task<IResult> PostProjectsIdRename(string id,
    RenameProjectRequest? body,
    ProjectStore store,
    ProjectArchiveService archives,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can rename this project.", ct) is { } forbidden)
        return forbidden;

    try
    {
        var title = body?.Title ?? body?.Name ?? "";
        // Re-slug rename: export → import under the new id → delete old (folder + display name both
        // change). Degrades to a display-name-only change when the slug is unchanged.
        var result = await archives.RenameViaReimportAsync(id, title, force: false, ct: ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = result.NewId,
            previousProjectId = result.OldId,
            reSlugged = result.ReSlugged,
            title = result.Project?.Title ?? title,
            label = result.Project?.Label ?? title,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdInvites(string id,
    SendInviteApiRequest? body,
    ProjectStore store,
    ProjectInviteService invites,
    UserDatabaseService userDb,
    IEmailSender email,
    IAdminAuthService auth,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await ApiEndpointHelpers.RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can invite collaborators.", ct) is { } forbidden)
            return forbidden;

        var targetHandle = string.IsNullOrWhiteSpace(body?.TargetHandle) ? null : body.TargetHandle.TrimStart('@').Trim();
        var targetEmail = string.IsNullOrWhiteSpace(body?.TargetEmail) ? null : body.TargetEmail.Trim();
        if (targetHandle is null && targetEmail is null)
            return Results.BadRequest(new { ok = false, error = "A handle or email is required." });

        // Resolve a handle to its email so we can actually deliver the invite — the client
        // never sees this; the /api/users/search endpoint already keeps raw emails server-side.
        if (targetHandle is not null && targetEmail is null)
        {
            var target = await userDb.GetUserByUsernameAsync(targetHandle);
            targetEmail = target?.Email;
        }

        var invite = await invites.CreateAsync(id, user.UserId ?? "unknown", targetHandle, targetEmail, ct);
        var link = auth is AdminAuthService concrete
            ? concrete.BuildAppLink($"/join?token={Uri.EscapeDataString(invite.Token)}")
            : $"/join?token={Uri.EscapeDataString(invite.Token)}";

        if (!string.IsNullOrWhiteSpace(targetEmail))
        {
            var subject = "You're invited to fork a PageToMovie project";
            var text = $"{user.UserId} invited you to fork \"{id}\" on PageToMovie.\n\n{link}\n\nThis link expires in 48 hours.";
            var html = $"<p><strong>{System.Net.WebUtility.HtmlEncode(user.UserId)}</strong> invited you to fork " +
                       $"\"{System.Net.WebUtility.HtmlEncode(id)}\" on PageToMovie.</p>" +
                       $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">Accept invite</a></p>" +
                       "<p>This link expires in 48 hours.</p>";
            await email.SendAsync(targetEmail, subject, html, text, ct);
        }

        return Results.Ok(new
        {
            ok = true,
            // Returned for the inviter's own "copy link" convenience — the recipient's copy
            // comes via email above, not by exposing whether their account/email exists.
            inviteUrl = link,
            delivered = !string.IsNullOrWhiteSpace(targetEmail),
            expiresAt = invite.ExpiresAt,
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsForkable(ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var all = await store.ListProjectsAsync(ct);
    var forkable = all
        .Where(p => p.VisibilityMode == ProjectVisibility.Public
                    // Exclude forks themselves — only original forkable sources are pickable stories.
                    && string.IsNullOrWhiteSpace(p.ParentProjectId))
        .OrderBy(p => p.Label ?? p.Title ?? p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new
        {
            id = p.Id,
            title = p.Label ?? p.Title ?? p.Id,
            ownerUserId = p.OwnerUserId,
        })
        .ToList();
    return Results.Ok(new { ok = true, projects = forkable });
}

    private static async Task<IResult> PostProjectsIdFork(string id,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    try
    {
        var fork = await store.ForkProjectAsync(id, user.UserId, ct: ct);
        await books.LinkForkAsync(id, user.UserId, fork.Id, invitationAuthorized: false, ct);
        return Results.Ok(new { ok = true, id = fork.Id, title = fork.Title, parentProjectId = fork.ParentProjectId, visibilityMode = fork.VisibilityMode });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdAugmentMusic(string id,
    ProjectStore store,
    SceneMusicCompositionService composer,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    string? model = null,
    CancellationToken ct = default)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    var pDir = await store.GetProjectDirAsync(id, ct);
    if (string.IsNullOrWhiteSpace(pDir) || !Directory.Exists(pDir))
        return Results.NotFound(new { ok = false, error = "Project not found." });

    var success = await composer.AugmentProjectMusicAsync(pDir, model, ct);
    if (!success)
        return Results.BadRequest(new { ok = false, error = "Music score augmentation failed. Ensure blueprint.clips.grok.json exists." });

    store.TriggerAutoGitCommit(id, "Augment blueprint with AI music score prompts");
    return Results.Ok(new { ok = true, message = "Successfully augmented blueprint with AI background music scores." });
}

    private static async Task<IResult> GetProjectsIdTakesTelemetry(string id,
    ProjectStore store,
    UserDatabaseService userDb,
    CancellationToken ct)
    {
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        var project = await userDb.GetTakesTelemetryStatsAsync(id, ct);
        return Results.Ok(new { ok = true, project });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = true, project = new TakesTelemetryStats { Notes = "unavailable: " + ex.Message } });
    }
}

    private static async Task<IResult> GetProjectsIdResolutionLock(string id, FilmJobService jobs, CancellationToken ct)
    {
    try
    {
        var locked = await jobs.GetLockedResolutionAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, locked });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsIdCreditsContent(string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try { return Results.Ok(store.BuildCreditsContent(id)); }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
}

    private static async Task<IResult> GetProjectsIdMovie(string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var path = store.ResolveWipMoviePath(id);
        if (path is null || !File.Exists(path))
        {
            var pDir = await store.GetProjectDirAsync(id, ct);
            var altWip = Path.Combine(pDir, ApiText.AssetsFolder, ApiText.VideoFolder, "wip_movie.mp4");
            if (File.Exists(altWip)) path = altWip;
        }
        if (path is null || !File.Exists(path))
            return Results.NotFound(new { ok = false, error = "Full movie file not found on server — build or play movie first." });
        return Results.File(path, SpecializedMimeType.VideoMp4.ToMimeTypeString(), fileDownloadName: $"{id}_full.mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsIdMovieWip(string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var path = store.ResolveWipMoviePath(id);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "WIP movie not found — Play first so the cut is built" });
        return Results.File(path, SpecializedMimeType.VideoMp4.ToMimeTypeString(), enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdMovieWipShare(string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    MediaShareService shares,
    HttpContext http,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var rec = await shares.EnsureWipShareAsync(id, user.UserId, ct: ct);
        var path = $"/api/share/{Uri.EscapeDataString(rec.Token)}";
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        return Results.Ok(new
        {
            ok = true,
            token = rec.Token,
            path,
            url = baseUrl + path,
            expiresAt = rec.ExpiresAt,
            projectId = rec.ProjectId,
            kind = rec.Kind,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdDialogueLines(string id, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    using var blueprint = await store.LoadBlueprintAsync(id, ct);
    if (blueprint is null)
        return Results.Ok(new { ok = true, scenes = Array.Empty<object>() });

    var clips = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, null);
    var scenes = clips
        .GroupBy(c => c.Scene)
        .OrderBy(g => g.Key)
        .Select(g => new
        {
            scene = g.Key,
            lines = g.OrderBy(c => c.Clip)
                     .SelectMany(c => c.Lines.Select(l => new { clip = c.Clip, speaker = l.CharacterKey, text = l.Text }))
                     .ToList(),
        })
        .Where(s => s.lines.Count > 0)
        .ToList();

    return Results.Ok(new { ok = true, scenes });
}

    private static async Task<IResult> GetProjectsIdDialogueTiming(string id, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var path = Path.Combine(await store.GetProjectDirAsync(id, ct), ApiText.AssetsFolder, "alignment", "dialogue_timing.json");
    if (!File.Exists(path))
        return Results.Ok(new { ok = true, timing = (DialogueTimingDoc?)null });
    try
    {
        var json = await File.ReadAllTextAsync(path, ct);
        var data = System.Text.Json.JsonSerializer.Deserialize<DialogueTimingDoc>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Results.Ok(new { ok = true, timing = data });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}

    private static async Task<IResult> PostProjectsIdDialogueTimingScene(string id, DialogueTimingScene body, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (body is null || body.Scene <= 0)
        return Results.BadRequest(new { ok = false, error = "scene body with a scene number required" });

    var dir = Path.Combine(await store.GetProjectDirAsync(id, ct), ApiText.AssetsFolder, "alignment");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "dialogue_timing.json");
    var webOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

    DialogueTimingDoc doc;
    if (File.Exists(path))
    {
        try { doc = System.Text.Json.JsonSerializer.Deserialize<DialogueTimingDoc>(await File.ReadAllTextAsync(path, ct), webOpts) ?? new(); }
        catch { doc = new(); }
    }
    else doc = new();

    doc.ProjectId = id;
    doc.GeneratedAtUtc = DateTime.UtcNow;
    doc.Scenes.RemoveAll(s => s.Scene == body.Scene);
    doc.Scenes.Add(body);
    doc.Scenes.Sort((a, b) => a.Scene.CompareTo(b.Scene));

    var writeOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
    await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(doc, writeOpts) + "\n", ct);
    return Results.Ok(new { ok = true, scene = body.Scene, rows = body.Rows?.Count ?? 0 });
}

    private static async Task<IResult> GetProjectsIdMovieYoutube(string id, ProjectStore store, CancellationToken ct)
    {
    try
    {
        var info = await store.GetYouTubeUploadInfoAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, upload = info });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsIdMovieWipMeta(string id, ProjectStore store)
    {
    try
    {
        var f = store.AssessWipFreshness(id);
        // url must be a string (or null) — never a bool (breaks System.Text.Json on the client).
        string? wipUrl = f.Exists
            ? $"/api/projects/{Uri.EscapeDataString(id)}/movie/wip"
            : null;
        return Results.Ok(new
        {
            ok = true,
            exists = f.Exists,
            stale = f.Stale,
            canBuild = f.CanBuild,
            reason = f.Reason,
            projectId = id,
            path = f.Path,
            bytes = f.Bytes,
            updatedAt = f.UpdatedAt,
            staleScenes = f.StaleScenes,
            url = wipUrl,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdFilmBuild(string id,
    HttpRequest request,
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
        var body = await request.ReadFromJsonAsync<FilmBuildRegisterRequest>(cancellationToken: ct);
        if (body is null)
            return Results.BadRequest(new { ok = false, error = "JSON body required" });

        var sha = (body.StudioSha256 ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(sha) && body.HashFromServerWip == true)
        {
            var docFromFile = await FilmBuildService.RegisterFromWipFileAsync(store, id, body.StudioPath, ct);
            if (docFromFile is null)
                return Results.BadRequest(new { ok = false, error = "WIP file not found to hash" });
            return Results.Ok(new { ok = true, filmId = docFromFile.FilmId, path = FilmBuildService.RelativePath, filmBuild = docFromFile });
        }

        if (string.IsNullOrWhiteSpace(sha) || sha.Length < 32)
            return Results.BadRequest(new { ok = false, error = "studioSha256 required (or hashFromServerWip=true)" });

        var segments = body.Segments?.Select((s, i) => new FilmBuildSegment
        {
            Index = s.Index >= 0 ? s.Index : i,
            Scene = s.Scene,
            Clip = s.Clip,
            Take = s.Take,
            TStart = s.TStart,
            TEnd = s.TEnd,
            Src = s.Src ?? "",
            SrcSha256 = s.SrcSha256,
            Sidecar = s.Sidecar,
        }).ToList();

        var doc = await FilmBuildService.RegisterAsync(
            store,
            id,
            sha,
            body.DurationSeconds,
            segments,
            body.ByteLength,
            string.IsNullOrWhiteSpace(body.AssemblyWhere) ? "client" : body.AssemblyWhere,
            ct);

        if (!string.IsNullOrWhiteSpace(body.StudioPath))
            doc.Studio.Path = body.StudioPath;

        await FilmBuildService.WriteAsync(await store.GetProjectDirAsync(id, ct), doc, ct);

        return Results.Ok(new
        {
            ok = true,
            filmId = doc.FilmId,
            path = FilmBuildService.RelativePath,
            filmBuild = doc,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdFilmBuild(string id,
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
        var doc = await FilmBuildService.TryReadAsync(await store.GetProjectDirAsync(id, ct), ct);
        if (doc is null)
            return Results.Ok(new { ok = true, exists = false, path = FilmBuildService.RelativePath });
        return Results.Ok(new { ok = true, exists = true, path = FilmBuildService.RelativePath, filmBuild = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdLearningPackage(string id,
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
        string? workspace = null;
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "prompts")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "evals")))
                {
                    workspace = dir.FullName;
                    break;
                }
            }
        }
        catch { /* ignore */ }

        var result = await LearningPackageService.CreateFromProjectAsync(store, id, workspaceRoot: workspace, ct: ct);
        return Results.Ok(new
        {
            ok = true,
            packageId = result.PackageId,
            path = result.ProjectRelativePath,
            labPath = result.LabRelativePath,
            publishPath = result.PublishPath,
            filmId = result.FilmId,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdLearningPackages(string id,
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
        var root = LearningPackageService.PackagesRoot(await store.GetProjectDirAsync(id, ct));
        var list = new List<object>();
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
            {
                var pkg = Path.Combine(dir, "package.json");
                if (!File.Exists(pkg)) continue;
                list.Add(new
                {
                    packageId = Path.GetFileName(dir),
                    path = Path.Combine("artifacts", "learning_packages", Path.GetFileName(dir)).Replace('\\', '/'),
                });
            }
        }
        return Results.Ok(new { ok = true, packages = list });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}
