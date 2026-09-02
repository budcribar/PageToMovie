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
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Api;

public static class CharacterEndpoints
{
    public static IEndpointRouteBuilder MapCharacterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{id}/characters", GetProjectsIdCharacters);
        app.MapGet("/api/projects/{projectId}/characters/{charKey}/ref", GetProjectsProjectIdCharactersCharKeyRef);
        app.MapGet("/api/projects/{projectId}/characters/{charKey}/variants/{index:int}", GetProjectsProjectIdCharactersCharKeyVariantsIndex);
        app.MapGet("/api/projects/{projectId}/characters/{charKey}/bookrefs/{index:int}", GetProjectsProjectIdCharactersCharKeyBookrefsIndex);
        app.MapGet("/api/projects/{projectId}/characters/{charKey}/book-candidates", GetProjectsProjectIdCharactersCharKeyBookCandidates);
        app.MapPost("/api/projects/{projectId}/characters/{charKey}/set-book-refs", PostProjectsProjectIdCharactersCharKeySetBookRefs);
        // <summary>
        // Save description / visual_lock for portrait continuity (cast_seeds + blueprint).
        // By default runs AI prompt scrub (literal filmable + base look, not later-story wardrobe).
        // </summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/look", PostProjectsIdCharactersCharKeyLook);
        // <summary>
        // AI: Fountain (+ book) → source/cast_seeds.json.
        // Closed cast for Characters UI — not dialogue-cue parse only.
        // </summary>
        // <summary>
        // AI: Fountain (+ book) → source/cast_seeds.json (and location seeds).
        // Starts a background job so long chat+literalize does not 502 on reverse proxies.
        // Prefer polling job status / SignalR; the old synchronous body is no longer used for the main path.
        // </summary>
        app.MapPost("/api/projects/{id}/characters/extract-cast", PostProjectsIdCharactersExtractCast);
        // <summary>
        // Heuristic attach (no Grok). Prefer POST /api/jobs/sort-character-plates for vision sort.
        // </summary>
        app.MapPost("/api/projects/{id}/characters/attach-book-plates", PostProjectsIdCharactersAttachBookPlates);
        app.MapPost("/api/projects/{id}/characters/{charKey}/lock-variant", PostProjectsIdCharactersCharKeyLockVariant);
        app.MapPost("/api/projects/{id}/characters/{charKey}/lock-bookref", PostProjectsIdCharactersCharKeyLockBookref);
        // <summary>
        // Upload an operator-provided image and lock it as the character reference (preferred look).
        // Multipart form field name: <c>file</c> (png/jpg/webp/gif).
        // </summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/upload-ref", PostProjectsIdCharactersCharKeyUploadRef);
        app.MapPost("/api/projects/{id}/characters/{charKey}/unlock", PostProjectsIdCharactersCharKeyUnlock);
        app.MapPost("/api/projects/{id}/characters/{charKey}/keep-look", PostProjectsIdCharactersCharKeyKeepLook);
        // <summary>
        // Delete a character picture: preferred/lock, variant, or book plate.
        // Body: { "kind": "preferred"|"variant"|"bookref", "index": 0 }
        // </summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/delete-image", PostProjectsIdCharactersCharKeyDeleteImage);
        return app;
    }

    private static IResult GetProjectsIdCharacters(string id, ProjectStore store)
    {
    try
    {
        // ListCharacters still has seed/json paths for Pass 3.5; keeps working via sync wrappers
        var chars = store.ListCharacters(id);
        var plates = store.GetCharacterPlatesState(id);
        var seedLimits = store.GetImageSeedLimits(id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            characters = chars,
            characterPlates = plates,
            imageSeedLimits = seedLimits,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsProjectIdCharactersCharKeyRef(HttpContext ctx, string projectId, string charKey, ProjectStore store)
    {
    var path = store.ResolveCharacterRefPath(projectId, charKey);
    return path is null ? Results.NotFound(new { ok = false, error = "ref image not found" }) : ApiEndpointHelpers.ServeCachedFile(ctx, path);
}

    private static IResult GetProjectsProjectIdCharactersCharKeyVariantsIndex(HttpContext ctx, string projectId, string charKey, int index, ProjectStore store)
    {
    var path = store.ResolveCharacterVariantPath(projectId, charKey, index);
    return path is null ? Results.NotFound(new { ok = false, error = "variant not found" }) : ApiEndpointHelpers.ServeCachedFile(ctx, path);
}

    private static IResult GetProjectsProjectIdCharactersCharKeyBookrefsIndex(HttpContext ctx, string projectId, string charKey, int index, ProjectStore store)
    {
    var path = store.ResolveCharacterBookRefPath(projectId, charKey, index);
    return path is null ? Results.NotFound(new { ok = false, error = "book ref not found" }) : ApiEndpointHelpers.ServeCachedFile(ctx, path);
}

    private static async Task<IResult> GetProjectsProjectIdCharactersCharKeyBookCandidates(string projectId, string charKey, CharacterBookPlateService service, CancellationToken ct)
    {
    try
    {
        var candidates = await service.GetRankedBookCandidatesAsync(projectId, charKey, ct);
        return Results.Ok(new { ok = true, candidates });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult PostProjectsProjectIdCharactersCharKeySetBookRefs(string projectId, string charKey, SetBookRefsRequest body, ProjectStore store)
    {
    try
    {
        var paths = body.ImagePaths ?? new List<string>();
        store.SetCharacterBookRefs(projectId, charKey, paths);
        return Results.Ok(new { ok = true, message = $"Saved {paths.Count} book reference picture(s) for {charKey}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyLook(string id,
    string charKey,
    UpdateCharacterLookRequest? body,
    ProjectStore store,
    CastVisualLiteralizeService literalize,
    PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
    IUserContext user,
    CancellationToken ct)
    {
    try
    {
        body ??= new UpdateCharacterLookRequest();
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        if (await ApiEndpointHelpers.TryAcquireCastLeaseAsync(id, charKey, leases, user, ct) is { } lookLocked)
            return lookLocked;

        var desc = body.Description;
        var vis = body.VisualLock;
        var (storedDesc, storedVis) = ReadSeedLookFields(store.GetCharacterSeed(id, charKey));
        if (LooksUnchanged(desc, vis, storedDesc, storedVis))
        {
            return Results.Ok(new
            {
                ok = true,
                projectId = id,
                charKey,
                scrubbedWithAi = false,
                description = storedDesc ?? desc,
                visualLock = storedVis ?? vis,
                message = "Look unchanged",
            });
        }

        var scrubbed = false;
        (desc, vis, scrubbed) = await MaybeScrubLookAsync(id, charKey, body, store, literalize, ct);
        store.UpdateCharacterSeedText(id, charKey, description: desc, visualLock: vis);
        var (savedDesc, savedVis) = ReadSeedLookFields(store.GetCharacterSeed(id, charKey));

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            scrubbedWithAi = scrubbed,
            description = savedDesc ?? desc,
            visualLock = savedVis ?? vis,
            message = scrubbed
                ? "Look saved (AI scrubbed: base + literal)"
                : "Look (description / visual lock) updated",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static (string? Desc, string? Vis) ReadSeedLookFields(System.Text.Json.JsonElement? seed)
    {
        string? desc = null;
        string? vis = null;
        if (seed is null)
            return (desc, vis);
        var existing = seed.Value;
        if (existing.TryGetProperty(ApiText.DescriptionKey, out var d0))
            desc = d0.GetString();
        if (existing.TryGetProperty("visual_lock", out var v0))
            vis = v0.GetString();
        return (desc, vis);
    }

    private static bool LooksUnchanged(string? desc, string? vis, string? storedDesc, string? storedVis) =>
        string.Equals(desc ?? "", storedDesc ?? "", StringComparison.Ordinal) &&
        string.Equals(vis ?? "", storedVis ?? "", StringComparison.Ordinal);

    private static async Task<(string? Desc, string? Vis, bool Scrubbed)> MaybeScrubLookAsync(
        string id,
        string charKey,
        UpdateCharacterLookRequest body,
        ProjectStore store,
        CastVisualLiteralizeService literalize,
        CancellationToken ct)
    {
        var desc = body.Description;
        var vis = body.VisualLock;
        if (!body.ScrubWithAi || (desc is null && vis is null))
            return (desc, vis, false);

        var (d2, v2, usedAi) = await literalize.ScrubLookFieldsAsync(
            charKey,
            description: desc ?? "",
            visualLock: vis ?? "",
            model: string.IsNullOrWhiteSpace(body.Model)
                ? ProjectModelSelection.RequirePlanning(
                    await store.GetConfigAsync(id, ct).ConfigureAwait(false),
                    "Character look scrub")
                : ProjectModelSelection.RequireExplicit(body.Model, ModelCapability.Chat, "Character look scrub"),
            ct: ct).ConfigureAwait(false);
        if (!usedAi)
            return (desc, vis, false);
        if (desc is not null) desc = d2;
        if (vis is not null) vis = v2;
        return (desc, vis, true);
    }

    private static async Task<IResult> PostProjectsIdCharactersExtractCast(string id,
    ExtractCastRequest? body,
    FilmJobService jobService)
    {
    try
    {
        body ??= new ExtractCastRequest();
        var job = await jobService.StartExtractCastAsync(id, force: body.Force, model: body.Model);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            jobId = job.JobId,
            status = job.Status,
            kind = job.Kind,
            message = job.Message ?? "Cast extract started…",
            async = true,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersAttachBookPlates(string id,
    AttachCharacterPlatesRequest? body,
    CharacterBookPlateService plates,
    CancellationToken ct)
    {
    try
    {
        body ??= new AttachCharacterPlatesRequest();
        var result = await plates.AttachAsync(
            id,
            force: body.Force,
            copyIntoAssets: body.CopyIntoAssets,
            onlyCharKey: body.CharKey,
            useVision: false,
            ct: ct);
        return result.Ok
            ? Results.Ok(new { ok = true, projectId = id, attach = result })
            : Results.BadRequest(new { ok = false, projectId = id, attach = result, error = result.Reason });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyLockVariant(string id, string charKey, HttpRequest req, FilmJobService jobService,
           ProjectTelemetryService telemetry, IOptions<PageToMovieOptions> opts,
           PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
           IUserContext user, CancellationToken ct)
    {
    try
    {
        if (await ApiEndpointHelpers.TryAcquireCastLeaseAsync(id, charKey, leases, user, ct) is { } variantLocked)
            return variantLocked;
        var (index, overrideStyle, overrideReason, overrideNote) =
            await ApiEndpointHelpers.ParseCharacterLockBodyAsync(req, defaultIndex: 1, acceptVariantIndexAlias: true);
        var result = await jobService.RunCharacterDesignActionAsync(id, "lock-variant", charKey, index, allowStyleOverride: overrideStyle, ct: ct);
        if (overrideStyle)
            await ApiEndpointHelpers.LogStyleOverrideAsync(telemetry, opts, id, charKey, overrideReason, overrideNote);
        return Results.Ok(new { ok = true, message = result, projectId = id, charKey, index });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyLockBookref(string id, string charKey, HttpRequest req, FilmJobService jobService,
           ProjectTelemetryService telemetry, IOptions<PageToMovieOptions> opts)
    {
    try
    {
        var (index, overrideStyle, overrideReason, overrideNote) =
            await ApiEndpointHelpers.ParseCharacterLockBodyAsync(req, defaultIndex: 0);
        // variantIndex slot reused as book-ref index for lock-bookref
        var result = await jobService.RunCharacterDesignActionAsync(
            id, "lock-bookref", charKey, variantIndex: index, allowStyleOverride: overrideStyle);
        if (overrideStyle)
            await ApiEndpointHelpers.LogStyleOverrideAsync(telemetry, opts, id, charKey, overrideReason, overrideNote);
        return Results.Ok(new { ok = true, message = result, projectId = id, charKey, index });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyUploadRef(string id,
    string charKey,
    HttpRequest req,
    CharacterDesignService characters,
    CancellationToken ct)
    {
    try
    {
        if (!req.HasFormContentType)
            return Results.BadRequest(new { ok = false, error = "multipart form required (field: file)" });

        var form = await req.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No image file in form (field name: file)" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ApiText.JpegExtension or ".webp" or ".gif" or ".bmp"))
            return Results.BadRequest(new { ok = false, error = "Use a PNG, JPG, WEBP, or GIF image." });

        if (file.Length > 25 * 1024 * 1024)
            return Results.BadRequest(new { ok = false, error = "Image too large (max 25 MB)." });

        var overrideStyle = string.Equals(form["overrideStyle"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        await using var stream = file.OpenReadStream();
        var path = await characters.LockFromUploadAsync(id, charKey, stream, file.FileName, overrideStyle, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            path = Path.GetFileName(path),
            message = "Locked preferred look from your upload",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    /// <summary>
    /// The operator looked at a picture flagged as drawn from an out-of-date reference and chose to
    /// keep it. Retires the flag; nothing is regenerated and nothing is deleted.
    /// </summary>
    private static IResult PostProjectsIdCharactersCharKeyKeepLook(string id, string charKey, ProjectStore projects)
    {
        try
        {
            projects.ClearCharacterLookStale(id, charKey);
            return Results.Ok(new { ok = true, projectId = id, charKey });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<IResult> PostProjectsIdCharactersCharKeyUnlock(string id, string charKey, FilmJobService jobService,
           PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
           IUserContext user, CancellationToken ct)
    {
    try
    {
        if (await ApiEndpointHelpers.TryAcquireCastLeaseAsync(id, charKey, leases, user, ct) is { } unlockLocked)
            return unlockLocked;
        var result = await jobService.RunCharacterDesignActionAsync(id, "unlock", charKey, ct: ct);
        return Results.Ok(new { ok = true, message = result, projectId = id, charKey });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult PostProjectsIdCharactersCharKeyDeleteImage(string id, string charKey, DeleteCharacterImageRequest? body, CharacterDesignService characters)
    {
    try
    {
        body ??= new DeleteCharacterImageRequest();
        if (string.IsNullOrWhiteSpace(body.Kind))
            return Results.BadRequest(new { ok = false, error = "kind required" });
        characters.DeleteImage(id, charKey, body.Kind, body.Index);
        return Results.Ok(new { ok = true, projectId = id, charKey, kind = body.Kind, index = body.Index });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}
