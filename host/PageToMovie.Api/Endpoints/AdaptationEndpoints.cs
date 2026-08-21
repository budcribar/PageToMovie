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
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Api;

public static class AdaptationEndpoints
{
    public static IEndpointRouteBuilder MapAdaptationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{id}/adaptation", GetProjectsIdAdaptation);
        app.MapPost("/api/projects/{id}/adaptation/upload", PostProjectsIdAdaptationUpload)
            .WithUploadSizeLimit(ApiEndpointHelpers.BookImportBytes);
        app.MapGet("/api/books/{idOrHash}", GetBooksIdOrHash);
        app.MapPost("/api/books/{bookId}/projects/{projectId}", PostBooksBookIdProjectsProjectId);
        app.MapPost("/api/books/{bookId}/artifacts", PostBooksBookIdArtifacts);
        app.MapGet("/api/book-artifacts/{artifactId}", GetBookArtifactsArtifactId);
        // <summary>
        // Import a Fountain file as the editable screenplay draft (does not approve / Stage 1 yet).
        // User reviews on Screenplay, then sign-off materialises Stage 1.
        // </summary>
        app.MapPost("/api/projects/{id}/adaptation/import-fountain", PostProjectsIdAdaptationImportFountain)
            .WithUploadSizeLimit(ApiEndpointHelpers.BookImportBytes);
        // <summary>Get the editable Fountain draft + status.</summary>
        app.MapGet("/api/projects/{id}/screenplay", GetProjectsIdScreenplay);
        // <summary>Save Fountain draft (no Stage 1 write). I6: requires script lease when collab.</summary>
        app.MapPut("/api/projects/{id}/screenplay", PutProjectsIdScreenplay);
        // <summary>
        // Approve the Fountain draft: materialise Stage 1 (scenes.json).
        // Optional body text saves first. Marks shot plan stale when hash changes.
        // </summary>
        app.MapPost("/api/projects/{id}/screenplay/sign-off", PostProjectsIdScreenplaySignOff);
        // <summary>Get Stage‑1 visual medium preference (auto | photoreal | picture book | …).</summary>
        app.MapGet("/api/projects/{id}/visual-medium", GetProjectsIdVisualMedium);
        // <summary>Set Stage‑1 visual medium preference before (or after) import.</summary>
        app.MapPut("/api/projects/{id}/visual-medium", PutProjectsIdVisualMedium);
        // <summary>
        // Re-skin the current Fountain draft to a visual medium (descriptive layer only).
        // Lightweight fountain → fountain regeneration so changing the look does not require a re-import.
        // Body (optional): { "visualMedium": "..." } — defaults to the stored preference.
        // Saves the result as the editable draft when the scene structure is preserved.
        // </summary>
        app.MapPost("/api/projects/{id}/adaptation/reskin", PostProjectsIdAdaptationReskin);
        // <summary>
        // Enrich the current Fountain draft's descriptive layer for the stored medium (Scene Embellishment).
        // Incorporates the book's own language where prepared text exists; dialogue / scenes / structure preserved.
        // Saves the enriched result as the editable draft when the scene structure is preserved.
        // </summary>
        app.MapPost("/api/projects/{id}/adaptation/embellish", PostProjectsIdAdaptationEmbellish);
        // <summary>
        // Trim the screenplay toward the project's current target runtime (Trim to cost/length).
        // Derives the working draft from the immutable full-length base; re-running with a new target
        // re-derives cheaply without re-import. Set the target first via PUT /film-runtime.
        // </summary>
        app.MapPost("/api/projects/{id}/adaptation/trim", PostProjectsIdAdaptationTrim);
        // <summary>Get natural + target film length for cost/Stage1.</summary>
        app.MapGet("/api/projects/{id}/film-runtime", GetProjectsIdFilmRuntime);
        // <summary>Set target film length (shorter = typically lower cost). Does not re-run Stage1.</summary>
        app.MapPut("/api/projects/{id}/film-runtime", PutProjectsIdFilmRuntime);
        // <summary>Create an editable Fountain draft from prepared book text (structured + page tags).</summary>
        app.MapPost("/api/projects/{id}/screenplay/from-book", PostProjectsIdScreenplayFromBook);
        return app;
    }

    private static async Task<IResult> GetProjectsIdAdaptation(string id, ProjectStore store, IUserContext user, CancellationToken ct)
    {
    try
    {
        var status = await store.GetAdaptationStatusAsync(id, user.UserId, ct);
        return Results.Ok(new { ok = true, projectId = id, adaptation = status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdAdaptationUpload(string id,
    HttpRequest req,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (!req.HasFormContentType)
            return Results.BadRequest(new { ok = false, error = "multipart form required" });
        var form = await req.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { ok = false, error = "file required" });
        if (file.Length > ApiEndpointHelpers.BookImportBytes)
            return Results.BadRequest(new { ok = false, error = "File too large (max 80 MB)." });
        await using var stream = file.OpenReadStream();
        var path = await store.SaveBookUploadAsync(id, file.FileName, stream);
        BookTextIdentity? bookIdentity = null;
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var text = await File.ReadAllTextAsync(path, req.HttpContext.RequestAborted);
            var project = await store.GetProjectAsync(id, req.HttpContext.RequestAborted);
            bookIdentity = await books.RegisterAsync(
                text, user.UserId, id, project?.VisibilityMode ?? ProjectVisibility.Private,
                req.HttpContext.RequestAborted);
        }
        var status = await store.GetAdaptationStatusAsync(id, user.UserId, req.HttpContext.RequestAborted);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            savedPath = path,
            bookId = bookIdentity?.BookId,
            bookSha256 = bookIdentity?.Sha256,
            message = $"Saved {file.FileName} ({file.Length} bytes)",
            adaptation = status,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetBooksIdOrHash(string idOrHash,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var book = await books.ResolveAsync(idOrHash, user.UserId, ct);
    return book is null ? Results.NotFound(new { ok = false, error = "Book text not found." }) : Results.Ok(new
    {
        ok = true,
        bookId = book.BookId,
        sha256 = book.Sha256,
        byteCount = book.ByteCount,
        text = book.Text,
    });
}

    private static async Task<IResult> PostBooksBookIdProjectsProjectId(string bookId, string projectId, BookTextRegistryService books,
    IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied) return denied;
    await books.LinkToProjectAsync(bookId, user.UserId, projectId, ct);
    return Results.Ok(new { ok = true, bookId, projectId });
}

    private static async Task<IResult> PostBooksBookIdArtifacts(string bookId, JsonElement body, BookTextRegistryService books,
    IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied) return denied;
    static string Required(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException($"{name} required");
    var artifact = await books.RegisterArtifactAsync(
        bookId, user.UserId,
        Required(body, "artifactKind"), Required(body, "content"), Required(body, "modelId"),
        Required(body, "promptVersion"), Required(body, "promptSha256"),
        body.TryGetProperty("temperature", out var temp) ? temp.GetDouble() : 0,
        body.TryGetProperty("behaviorVersions", out var behaviors) ? behaviors.GetRawText() : "{}",
        ct);
    return Results.Ok(new
    {
        ok = true,
        artifactId = artifact.ArtifactId,
        derivationSha256 = artifact.DerivationSha256,
        contentSha256 = artifact.ContentSha256,
    });
}

    private static async Task<IResult> GetBookArtifactsArtifactId(string artifactId, BookTextRegistryService books,
    IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied) return denied;
    var artifact = await books.ResolveArtifactAsync(artifactId, user.UserId, ct);
    return artifact is null
        ? Results.NotFound(new { ok = false, error = "Derived book artifact not found." })
        : Results.Ok(new { ok = true, artifact });
}

    private static async Task<IResult> PostProjectsIdAdaptationImportFountain(string id, HttpRequest req, ProjectStore store, IUserContext user, CancellationToken ct)
    {
    try
    {
        string text;
        string? fileName = null;
        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { ok = false, error = "file required" });
            if (file.Length > ApiEndpointHelpers.BookImportBytes)
                return Results.BadRequest(new { ok = false, error = "File too large (max 80 MB)." });
            fileName = file.FileName;
            using var reader = new StreamReader(file.OpenReadStream());
            text = await reader.ReadToEndAsync(ct);
        }
        else
        {
            using var reader = new StreamReader(req.Body);
            text = await reader.ReadToEndAsync(ct);
            fileName = ScreenplayService.CanonicalFileName;
        }

        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(new { ok = false, error = "empty fountain text" });

        var result = ScreenplayService.ImportAsDraft(store, id, text, fileName);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        var status = await store.GetAdaptationStatusAsync(id, user.UserId, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            title = result.Status.Title,
            sceneHeadingCount = result.Status.SceneHeadingCount,
            draftBytes = result.Status.DraftBytes,
            dirty = result.Status.Dirty,
            signed = result.Status.Signed,
            message = result.Message ?? "Screenplay draft ready — review and approve",
            adaptation = status,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdScreenplay(string id, ProjectStore store, IUserContext user, CancellationToken ct)
    {
    try
    {
        var doc = ScreenplayService.Get(store, id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            text = doc.Text,
            screenplay = doc.Status,
            adaptation = await store.GetAdaptationStatusAsync(id, user.UserId, ct),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PutProjectsIdScreenplay(string id, HttpRequest req, ProjectStore store, IUserContext user,
    PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
    PageToMovie.Engine.Collaboration.IProjectAclService acl,
    IHubContext<PageToMovie.Api.Collaboration.ProjectHub>? hub,
    CancellationToken ct)
    {
    try
    {
        var uid = user.UserId ?? "";
        if (await TryAcquireScriptLeaseAsync(id, uid, leases, acl, ct) is { } locked)
            return locked;
        var text = await ReadScreenplayTextAsync(req, ct);

        var result = ScreenplayService.SaveDraft(store, id, text);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        await NotifyPlanDirtyAsync(id, uid, acl, hub, ct);

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            message = result.Message,
            screenplay = result.Status,
            adaptation = await store.GetAdaptationStatusAsync(id, user.UserId, req.HttpContext.RequestAborted),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult?> TryAcquireScriptLeaseAsync(
        string id,
        string uid,
        PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
        PageToMovie.Engine.Collaboration.IProjectAclService acl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uid)
            || !await acl.CanAccessAsync(id, uid, PageToMovie.Engine.Collaboration.ProjectAccessLevel.Editor, ct))
            return null;
        var (acquired, lease) = await leases.TryAcquireAsync(
            id, PageToMovie.Engine.Collaboration.ProjectLeaseKeys.Script, uid,
            PageToMovie.Api.Collaboration.CollaborationEndpoints.DefaultLeaseTtl, ct);
        if (acquired)
            return null;
        return Results.Json(new {
            ok = false,
            error = "script_locked",
            message = $"Script is being edited by {lease.HolderUserId}.",
            holderUserId = lease.HolderUserId,
        }, statusCode: StatusCodes.Status423Locked);
    }

    private static async Task<string> ReadScreenplayTextAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.HasFormContentType)
            return await ReadScreenplayFormTextAsync(req, ct);
        return await ReadScreenplayBodyTextAsync(req, ct);
    }

    private static async Task<string> ReadScreenplayFormTextAsync(HttpRequest req, CancellationToken ct)
    {
        var form = await req.ReadFormAsync(ct);
        var text = form["text"].ToString() ?? form["content"].ToString() ?? "";
        if (!string.IsNullOrEmpty(text) || form.Files.Count == 0)
            return text;
        using var reader = new StreamReader(form.Files[0].OpenReadStream());
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task<string> ReadScreenplayBodyTextAsync(HttpRequest req, CancellationToken ct)
    {
        using var reader = new StreamReader(req.Body);
        var body = await reader.ReadToEndAsync(ct);
        // Accept raw text or JSON { "text": "..." }
        if (!body.TrimStart().StartsWith('{'))
            return body;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var t))
                return t.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("content", out var c))
                return c.GetString() ?? "";
        }
        catch { /* treat as raw */ }
        return body;
    }

    private static async Task NotifyPlanDirtyAsync(
        string id,
        string uid,
        PageToMovie.Engine.Collaboration.IProjectAclService acl,
        IHubContext<PageToMovie.Api.Collaboration.ProjectHub>? hub,
        CancellationToken ct)
    {
        // I12: PlanDirty — collaborators re-fetch estimate
        try
        {
            var doc = await acl.GetOrCreateAclAsync(id, uid, ct);
            doc.Rev++;
            await acl.SaveAclAsync(id, doc, ct);
            if (hub is not null)
                await hub.Clients.Group(PageToMovie.Api.Collaboration.ProjectHub.GroupName(id))
                    .SendAsync("PlanDirty", id, doc.Rev, uid, ct);
        }
        catch { /* soft */ }
    }

    private static async Task<IResult> PostProjectsIdScreenplaySignOff(string id,
    HttpRequest req,
    ProjectStore store,
    CastFromScreenplayService castService,
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
    CancellationToken ct)
    {
    try
    {
        var text = await ReadOptionalScreenplayTextAsync(req, ct);
        var result = ScreenplayService.SignOff(store, id, text);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        var cast = await TryExtractCastAsync(id, castService, chat, ct);
        // One line per sign-off with the project it landed on (the UI suite reads this to tell a
        // "sign-off went to project A, shot plan ran on project B" race from a real extraction failure).
        await Console.Error.WriteLineAsync($"[sign-off] {id}: ok scenes={result.SceneCount} characters={result.CharacterCount} user={user.UserId} cast={(cast is null ? "n/a" : System.Text.Json.JsonSerializer.Serialize(cast))}");
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            title = result.Title,
            sceneCount = result.SceneCount,
            characterCount = result.CharacterCount,
            locationCount = result.LocationCount,
            hashChanged = result.HashChanged,
            message = result.Message,
            screenplay = result.Status,
            adaptation = await store.GetAdaptationStatusAsync(id, user.UserId, ct),
            cast,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<string?> ReadOptionalScreenplayTextAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is not > 0 && req.ContentType is null)
            return null;
        using var reader = new StreamReader(req.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return null;
        if (!body.TrimStart().StartsWith('{'))
            return body;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var t))
                return t.GetString();
        }
        catch { return body; }
        return null;
    }

    private static async Task<object?> TryExtractCastAsync(
        string id, CastFromScreenplayService castService, PageToMovie.Core.Abstractions.IChatClient chat, CancellationToken ct)
    {
        // AI cast sidecar after approve (closed cast for Characters / plates)
        if (!chat.IsConfigured)
        {
            // Visible skip: an unconfigured chat client at sign-off is the one path that leaves a
            // project with an approved screenplay and no cast (seen intermittently in UI runs while
            // the per-project config write is still in flight). Say so instead of returning nothing.
            await Console.Error.WriteLineAsync($"[sign-off] {id}: cast extraction skipped — chat client not configured");
            return new { ok = false, skipped = "chat_not_configured", error = "Cast extraction skipped: no chat model configured at sign-off." };
        }
        try
        {
            // force:false — respects ExtractAsync's own skip-if-present guard. Sign-off still
            // auto-populates cast the first time (file doesn't exist yet), but never blows away
            // an existing cast_seeds.json (voice clones, portrait locks, curated looks) just
            // because the Fountain changed. Use the explicit "Extract Cast" button/endpoint
            // (force:true) to intentionally rebuild after adding a character.
            var castResult = await castService.ExtractAsync(id, force: false, ct: ct);
            return new
            {
                ok = castResult.Ok,
                characterCount = castResult.CharacterCount,
                characters = castResult.CharacterKeys,
                error = castResult.Error,
                path = castResult.OutPath,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private static async Task<IResult> GetProjectsIdVisualMedium(string id,
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
        var dir = await store.GetProjectDirAsync(id, ct);
        var medium = ProjectVisionMeta.GetAdaptationMediumPreference(dir);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            visualMedium = medium,
            options = new[]
            {
                new { id = ProjectVisionMeta.MediumAuto, label = VisualMediumStyles.DisplayLabel(ProjectVisionMeta.MediumAuto) },
                new { id = ProjectVisionMeta.MediumPhotoreal, label = VisualMediumStyles.DisplayLabel(ProjectVisionMeta.MediumPhotoreal) },
                new { id = ProjectVisionMeta.MediumIllustrated, label = VisualMediumStyles.DisplayLabel(ProjectVisionMeta.MediumIllustrated) },
                new { id = ProjectVisionMeta.MediumStylized3d, label = VisualMediumStyles.DisplayLabel(ProjectVisionMeta.MediumStylized3d) },
                new { id = ProjectVisionMeta.MediumOther, label = VisualMediumStyles.DisplayLabel(ProjectVisionMeta.MediumOther) },
            },
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PutProjectsIdVisualMedium(string id,
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
        await store.RequireProjectAsync(id, ct);
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        string? medium = null;
        if (root.TryGetProperty("visualMedium", out var vm) && vm.ValueKind == JsonValueKind.String)
            medium = vm.GetString();
        else if (root.TryGetProperty("visual_medium", out var vm2) && vm2.ValueKind == JsonValueKind.String)
            medium = vm2.GetString();
        if (string.IsNullOrWhiteSpace(medium))
            return Results.BadRequest(new { ok = false, error = "visualMedium required" });

        var written = ProjectVisionMeta.SetAdaptationMediumPreference(await store.GetProjectDirAsync(id, ct), medium);
        store.TriggerAutoGitCommit(id, $"ptm:stage=visual_medium_preference medium={written.VisualMedium}");
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            visualMedium = written.VisualMedium,
            message = string.Equals(written.VisualMedium, ProjectVisionMeta.MediumAuto, StringComparison.Ordinal)
                ? "Medium set to Auto — Stage‑1 will infer from the book."
                : $"Medium locked to {written.VisualMedium} for Stage‑1.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdAdaptationReskin(string id,
    HttpRequest req,
    ProjectStore store,
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    XaiResponsesClient? responses,
    BookTextRegistryService? books,
    PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var dir = await store.GetProjectDirAsync(id, ct);

        string? medium = null;
        if (req.ContentLength is > 0)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.TryGetProperty("visualMedium", out var vm) && vm.ValueKind == JsonValueKind.String)
                    medium = vm.GetString();
                else if (root.TryGetProperty("visual_medium", out var vm2) && vm2.ValueKind == JsonValueKind.String)
                    medium = vm2.GetString();
            }
            catch { /* no/invalid body — fall back to stored preference */ }
        }
        if (string.IsNullOrWhiteSpace(medium))
            medium = ProjectVisionMeta.GetAdaptationMediumPreference(dir);

        var result = await ScreenplayService.ReskinDraftAsync(
            store, id, medium, chat, ct: ct,
            responses: responses, bookRegistry: books, bookFileSessions: bookFileSessions,
            useFakes: opts.Value.UseFakes);
        return await ApiEndpointHelpers.DraftEditResponseAsync(result, id, $"ptm:stage=reskin medium={medium}", store, user, ct);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdAdaptationEmbellish(string id,
    ProjectStore store,
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    XaiResponsesClient? responses,
    BookTextRegistryService? books,
    PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var medium = ProjectVisionMeta.GetAdaptationMediumPreference(await store.GetProjectDirAsync(id, ct));

        var result = await ScreenplayService.EmbellishDraftAsync(
            store, id, medium, chat, ct: ct,
            responses: responses, bookRegistry: books, bookFileSessions: bookFileSessions,
            useFakes: opts.Value.UseFakes);
        return await ApiEndpointHelpers.DraftEditResponseAsync(result, id, "ptm:stage=embellish", store, user, ct);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdAdaptationTrim(string id,
    ProjectStore store,
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    XaiResponsesClient? responses,
    BookTextRegistryService? books,
    PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);

        var result = await ScreenplayService.TrimDraftAsync(
            store, id, new ChatCall(chat, Progress: new ProgressCall(ct)),
            responses: responses, bookRegistry: books, bookFileSessions: bookFileSessions,
            useFakes: opts.Value.UseFakes);
        return await ApiEndpointHelpers.DraftEditResponseAsync(result, id, "ptm:stage=trim", store, user, ct);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdFilmRuntime(string id,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        var snap = await FilmRuntime.ResolveAsync(store, id, ct: ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            hasBookText = snap.HasBookText,
            naturalMinutes = snap.NaturalMinutes,
            targetMinutes = snap.TargetMinutes,
            mode = snap.Mode,
            textWords = snap.TextWords,
            bookKind = snap.BookKind,
            source = snap.Source,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PutProjectsIdFilmRuntime(string id,
    HttpRequest req,
    ProjectStore store,
    IUserContext user,
    CancellationToken ct)
    {
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        int target = 0;
        if (root.TryGetProperty("targetMinutes", out var tm) && tm.TryGetInt32(out var t1))
            target = t1;
        else if (root.TryGetProperty("target_runtime_minutes", out var tm2) && tm2.TryGetInt32(out var t2))
            target = t2;
        if (target <= 0)
            return Results.BadRequest(new { ok = false, error = "targetMinutes required (2–180)" });

        var snap = await FilmRuntime.SetTargetAsync(store, id, target, ct).ConfigureAwait(false);
        store.TriggerAutoGitCommit(id, $"ptm:stage=runtime_retarget target={snap.TargetMinutes}");
        string message;
        if (snap.TargetMinutes < snap.NaturalMinutes)
            message = $"Target set to {snap.TargetMinutes} min (shorter than natural ~{snap.NaturalMinutes} min — typically fewer clips / lower cost).";
        else if (snap.TargetMinutes == snap.NaturalMinutes)
            message = $"Target set to natural length (~{snap.NaturalMinutes} min).";
        else
            message = $"Target set to {snap.TargetMinutes} min (longer than natural ~{snap.NaturalMinutes} min).";
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            hasBookText = snap.HasBookText,
            naturalMinutes = snap.NaturalMinutes,
            targetMinutes = snap.TargetMinutes,
            mode = snap.Mode,
            message,
            adaptation = await store.GetAdaptationStatusAsync(id, user.UserId, ct),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdScreenplayFromBook(string id,
    ProjectStore store,
    PageToMovie.Core.Abstractions.IChatClient chat,
    BookTextRegistryService books,
    IUserContext user,
    UserDatabaseService userDb,
    IUserApiKeyProvider keys,
    IOptions<PageToMovieOptions> opts,
    PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions,
    PageToMovie.Core.Abstractions.IFountainFileSessionFactory? fountainFileSessions,
    XaiResponsesClient? responses,
    CancellationToken ct)
    {
    if (await AuthGate.RequirePersonalGrokKeyAsync(user, userDb, opts, ApiRuntime.UseFakes, keys) is { } denied)
        return denied;
    try
    {
        var result = await ScreenplayService.CreateDraftFromBookAsync(
            store, id, chat, ct: ct, bookRegistry: books, cacheUserId: user.UserId,
            bookFileSessionFactory: bookFileSessions,
            responses: responses,
            useFakes: opts.Value.UseFakes,
            fountainFileSessionFactory: fountainFileSessions);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            message = result.Message,
            screenplay = result.Status,
            adaptation = await store.GetAdaptationStatusAsync(id, user.UserId, ct),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}
