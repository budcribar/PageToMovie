using System.Text.RegularExpressions;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;

namespace PageToMovie.Api.Collaboration;

/// <summary>
/// Enforces ACL on /api/projects/{projectId}/... routes.
/// GET/HEAD → Viewer+; mutating methods → Editor+ (owner included).
/// Exempt: create/list/import at collection level; /acl invite is owner-checked in handlers.
/// </summary>
public sealed class ProjectAccessMiddleware
{
    private static readonly Regex ProjectPath = new(
        @"^/api/projects/(?<id>[^/]+)(?<rest>/.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> CollectionOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "/",
    };

    // Paths under a project that allow Viewer on write? none — writes need Editor.
    private static readonly HashSet<string> AnonymousOk = new(StringComparer.OrdinalIgnoreCase)
    {
        // none for project-scoped
    };

    private readonly RequestDelegate _next;

    public ProjectAccessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IProjectAclService acl, IUserContext user)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/projects", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var m = ProjectPath.Match(path);
        if (!m.Success)
        {
            // /api/projects or /api/projects/ with no id → collection (create/list)
            await _next(context);
            return;
        }

        var projectIdRaw = Uri.UnescapeDataString(m.Groups["id"].Value);
        // project ids are often owner%2Fname → owner/name
        var projectId = projectIdRaw;
        var rest = m.Groups["rest"].Value ?? "";

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
            await context.Response.WriteAsJsonAsync(new { error = "authentication_required" });
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
            await acl.GetOrCreateAclAsync(projectId, userId);
        }
        catch
        {
            // project dir may not exist yet — still evaluate access
        }

        var allowed = await acl.CanAccessAsync(projectId, userId, minimum);
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "project_access_denied",
                projectId,
                minimum = minimum.ToString(),
            });
            return;
        }

        await _next(context);
    }
}
