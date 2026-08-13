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

public static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        // <summary>Public gallery: demos on YouTube (no login). sort=top|new (default top by upvotes).</summary>
        app.MapGet("/api/demos", GetDemos);
        // <summary>Public metadata for a public demo; owner/admin can see pending.</summary>
        app.MapGet("/api/demos/{demoId}", GetDemosDemoId);
        // <summary>Star / upvote a public demo (signed-in). Idempotent. No self-upvote.</summary>
        app.MapPost("/api/demos/{demoId}/upvote", PostDemosDemoIdUpvote);
        // <summary>
        // Feature 11: fork the studio project behind a public gallery film (lightweight package, no video).
        // Requires sign-in. Visibility modes are not fully productized yet — any public demo with a
        // still-existing source project is forkable from the gallery.
        // </summary>
        app.MapPost("/api/demos/{demoId}/fork", PostDemosDemoIdFork);
        // <summary>Remove star / upvote (signed-in).</summary>
        app.MapDelete("/api/demos/{demoId}/upvote", DeleteDemosDemoIdUpvote);
        // <summary>
        // Demo playback: redirect to YouTube when published there (source of truth).
        // Local MP4 only while staging (owner/admin) before upload completes.
        // </summary>
        app.MapGet("/api/demos/{demoId}/video", GetDemosDemoIdVideo);
        app.MapPost("/api/demos", PostDemos);
        // <summary>Report a public demo (any viewer; optional login). Auto-removed after 3 reports.</summary>
        app.MapPost("/api/demos/{demoId}/report", PostDemosDemoIdReport);
        // <summary>Delete a demo (owner or admin).</summary>
        app.MapDelete("/api/demos/{demoId}", DeleteDemosDemoId);
        return app;
    }

    private static async Task<IResult> GetDemos(DemoCatalogService demos,
    DemoUpvoteService upvotes,
    ProjectStore store,
    IUserContext user,
    YouTubeChannelGallerySync channelSync,
    int? take,
    string? sort,
    CancellationToken ct)
    {
    // YouTube channel is SoT: quietly refresh catalog when connected (throttled).
    try { await channelSync.EnsureSyncedAsync(force: false, ct: ct); }
    catch { /* non-fatal for public list */ }

    var list = (await demos.ListPublicAsync(take ?? 50, ct)).ToList();
    var ids = list.Select(d => d.Id).ToList();
    var counts = await upvotes.GetCountsAsync(ids, ct);
    var mine = await upvotes.GetUpvotedSetAsync(user.UserId, ids, ct);

    var visibilityMap = new Dictionary<string, string>();
    var forkableProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in list.Where(x => !string.IsNullOrWhiteSpace(x.ProjectId)))
    {
        try
        {
            var proj = await store.GetProjectAsync(d.ProjectId, ct);
            if (proj is not null)
            {
                visibilityMap[d.Id] = proj.VisibilityMode.ToString();
                forkableProjectIds.Add(d.ProjectId);
            }
        }
        catch { /* project lookup is best-effort for the public gallery */ }
    }

    var sortKey = (sort ?? "top").Trim().ToLowerInvariant();
    IEnumerable<DemoCatalogService.DemoEntry> ordered = sortKey switch
    {
        "new" => list.OrderByDescending(d => d.CreatedAt),
        _ => list
            .OrderByDescending(d => counts.GetValueOrDefault(d.Id))
            .ThenByDescending(d => d.CreatedAt),
    };

    return Results.Ok(new
    {
        ok = true,
        sort = sortKey is "new" ? "new" : "top",
        youtubeSync = new
        {
            lastSuccessUtc = channelSync.LastSuccessUtc,
            lastError = channelSync.LastError,
        },
        demos = ordered.Select(d => ApiEndpointHelpers.DemoPublicDto(
            d,
            counts.GetValueOrDefault(d.Id),
            mine.Contains(d.Id),
            canFork: d.ProjectId is { Length: > 0 } pid && forkableProjectIds.Contains(pid),
            visibilityMode: visibilityMap.GetValueOrDefault(d.Id, "Private"))),
    });
}

    private static async Task<IResult> GetDemosDemoId(string demoId,
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    IUserContext user,
    CancellationToken ct)
    {
    var d = await demos.TryGetAsync(demoId, ct);
    if (d is null)
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });
    if (!demos.CanUserViewVideo(d, user.UserId, user.IsAdmin))
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });
    var count = await upvotes.GetCountAsync(demoId, ct);
    var me = await upvotes.HasUpvotedAsync(demoId, user.UserId, ct);
    if (user.IsAdmin)
    {
        return Results.Ok(new
        {
            ok = true,
            demo = ApiEndpointHelpers.DemoAdminDto(d),
            upvoteCount = count,
            upvotedByMe = me,
        });
    }
    return Results.Ok(new { ok = true, demo = ApiEndpointHelpers.DemoPublicDto(d, count, me) });
}

    private static async Task<IResult> PostDemosDemoIdUpvote(string demoId,
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var d = await demos.TryGetAsync(demoId, ct);
    if (d is null || !DemoCatalogService.IsPubliclyStreamable(d))
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });
    if (!string.IsNullOrWhiteSpace(d.CreatedBy) &&
        string.Equals(d.CreatedBy, user.UserId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new
        {
            ok = false,
            error = "You can’t star your own demo.",
            code = "self_upvote",
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    await upvotes.TryAddAsync(demoId, user.UserId, ct);
    var newCount = await upvotes.GetCountAsync(demoId, ct);
    return Results.Ok(new
    {
        ok = true,
        upvoteCount = newCount,
        upvotedByMe = true,
    });
}

    private static async Task<IResult> PostDemosDemoIdFork(string demoId,
    DemoCatalogService demos,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;

    var d = await demos.TryGetAsync(demoId, ct);
    if (d is null || !DemoCatalogService.IsPubliclyStreamable(d))
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });

    var sourceId = (d.ProjectId ?? "").Trim();
    if (sourceId.Length == 0)
    {
        return Results.BadRequest(new
        {
            ok = false,
            error = "This film has no studio project to fork.",
            code = "no_source_project",
        });
    }

    try
    {
        var source = await store.GetProjectAsync(sourceId, ct);
        if (source is null)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "The studio project for this film is no longer available.",
                code = "source_missing",
            });
        }

        // A demo already confirmed public via DemoCatalogService.IsPubliclyStreamable(d) above is exactly the
        // "explicit authorization to fork" this endpoint's own doc comment promises — same bypass
        // ForkProjectAsync gives real invite-accepts, regardless of the source project's own
        // (possibly still-Private) VisibilityMode.
        var fork = await store.ForkProjectAsync(sourceId, user.UserId, isInvite: true, ct: ct);
        await books.LinkForkAsync(sourceId, user.UserId, fork.Id, invitationAuthorized: true, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = fork.Id,
            title = fork.Title,
            parentProjectId = sourceId,
            demoId,
            message = $"Created “{fork.Title ?? fork.Id}” from this film’s project.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> DeleteDemosDemoIdUpvote(string demoId,
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var d = await demos.TryGetAsync(demoId, ct);
    if (d is null || !DemoCatalogService.IsPubliclyStreamable(d))
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });

    await upvotes.TryRemoveAsync(demoId, user.UserId, ct);
    var newCount = await upvotes.GetCountAsync(demoId, ct);
    return Results.Ok(new
    {
        ok = true,
        upvoteCount = newCount,
        upvotedByMe = false,
    });
}

    private static async Task<IResult> GetDemosDemoIdVideo(string demoId,
    DemoCatalogService demos,
    IUserContext user,
    CancellationToken ct)
    {
    var d = await demos.TryGetAsync(demoId, ct);
    if (d is null)
        return Results.NotFound(new { ok = false, error = "Demo video not found" });
    if (!demos.CanUserViewVideo(d, user.UserId, user.IsAdmin))
        return Results.NotFound(new { ok = false, error = "Demo video not found" });

    // YouTube is the public source of truth — never stream server MP4 once YT id exists.
    if (!string.IsNullOrWhiteSpace(d.YoutubeId))
    {
        var url = string.IsNullOrWhiteSpace(d.YoutubeUrl)
            ? $"https://www.youtube.com/watch?v={d.YoutubeId.Trim()}"
            : d.YoutubeUrl;
        return Results.Redirect(url);
    }

    var path = demos.ResolveMoviePath(demoId);
    if (path is null)
        return Results.NotFound(new
        {
            ok = false,
            error = "Film is uploading to YouTube — try the gallery again in a moment.",
            code = "awaiting_youtube",
        });
    return Results.File(path, SpecializedMimeType.VideoMp4.ToMimeTypeString(), enableRangeProcessing: true);
}

    private static async Task<IResult> PostDemos(HttpRequest request,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    DemoCatalogService demos,
    ProjectStore store,
    MediaRegistryService media,
    UserDatabaseService userDb,
    DemoYouTubePublisherService youTubePublisher,
    CancellationToken ct)
    {
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;

    // Uploads go only through this API → shared "Page to Movie" YouTube channel (OAuth on server).
    // Creators never need YouTube Studio; admins alone connect the channel.
    try
    {
        string? title = null;
        string? description = null;
        string? projectId = null;
        var acceptedGuidelines = false;
        var madeForKids = false;
        var isAiSynthetic = true;
        string? privacyStatus = null;
        string? tagsRaw = null;
        // When true and a public demo already exists for this project/user, replace its movie
        // and re-upload to YouTube (V2 pointer replace) instead of creating a new demo entry.
        var replaceExisting = true;
        IFormFile? file = null;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            title = form["title"].ToString();
            description = form[ApiText.DescriptionKey].ToString();
            projectId = form[ApiText.ProjectIdKey].ToString();
            acceptedGuidelines = string.Equals(form[ApiText.AcceptedGuidelinesKey].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                                 || form[ApiText.AcceptedGuidelinesKey] == "1"
                                 || form[ApiText.AcceptedGuidelinesKey] == "on";
            madeForKids = string.Equals(form["madeForKids"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (bool.TryParse(form["isAiSynthetic"].ToString(), out var aiForm)) isAiSynthetic = aiForm;
            privacyStatus = form["privacyStatus"].ToString();
            tagsRaw = form["tags"].ToString();
            if (bool.TryParse(form[ApiText.ReplaceExistingKey].ToString(), out var reForm))
                replaceExisting = reForm;
            else if (string.Equals(form[ApiText.ReplaceExistingKey].ToString(), "0", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(form[ApiText.ReplaceExistingKey].ToString(), "false", StringComparison.OrdinalIgnoreCase))
                replaceExisting = false;
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        else
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t)) title = t.GetString();
            if (root.TryGetProperty(ApiText.DescriptionKey, out var d)) description = d.GetString();
            if (root.TryGetProperty(ApiText.ProjectIdKey, out var p)) projectId = p.GetString();
            if (root.TryGetProperty(ApiText.AcceptedGuidelinesKey, out var ag))
                acceptedGuidelines = ag.ValueKind == JsonValueKind.True
                                     || (ag.ValueKind == JsonValueKind.String
                                         && bool.TryParse(ag.GetString(), out var b) && b);
            if (root.TryGetProperty("madeForKids", out var mfk))
                madeForKids = mfk.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("isAiSynthetic", out var ai))
                isAiSynthetic = ai.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("privacyStatus", out var ps)) privacyStatus = ps.GetString();
            if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.String)
                tagsRaw = tg.GetString();
            if (root.TryGetProperty(ApiText.ReplaceExistingKey, out var re) && re.ValueKind == JsonValueKind.False)
                replaceExisting = false;
        }

        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        // Default unlisted: channel is operated privately; gallery embeds still work.
        // true "private" would hide films from everyone except the channel owner.
        privacyStatus = privacyStatus is "public" or "unlisted" or "private" ? privacyStatus : "unlisted";
        var tags = string.IsNullOrWhiteSpace(tagsRaw)
            ? null
            : tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (!acceptedGuidelines)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "Accept the gallery guidelines (no NSFW / illegal content) before publishing.",
                code = "guidelines_required",
            });
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "projectId is required to publish a demo",
                code = "project_required",
            });
        }

        await store.RequireProjectAsync(projectId, CancellationToken.None);
        if (!await store.CanUserPublishDemoAsync(projectId, user.UserId, user.IsAdmin, CancellationToken.None))
        {
            return Results.Json(new
            {
                ok = false,
                error =
                    "You can only publish demos for projects you own. " +
                    "Legacy projects without an owner require an admin.",
                code = "project_forbidden",
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        await demos.EnsureUserMayPublishAsync(user.UserId, user.IsAdmin, ct);

        DemoCatalogService.DemoEntry entry;
        var replacedExisting = false;

        // Item 11: re-publish → attach new movie to existing public demo and V2 YouTube replace.
        var existingPublic = replaceExisting
            ? await demos.FindPublicDemoForProjectAsync(projectId, user.UserId, ct)
            : null;
        var canReplace = existingPublic is not null
                         && !string.IsNullOrWhiteSpace(existingPublic.YoutubeId);

        if (file is not null && file.Length > 0)
        {
            var ctHeader = file.ContentType ?? "";
            if (!string.IsNullOrWhiteSpace(ctHeader) &&
                !ctHeader.Contains(ApiText.VideoFolder, StringComparison.OrdinalIgnoreCase) &&
                !ctHeader.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) &&
                !ctHeader.Contains("mp4", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    ok = false,
                    error = $"Unsupported content type for demo upload: {ctHeader}",
                    code = "invalid_media_type",
                });
            }

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var sha = MediaRegistryService.HashBytes(bytes);

            await using var stream = new MemoryStream(bytes);
            if (canReplace)
            {
                entry = await demos.AttachMovieFromStreamAsync(
                    existingPublic.Id,
                    stream,
                    title ?? existingPublic.Title,
                    description,
                    madeForKids,
                    isAiSynthetic,
                    privacyStatus,
                    tags,
                    ct);
                replacedExisting = true;
                // Always overwrite assets/movie_wip.mp4 on server disk so WIP movie matches the fresh cut!
                try
                {
                    var wipPath = Path.Combine(await store.GetProjectDirAsync(projectId, ct), ApiText.AssetsFolder, "movie_wip.mp4");
                    Directory.CreateDirectory(Path.GetDirectoryName(wipPath) ?? ".");
                    await File.WriteAllBytesAsync(wipPath, bytes, ct);
                    try
                    {
                        await FilmBuildService.RegisterAsync(
                            store,
                            projectId,
                            FilmBuildService.HashBytes(bytes),
                            durationSeconds: 0,
                            segments: null,
                            byteLength: bytes.Length,
                            assemblyWhere: "server",
                            ct: ct);
                    }
                    catch { /* non-fatal film_build */ }
                }
                catch { /* non-fatal */ }

                // Keep public; re-upload to YouTube in background (V2 replace).
                if (!string.Equals(entry.Status, DemoCatalogService.DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
                    await demos.SetStatusAsync(entry.Id, DemoCatalogService.DemoStatuses.Public, user.UserId, "Re-publish: YouTube V2 replace", ct);
                entry = await demos.TryGetAsync(entry.Id, ct) ?? entry;
                var demoIdForUpload = entry.Id;
                _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
            }
            else
            {
                entry = await demos.PublishFromStreamAsync(
                    stream,
                    title ?? projectId ?? file.FileName ?? "Demo",
                    description,
                    projectId,
                    user.UserId,
                    acceptedGuidelines: true,
                    madeForKids: madeForKids,
                    isAiSyntheticContent: isAiSynthetic,
                    privacyStatus: privacyStatus,
                    tags: tags,
                    ct: ct);

                await demos.SetStatusAsync(entry.Id, DemoCatalogService.DemoStatuses.Public, user.UserId,
                    "Auto-public: creator publish", ct);
                entry = await demos.TryGetAsync(entry.Id, ct) ?? entry;

                // Always overwrite assets/movie_wip.mp4 on server disk so WIP movie matches the fresh cut!
                try
                {
                    var wipPath = Path.Combine(await store.GetProjectDirAsync(projectId, ct), ApiText.AssetsFolder, "movie_wip.mp4");
                    Directory.CreateDirectory(Path.GetDirectoryName(wipPath) ?? ".");
                    await File.WriteAllBytesAsync(wipPath, bytes, ct);
                    try
                    {
                        await FilmBuildService.RegisterAsync(
                            store,
                            projectId,
                            FilmBuildService.HashBytes(bytes),
                            durationSeconds: 0,
                            segments: null,
                            byteLength: bytes.Length,
                            assemblyWhere: "server",
                            ct: ct);
                    }
                    catch { /* non-fatal film_build */ }
                }
                catch { /* non-fatal */ }

                try
                {
                    await media.UpsertAsync(
                        projectId,
                        $"_demos/{entry.Id}/movie.mp4",
                        sha,
                        bytes.LongLength,
                        "demo",
                        scene: null,
                        clip: null,
                        user.UserId,
                        ct);
                }
                catch { /* non-fatal */ }

                var demoIdForUpload = entry.Id;
                _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
            }
        }
        else if (canReplace)
        {
            entry = await demos.AttachMovieFromWipAsync(
                existingPublic.Id,
                projectId,
                title ?? existingPublic.Title,
                description,
                madeForKids,
                isAiSynthetic,
                privacyStatus,
                tags,
                ct);
            replacedExisting = true;
            if (!string.Equals(entry.Status, DemoCatalogService.DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
                await demos.SetStatusAsync(entry.Id, DemoCatalogService.DemoStatuses.Public, user.UserId, "Re-publish: YouTube V2 replace", ct);
            entry = await demos.TryGetAsync(entry.Id, ct) ?? entry;
            var demoIdForUpload = entry.Id;
            _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
        }
        else
        {
            entry = await demos.PublishFromWipAsync(
                projectId,
                title ?? projectId,
                description,
                user.UserId,
                acceptedGuidelines: true,
                madeForKids: madeForKids,
                isAiSyntheticContent: isAiSynthetic,
                privacyStatus: privacyStatus,
                tags: tags,
                ct: ct);
            // Always push to YouTube — gallery only lists films with a YouTube id.
            var demoIdForUpload = entry.Id;
            _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
        }

        return Results.Ok(new
        {
            ok = true,
            // No admin review queue — YouTube upload is the gate for the public wall.
            pendingReview = false,
            awaitingYouTube = string.IsNullOrWhiteSpace(entry.YoutubeId),
            autoPublic = true,
            replacedExisting,
            message = replacedExisting
                ? "Updated cut — uploading to YouTube. Gallery shows it when the upload finishes."
                : string.IsNullOrWhiteSpace(entry.YoutubeId)
                    ? "Publishing to YouTube… It appears in the gallery when the upload finishes."
                    : "Film is live on YouTube and in the gallery.",
            demo = ApiEndpointHelpers.DemoPublicDto(entry),
            pagePath = "/demo",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostDemosDemoIdReport(string demoId,
    DemoReportRequest? body,
    DemoCatalogService demos,
    IUserContext user,
    CancellationToken ct)
    {
    var note = body?.Note;
    var d = await demos.ReportAsync(demoId, note, user.IsAuthenticated ? user.UserId : null, ct);
    if (d is null)
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });
    return Results.Ok(new
    {
        ok = true,
        reportCount = d.ReportCount,
        status = d.Status,
        message = d.ReportCount >= 3
            ? "Thanks — this film was queued for re-review."
            : "Thanks — report recorded.",
    });
}

    private static async Task<IResult> DeleteDemosDemoId(string demoId,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    DemoCatalogService demos,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!await demos.DeleteAsync(demoId, user.UserId, user.IsAdmin, ct))
        return Results.NotFound(new { ok = false, error = "Demo not found or not allowed" });
    return Results.Ok(new { ok = true });
}
}
