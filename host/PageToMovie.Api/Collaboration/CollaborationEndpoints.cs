using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;

namespace PageToMovie.Api.Collaboration;

public static class CollaborationEndpoints
{
    public static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapCollaborationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId}");

        g.MapGet("/acl", GetAclAsync);
        g.MapPost("/acl/editors", InviteEditorAsync);
        g.MapDelete("/acl/editors/{editorUserId}", RemoveEditorAsync);
        g.MapPost("/acl/viewers", InviteViewerAsync);
        g.MapDelete("/acl/viewers/{viewerUserId}", RemoveViewerAsync);

        g.MapPost("/leases/{resourceKey}/acquire", AcquireLeaseAsync);
        g.MapPost("/leases/{resourceKey}/release", ReleaseLeaseAsync);
        g.MapPost("/leases/{resourceKey}/renew", RenewLeaseAsync);
        g.MapPost("/leases/{resourceKey}/transfer", TransferLeaseAsync);
        g.MapGet("/leases/{resourceKey}", GetLeaseAsync);

        g.MapGet("/presence", ListPresenceAsync);
        g.MapPost("/presence/heartbeat", PresenceHeartbeatAsync);
        g.MapPost("/presence/leave", PresenceLeaveAsync);

        g.MapGet("/rev", GetRevAsync);
        g.MapPost("/rev/bump", BumpRevAsync);

        return app;
    }

    static string? UserId(HttpContext http, IUserContext user) =>
        user.UserId
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub");

    static async Task<IResult> GetAclAsync(
        string projectId, IProjectAclService acl, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Viewer, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        var doc = await acl.GetOrCreateAclAsync(projectId, uid, ct);
        return Results.Json(doc);
    }

    static async Task<IResult> InviteEditorAsync(
        string projectId, InviteBody body, IProjectAclService acl, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.UserId)) return Results.BadRequest(new { error = "userId required" });
        try
        {
            await acl.InviteEditorAsync(projectId, uid, body.UserId, ct);
            return Results.Ok(await acl.GetAclAsync(projectId, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    static async Task<IResult> RemoveEditorAsync(
        string projectId, string editorUserId, IProjectAclService acl, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        try
        {
            await acl.RemoveEditorAsync(projectId, uid, editorUserId, ct);
            return Results.Ok(await acl.GetAclAsync(projectId, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    static async Task<IResult> InviteViewerAsync(
        string projectId, InviteBody body, IProjectAclService acl, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.UserId)) return Results.BadRequest(new { error = "userId required" });
        try
        {
            await acl.InviteViewerAsync(projectId, uid, body.UserId, ct);
            return Results.Ok(await acl.GetAclAsync(projectId, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    static async Task<IResult> RemoveViewerAsync(
        string projectId, string viewerUserId, IProjectAclService acl, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        try
        {
            await acl.RemoveViewerAsync(projectId, uid, viewerUserId, ct);
            return Results.Ok(await acl.GetAclAsync(projectId, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    static async Task<IResult> AcquireLeaseAsync(
        string projectId, string resourceKey, IProjectLeaseService leases, IProjectAclService acl,
        IUserContext user, HttpContext http, IHubContext<ProjectHub>? hub, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Editor, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);

        var (ok, lease) = await leases.TryAcquireAsync(projectId, resourceKey, uid, DefaultLeaseTtl, ct);
        if (!ok)
            return Results.Json(new { error = "lease_held", holderUserId = lease.HolderUserId, expiresAt = lease.ExpiresAt },
                statusCode: StatusCodes.Status423Locked);

        if (hub is not null)
            await hub.Clients.Group(ProjectHub.GroupName(projectId))
                .SendAsync("LeaseChanged", projectId, resourceKey, lease, ct);
        return Results.Ok(lease);
    }

    static async Task<IResult> ReleaseLeaseAsync(
        string projectId, string resourceKey, IProjectLeaseService leases, IProjectAclService acl,
        IUserContext user, HttpContext http, IHubContext<ProjectHub>? hub, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Editor, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        var ok = await leases.ReleaseAsync(projectId, resourceKey, uid, ct);
        if (!ok) return Results.Json(new { error = "not_holder" }, statusCode: StatusCodes.Status409Conflict);
        if (hub is not null)
            await hub.Clients.Group(ProjectHub.GroupName(projectId))
                .SendAsync("LeaseChanged", projectId, resourceKey, null, ct);
        return Results.Ok();
    }

    static async Task<IResult> RenewLeaseAsync(
        string projectId, string resourceKey, IProjectLeaseService leases, IProjectAclService acl,
        IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Editor, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        var (ok, lease) = await leases.TryRenewAsync(projectId, resourceKey, uid, DefaultLeaseTtl, ct);
        if (!ok) return Results.Json(new { error = "not_holder", lease }, statusCode: StatusCodes.Status423Locked);
        return Results.Ok(lease);
    }

    static async Task<IResult> TransferLeaseAsync(
        string projectId, string resourceKey, TransferBody body, IProjectLeaseService leases, IProjectAclService acl,
        IUserContext user, HttpContext http, IHubContext<ProjectHub>? hub, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.ToUserId)) return Results.BadRequest(new { error = "toUserId required" });
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Editor, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        var (ok, lease) = await leases.TryTransferAsync(projectId, resourceKey, uid, body.ToUserId, DefaultLeaseTtl, ct);
        if (!ok) return Results.Json(new { error = "not_holder", lease }, statusCode: StatusCodes.Status423Locked);
        if (hub is not null)
            await hub.Clients.Group(ProjectHub.GroupName(projectId))
                .SendAsync("LeaseChanged", projectId, resourceKey, lease, ct);
        return Results.Ok(lease);
    }

    static async Task<IResult> GetLeaseAsync(
        string projectId, string resourceKey, IProjectLeaseService leases, IProjectAclService acl,
        IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Viewer, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        var lease = await leases.GetAsync(projectId, resourceKey, ct);
        return lease is null ? Results.NoContent() : Results.Ok(lease);
    }

    static async Task<IResult> ListPresenceAsync(
        string projectId, IProjectPresenceService presence, IProjectAclService acl,
        IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Viewer, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        return Results.Ok(await presence.ListAsync(projectId, ct));
    }

    static async Task<IResult> PresenceHeartbeatAsync(
        string projectId, IProjectPresenceService presence, IProjectAclService acl,
        IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Viewer, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        await presence.HeartbeatAsync(projectId, uid, null, ct);
        return Results.Ok();
    }

    static async Task<IResult> PresenceLeaveAsync(
        string projectId, IProjectPresenceService presence, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        await presence.LeaveAsync(projectId, uid, ct);
        return Results.Ok();
    }

    static async Task<IResult> GetRevAsync(
        string projectId, IProjectAclService acl, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Viewer, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        var doc = await acl.GetOrCreateAclAsync(projectId, uid, ct);
        return Results.Ok(new { rev = doc.Rev });
    }

    static async Task<IResult> BumpRevAsync(
        string projectId, IProjectAclService acl, IUserContext user, HttpContext http, CancellationToken ct)
    {
        var uid = UserId(http, user);
        if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();
        if (!await acl.CanAccessAsync(projectId, uid, ProjectAccessLevel.Editor, ct))
            return Results.Json(new { error = "project_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        var doc = await acl.GetOrCreateAclAsync(projectId, uid, ct);
        doc.Rev++;
        await acl.SaveAclAsync(projectId, doc, ct);
        return Results.Ok(new { rev = doc.Rev });
    }

    public sealed record InviteBody(string UserId);
    public sealed record TransferBody(string ToUserId);
}
