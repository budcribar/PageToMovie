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

public static class LocationEndpoints
{
    public static IEndpointRouteBuilder MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{id}/locations", GetProjectsIdLocations);
        // <summary>Serve locked location set plate (no-cache — plates are overwritten in place).</summary>
        app.MapGet("/api/projects/{projectId}/locations/{locKey}/ref", GetProjectsProjectIdLocationsLocKeyRef);
        // <summary>Save location description / visual_lock into location_seed_tokens. I8: loc lease.</summary>
        app.MapPost("/api/projects/{id}/locations/{locKey}/look", PostProjectsIdLocationsLocKeyLook);
        // <summary>Upload and lock an operator-provided location set plate.</summary>
        app.MapPost("/api/projects/{id}/locations/{locKey}/upload-ref", PostProjectsIdLocationsLocKeyUploadRef);
        app.MapGet("/api/projects/{projectId}/locations/{locKey}/variants/{index:int}", GetProjectsProjectIdLocationsLocKeyVariantsIndex);
        app.MapPost("/api/projects/{id}/locations/{locKey}/lock-variant", PostProjectsIdLocationsLocKeyLockVariant);
        return app;
    }

    private static IResult GetProjectsIdLocations(string id, ProjectStore store)
    {
    try
    {
        var locs = store.ListLocations(id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            locations = locs,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsProjectIdLocationsLocKeyRef(HttpContext ctx, string projectId, string locKey, ProjectStore store)
    {
    var path = store.ResolveLocationRefPath(projectId, locKey);
    if (path is null)
        return Results.NotFound(new { ok = false, error = "No locked location plate" });
    return ApiEndpointHelpers.ServeCachedFile(ctx, path, SpecializedMimeType.ImagePng.ToMimeTypeString(), immutable: false);
}

    private static async Task<IResult> PostProjectsIdLocationsLocKeyLook(string id,
    string locKey,
    UpdateLocationLookRequest body,
    ProjectStore store,
    PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
    IUserContext user,
    CancellationToken ct)
    {
    try
    {
        locKey = Uri.UnescapeDataString(locKey ?? "");
        if (string.IsNullOrWhiteSpace(locKey))
            return Results.BadRequest(new { ok = false, error = "locKey required" });
        var uid = user.UserId ?? "";
        if (!string.IsNullOrWhiteSpace(uid))
        {
            var (okLease, lease) = await leases.TryAcquireAsync(
                id, PageToMovie.Engine.Collaboration.ProjectLeaseKeys.Loc(locKey), uid,
                PageToMovie.Api.Collaboration.CollaborationEndpoints.DefaultLeaseTtl, ct);
            if (!okLease)
                return Results.Json(new {
                    ok = false,
                    error = "loc_locked",
                    message = $"Location is locked by {lease.HolderUserId}.",
                    holderUserId = lease.HolderUserId,
                }, statusCode: StatusCodes.Status423Locked);
        }
        var ok = store.UpdateLocationLook(id, locKey, body.Description, body.VisualLock);
        if (!ok)
            return Results.BadRequest(new { ok = false, error = "Could not update location look" });
        var row = store.ListLocations(id).FirstOrDefault(l =>
            string.Equals(l.Key, locKey, StringComparison.OrdinalIgnoreCase));
        return Results.Ok(new
        {
            ok = true,
            location = row,
            description = body.Description,
            visualLock = body.VisualLock,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdLocationsLocKeyUploadRef(string id,
    string locKey,
    HttpRequest req,
    ProjectStore store)
    {
    try
    {
        locKey = Uri.UnescapeDataString(locKey ?? "");
        if (string.IsNullOrWhiteSpace(locKey))
            return Results.BadRequest(new { ok = false, error = "locKey required" });
        if (!req.HasFormContentType)
            return Results.BadRequest(new { ok = false, error = "multipart form expected" });
        var form = await req.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length < 64)
            return Results.BadRequest(new { ok = false, error = "Image file required" });
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var path = store.LockLocationRefFromBytes(id, locKey, ms.ToArray());
        return Results.Ok(new
        {
            ok = true,
            message = "Locked location plate from your upload",
            path = path,
            url = $"/api/projects/{Uri.EscapeDataString(id)}/locations/{Uri.EscapeDataString(locKey)}/ref",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsProjectIdLocationsLocKeyVariantsIndex(HttpContext ctx, string projectId, string locKey, int index, ProjectStore store)
    {
    locKey = Uri.UnescapeDataString(locKey ?? "");
    var dir = store.GetLocationAssetsDir(projectId);
    var name = ProjectStore.LocationVariantFileName(locKey, index);
    var path = Path.Combine(dir, name);
    if (!File.Exists(path))
        return Results.NotFound(new { ok = false, error = "Variant not found" });
    return ApiEndpointHelpers.ServeCachedFile(ctx, path, SpecializedMimeType.ImagePng.ToMimeTypeString(), immutable: false);
}

    private static async Task<IResult> PostProjectsIdLocationsLocKeyLockVariant(string id,
    string locKey,
    int? index,
    LocationDesignService locations,
    PageToMovie.Engine.Collaboration.IProjectLeaseService leases,
    IUserContext user,
    CancellationToken ct)
    {
    try
    {
        locKey = Uri.UnescapeDataString(locKey ?? "");
        var uid = user.UserId ?? "";
        if (!string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(locKey))
        {
            var (ok, lease) = await leases.TryAcquireAsync(
                id, PageToMovie.Engine.Collaboration.ProjectLeaseKeys.Loc(locKey), uid,
                PageToMovie.Api.Collaboration.CollaborationEndpoints.DefaultLeaseTtl, ct);
            if (!ok)
                return Results.Json(new {
                    ok = false,
                    error = "loc_locked",
                    message = $"Location is locked by {lease.HolderUserId}.",
                    holderUserId = lease.HolderUserId,
                }, statusCode: StatusCodes.Status423Locked);
        }
        var vi = index is > 0 ? index.Value : 1;
        var path = await locations.LockVariantAsync(id, locKey, vi, ct);
        return Results.Ok(new
        {
            ok = true,
            message = $"Locked location plate from variant {vi}",
            path,
            url = $"/api/projects/{Uri.EscapeDataString(id)}/locations/{Uri.EscapeDataString(locKey)}/ref",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}
