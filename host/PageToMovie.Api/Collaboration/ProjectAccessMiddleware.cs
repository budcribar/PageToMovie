using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;
using PageToMovie.Core.Utils;

namespace PageToMovie.Api.Collaboration;

/// <summary>
/// Enforces ACL on /api/projects/{projectId}/... routes.
/// GET/HEAD → Viewer+; mutating methods → Editor+ (owner included).
/// Exempt: create/list/import at collection level; /acl invite is owner-checked in handlers.
/// </summary>
public sealed class ProjectAccessMiddleware
{
    private readonly RequestDelegate _next;

    public ProjectAccessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IProjectAclService acl, IUserContext user)
    {
        var path = context.Request.Path.Value ?? "";
        var isUserProjects = path.StartsWith("/api/projects", StringComparison.OrdinalIgnoreCase);
        var isAdminProjects = path.StartsWith("/api/admin/projects", StringComparison.OrdinalIgnoreCase);
        if (!isUserProjects && !isAdminProjects)
        {
            await _next(context);
            return;
        }

        // owner/Name arrives as two segments once %2F is decoded (or sent unencoded).
        // Collapse to owner%2FName so {id}/{projectId} route templates still match.
        if (ProjectIdRouting.TryRewriteRequestPath(path, out var rewritten))
        {
            context.Request.Path = rewritten;
            path = rewritten;
        }

        if (!isUserProjects || !ProjectIdRouting.TryExtractProjectId(path, out var projectId))
        {
            // Admin project URLs: rewrite only. /api/projects collection (create/list): pass through.
            await _next(context);
            return;
        }

        // Skip ACL file bootstrap endpoints that only need auth? Still require access.
        var method = context.Request.Method.ToUpperInvariant();
        var isRead = method is "GET" or "HEAD" or "OPTIONS";
        // Mutations only — reads keep existing handler-level auth
        if (isRead)
        {
            await _next(context);
            return;
        }

        var userId = user.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "authentication_required" }, cancellationToken: context.RequestAborted);
            return;
        }

        if (user.IsAdmin)
        {
            await _next(context);
            return;
        }

        var minimum = ProjectAccessLevel.Editor;

        // Owner-only ACL mutations are still Editor+ at middleware; handlers enforce owner.
        // Seed the ACL for projects that predate the ACL system. GetOrCreateAclAsync resolves the
        // real owner from project.json itself when possible; userId here is only the last-resort
        // fallback if that lookup fails — never a guess derived from the project path (path segments
        // are a sanitized slug, e.g. a pre-migration email turned into "budcribargmail_com", which is
        // NOT the account's real user id and must never be treated as though it were).
        try
        {
            await acl.GetOrCreateAclAsync(projectId, userId, context.RequestAborted);
        }
        catch
        {
            // project dir may not exist yet — still evaluate access
        }

        var allowed = await acl.CanAccessAsync(projectId, userId, minimum, context.RequestAborted);
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "project_access_denied",
                projectId,
                minimum = minimum.ToString(),
            }, cancellationToken: context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
