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
    var lookMap = new Dictionary<string, (string? Look, string? VisualMedium)>(StringComparer.OrdinalIgnoreCase);
    var forkableProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in list.Where(x => !string.IsNullOrWhiteSpace(x.ProjectId)))
    {
        try
        {
            var proj = await store.GetProjectAsync(d.ProjectId ?? "", ct);
            if (proj is not null)
            {
                visibilityMap[d.Id] = proj.VisibilityMode.ToString();
                forkableProjectIds.Add(d.ProjectId!);
                var dir = await store.GetProjectDirAsync(d.ProjectId ?? "", ct);
                lookMap[d.Id] = ApiEndpointHelpers.ResolveDemoLook(dir);
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
        demos = ordered.Select(d =>
        {
            lookMap.TryGetValue(d.Id, out var look);
            return ApiEndpointHelpers.DemoPublicDto(
                d,
                counts.GetValueOrDefault(d.Id),
                mine.Contains(d.Id),
                canFork: d.ProjectId is { Length: > 0 } pid && forkableProjectIds.Contains(pid),
                visibilityMode: visibilityMap.GetValueOrDefault(d.Id, "Private"),
                look: look.Look,
                visualMedium: look.VisualMedium);
        }),
    });
}

    private static async Task<IResult> GetDemosDemoId(string demoId,
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    ProjectStore store,
    IUserContext user,
    CancellationToken ct)
    {
    var d = await demos.TryGetAsync(demoId, ct);
    if (d is null)
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });
    if (!DemoCatalogService.CanUserViewVideo(d, user.UserId, user.IsAdmin))
        return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });
    var count = await upvotes.GetCountAsync(demoId, ct);
    var me = await upvotes.HasUpvotedAsync(demoId, user.UserId, ct);
    var look = await TryResolveDemoLookAsync(store, d.ProjectId, ct);
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
    return Results.Ok(new { ok = true, demo = ApiEndpointHelpers.DemoPublicDto(d, count, me, look: look.Look, visualMedium: look.VisualMedium) });
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
    if (!DemoCatalogService.CanUserViewVideo(d, user.UserId, user.IsAdmin))
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
        var parsed = await ParsePublishDemoRequestAsync(request, ct);
        if (ValidatePublishDemoRequest(parsed, out var projectId) is { } invalid)
            return invalid;
        if (await AuthorizePublishDemoAsync(projectId, user, store, demos, ct) is { } forbidden)
            return forbidden;
        var runtime = new DemoPublishRuntime
        {
            User = user,
            Demos = demos,
            Store = store,
            Media = media,
            YouTube = youTubePublisher,
        };
        return await PublishDemoAsync(parsed, projectId, runtime, ct);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private sealed class PublishDemoRequest
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? ProjectId { get; set; }
        public bool AcceptedGuidelines { get; set; }
        public bool MadeForKids { get; set; }
        public bool IsAiSynthetic { get; set; } = true;
        public string PrivacyStatus { get; set; } = "unlisted";
        public List<string>? Tags { get; set; }
        public bool ReplaceExisting { get; set; } = true;
        public IFormFile? File { get; set; }
    }

    private sealed class DemoPublishRuntime
    {
        public required IUserContext User { get; init; }
        public required DemoCatalogService Demos { get; init; }
        public required ProjectStore Store { get; init; }
        public required MediaRegistryService Media { get; init; }
        public required DemoYouTubePublisherService YouTube { get; init; }
    }

    private sealed class DemoMovieUpload
    {
        public required IFormFile File { get; init; }
        public required byte[] Bytes { get; init; }
        public required string Sha { get; init; }
        public required Stream Stream { get; init; }
    }

    private static async Task<PublishDemoRequest> ParsePublishDemoRequestAsync(HttpRequest request, CancellationToken ct)
    {
        var parsed = request.HasFormContentType
            ? await ParsePublishDemoFormAsync(request, ct)
            : await ParsePublishDemoJsonAsync(request, ct);
        parsed.Title = string.IsNullOrWhiteSpace(parsed.Title) ? null : parsed.Title.Trim();
        parsed.Description = string.IsNullOrWhiteSpace(parsed.Description) ? null : parsed.Description.Trim();
        parsed.ProjectId = string.IsNullOrWhiteSpace(parsed.ProjectId) ? null : parsed.ProjectId.Trim();
        // Default unlisted: channel is operated privately; gallery embeds still work.
        // true "private" would hide films from everyone except the channel owner.
        parsed.PrivacyStatus = parsed.PrivacyStatus is "public" or "unlisted" or "private"
            ? parsed.PrivacyStatus
            : "unlisted";
        return parsed;
    }

    private static async Task<PublishDemoRequest> ParsePublishDemoFormAsync(HttpRequest request, CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var parsed = new PublishDemoRequest
        {
            Title = form["title"].ToString(),
            Description = form[ApiText.DescriptionKey].ToString(),
            ProjectId = form[ApiText.ProjectIdKey].ToString(),
            AcceptedGuidelines = string.Equals(form[ApiText.AcceptedGuidelinesKey].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                                 || form[ApiText.AcceptedGuidelinesKey] == "1"
                                 || form[ApiText.AcceptedGuidelinesKey] == "on",
            MadeForKids = string.Equals(form["madeForKids"].ToString(), "true", StringComparison.OrdinalIgnoreCase),
            PrivacyStatus = form["privacyStatus"].ToString(),
            File = form.Files.GetFile("file") ?? form.Files.FirstOrDefault(),
        };
        if (bool.TryParse(form["isAiSynthetic"].ToString(), out var aiForm))
            parsed.IsAiSynthetic = aiForm;
        parsed.ReplaceExisting = ParseReplaceExistingFormFlag(form[ApiText.ReplaceExistingKey].ToString());
        parsed.Tags = SplitDemoTags(form["tags"].ToString());
        return parsed;
    }

    private static bool ParseReplaceExistingFormFlag(string raw)
    {
        if (bool.TryParse(raw, out var parsed))
            return parsed;
        if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static async Task<PublishDemoRequest> ParsePublishDemoJsonAsync(HttpRequest request, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var parsed = new PublishDemoRequest();
        if (root.TryGetProperty("title", out var t)) parsed.Title = t.GetString();
        if (root.TryGetProperty(ApiText.DescriptionKey, out var d)) parsed.Description = d.GetString();
        if (root.TryGetProperty(ApiText.ProjectIdKey, out var p)) parsed.ProjectId = p.GetString();
        if (root.TryGetProperty(ApiText.AcceptedGuidelinesKey, out var ag))
            parsed.AcceptedGuidelines = ag.ValueKind == JsonValueKind.True
                                 || (ag.ValueKind == JsonValueKind.String
                                     && bool.TryParse(ag.GetString(), out var b) && b);
        if (root.TryGetProperty("madeForKids", out var mfk))
            parsed.MadeForKids = mfk.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("isAiSynthetic", out var ai))
            parsed.IsAiSynthetic = ai.ValueKind != JsonValueKind.False;
        if (root.TryGetProperty("privacyStatus", out var ps) && ps.GetString() is { } privacy)
            parsed.PrivacyStatus = privacy;
        if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.String)
            parsed.Tags = SplitDemoTags(tg.GetString());
        if (root.TryGetProperty(ApiText.ReplaceExistingKey, out var re) && re.ValueKind == JsonValueKind.False)
            parsed.ReplaceExisting = false;
        return parsed;
    }

    private static List<string>? SplitDemoTags(string? tagsRaw) =>
        string.IsNullOrWhiteSpace(tagsRaw)
            ? null
            : tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static IResult? ValidatePublishDemoRequest(PublishDemoRequest parsed, out string projectId)
    {
        projectId = parsed.ProjectId ?? "";
        if (!parsed.AcceptedGuidelines)
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

        return null;
    }

    private static async Task<IResult?> AuthorizePublishDemoAsync(
        string projectId, IUserContext user, ProjectStore store, DemoCatalogService demos, CancellationToken ct)
    {
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
        return null;
    }

    private static async Task<IResult> PublishDemoAsync(
        PublishDemoRequest parsed,
        string projectId,
        DemoPublishRuntime runtime,
        CancellationToken ct)
    {
        // Item 11: re-publish → attach new movie to existing public demo and V2 YouTube replace.
        var existingPublic = parsed.ReplaceExisting
            ? await runtime.Demos.FindPublicDemoForProjectAsync(projectId, runtime.User.UserId, ct)
            : null;

        if (parsed.File is not null && parsed.File.Length > 0)
            return await PublishDemoFromUploadAsync(parsed, projectId, existingPublic, runtime, ct);
        if (existingPublic is not null && !string.IsNullOrWhiteSpace(existingPublic.YoutubeId))
            return await RepublishDemoFromWipAsync(parsed, projectId, existingPublic, runtime, ct);
        return await PublishNewDemoFromWipAsync(parsed, projectId, runtime, ct);
    }

    private static async Task<IResult> PublishDemoFromUploadAsync(
        PublishDemoRequest parsed,
        string projectId,
        DemoCatalogService.DemoEntry? existingPublic,
        DemoPublishRuntime runtime,
        CancellationToken ct)
    {
        var file = parsed.File;
        if (file is null)
            return Results.BadRequest(new { ok = false, error = "file required" });

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
        var upload = new DemoMovieUpload
        {
            File = file,
            Bytes = bytes,
            Sha = MediaRegistryService.HashBytes(bytes),
            Stream = new MemoryStream(bytes),
        };
        await using (upload.Stream)
        {
            if (existingPublic is not null && !string.IsNullOrWhiteSpace(existingPublic.YoutubeId))
                return await ReplaceDemoFromStreamAsync(parsed, projectId, existingPublic, upload, runtime, ct);
            return await PublishNewDemoFromStreamAsync(parsed, projectId, upload, runtime, ct);
        }
    }

    private static async Task<IResult> ReplaceDemoFromStreamAsync(
        PublishDemoRequest parsed,
        string projectId,
        DemoCatalogService.DemoEntry existingPublic,
        DemoMovieUpload upload,
        DemoPublishRuntime runtime,
        CancellationToken ct)
    {
        var entry = await runtime.Demos.AttachMovieFromStreamAsync(
            existingPublic.Id,
            upload.Stream,
            parsed.Title ?? existingPublic.Title,
            parsed.Description,
            parsed.MadeForKids,
            parsed.IsAiSynthetic,
            parsed.PrivacyStatus,
            parsed.Tags,
            ct);
        await TryWriteWipMovieAsync(runtime.Store, projectId, upload.Bytes, ct);
        entry = await EnsureDemoPublicAsync(runtime.Demos, entry, runtime.User.UserId, "Re-publish: YouTube V2 replace", ct);
        QueueYouTubePublish(runtime.YouTube, entry.Id);
        return BuildPublishDemoResult(entry, replacedExisting: true);
    }

    private static async Task<IResult> PublishNewDemoFromStreamAsync(
        PublishDemoRequest parsed,
        string projectId,
        DemoMovieUpload upload,
        DemoPublishRuntime runtime,
        CancellationToken ct)
    {
        var entry = await runtime.Demos.PublishFromStreamAsync(
            upload.Stream,
            parsed.Title ?? projectId ?? upload.File.FileName ?? "Demo",
            parsed.Description,
            projectId,
            runtime.User.UserId,
            acceptedGuidelines: true,
            madeForKids: parsed.MadeForKids,
            isAiSyntheticContent: parsed.IsAiSynthetic,
            privacyStatus: parsed.PrivacyStatus,
            tags: parsed.Tags,
            ct: ct);

        await runtime.Demos.SetStatusAsync(entry.Id, DemoCatalogService.DemoStatuses.Public, runtime.User.UserId,
            "Auto-public: creator publish", ct);
        entry = await runtime.Demos.TryGetAsync(entry.Id, ct) ?? entry;
        await TryWriteWipMovieAsync(runtime.Store, projectId ?? "", upload.Bytes, ct);
        await TryRegisterDemoMediaAsync(runtime.Media, projectId ?? "", entry.Id, upload.Sha, upload.Bytes.LongLength, runtime.User.UserId, ct);
        QueueYouTubePublish(runtime.YouTube, entry.Id);
        return BuildPublishDemoResult(entry, replacedExisting: false);
    }

    private static async Task<IResult> RepublishDemoFromWipAsync(
        PublishDemoRequest parsed,
        string projectId,
        DemoCatalogService.DemoEntry existingPublic,
        DemoPublishRuntime runtime,
        CancellationToken ct)
    {
        var entry = await runtime.Demos.AttachMovieFromWipAsync(
            existingPublic.Id,
            projectId,
            parsed.Title ?? existingPublic.Title,
            parsed.Description,
            parsed.MadeForKids,
            parsed.IsAiSynthetic,
            parsed.PrivacyStatus,
            parsed.Tags,
            ct);
        entry = await EnsureDemoPublicAsync(runtime.Demos, entry, runtime.User.UserId, "Re-publish: YouTube V2 replace", ct);
        QueueYouTubePublish(runtime.YouTube, entry.Id);
        return BuildPublishDemoResult(entry, replacedExisting: true);
    }

    private static async Task<IResult> PublishNewDemoFromWipAsync(
        PublishDemoRequest parsed,
        string projectId,
        DemoPublishRuntime runtime,
        CancellationToken ct)
    {
        var entry = await runtime.Demos.PublishFromWipAsync(
            projectId,
            parsed.Title ?? projectId,
            parsed.Description,
            runtime.User.UserId,
            acceptedGuidelines: true,
            madeForKids: parsed.MadeForKids,
            isAiSyntheticContent: parsed.IsAiSynthetic,
            privacyStatus: parsed.PrivacyStatus,
            tags: parsed.Tags,
            ct: ct);
        // Always push to YouTube — gallery only lists films with a YouTube id.
        QueueYouTubePublish(runtime.YouTube, entry.Id);
        return BuildPublishDemoResult(entry, replacedExisting: false);
    }

    private static async Task TryWriteWipMovieAsync(ProjectStore store, string projectId, byte[] bytes, CancellationToken ct)
    {
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
    }

    private static async Task TryRegisterDemoMediaAsync(
        MediaRegistryService media, string projectId, string demoId, string sha, long sizeBytes, string? userId, CancellationToken ct)
    {
        try
        {
            await media.UpsertAsync(
                projectId,
                $"_demos/{demoId}/movie.mp4",
                sha,
                sizeBytes,
                "demo",
                scene: null,
                clip: null,
                userId,
                ct);
        }
        catch { /* non-fatal */ }
    }

    private static async Task<(string? Look, string? VisualMedium)> TryResolveDemoLookAsync(
        ProjectStore store, string? projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return (null, null);
        try
        {
            var dir = await store.GetProjectDirAsync(projectId, ct);
            return ApiEndpointHelpers.ResolveDemoLook(dir);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task<DemoCatalogService.DemoEntry> EnsureDemoPublicAsync(
        DemoCatalogService demos, DemoCatalogService.DemoEntry entry, string? userId, string note, CancellationToken ct)
    {
        if (!string.Equals(entry.Status, DemoCatalogService.DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
            await demos.SetStatusAsync(entry.Id, DemoCatalogService.DemoStatuses.Public, userId, note, ct);
        return await demos.TryGetAsync(entry.Id, ct) ?? entry;
    }

    private static void QueueYouTubePublish(DemoYouTubePublisherService youTubePublisher, string demoId) =>
        _ = Task.Run(() => youTubePublisher.PublishAsync(demoId, CancellationToken.None));

    private static IResult BuildPublishDemoResult(DemoCatalogService.DemoEntry entry, bool replacedExisting)
    {
        string message;
        if (replacedExisting)
            message = "Updated cut — uploading to YouTube. Gallery shows it when the upload finishes.";
        else if (string.IsNullOrWhiteSpace(entry.YoutubeId))
            message = "Publishing to YouTube… It appears in the gallery when the upload finishes.";
        else
            message = "Film is live on YouTube and in the gallery.";

        return Results.Ok(new
        {
            ok = true,
            // No admin review queue — YouTube upload is the gate for the public wall.
            pendingReview = false,
            awaitingYouTube = string.IsNullOrWhiteSpace(entry.YoutubeId),
            autoPublic = true,
            replacedExisting,
            message,
            demo = ApiEndpointHelpers.DemoPublicDto(entry),
            pagePath = "/demo",
        });
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
