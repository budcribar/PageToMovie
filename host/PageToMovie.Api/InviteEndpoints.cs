using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;

namespace PageToMovie.Api;

public static class InviteEndpoints
{
    public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{id}/acl/invite", InviteAsync);
        app.MapPost("/api/projects/{id}/acl/invites/resend", ResendAsync);
        app.MapDelete("/api/projects/{id}/acl/invites/{key}", RevokeAsync);
        app.MapGet("/api/invites/{token}", PreviewAsync);
        app.MapPost("/api/invites/{token}/accept", AcceptAsync);
        return app;
    }

    private static async Task<IResult> InviteAsync(
        string id, ProjectAclService acl, IUserContext user, IConfiguration config, HttpRequest req, CancellationToken ct)
    {
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;
            string? username;
            if (root.TryGetProperty("username", out var u))
                username = u.GetString();
            else if (root.TryGetProperty("email", out var em))
                username = em.GetString();
            else
                username = null;
            var role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "editor" : "editor";
            if (string.IsNullOrWhiteSpace(username))
                return Results.BadRequest(new { ok = false, error = "username or email required" });
            var publicBase = config["PublicBaseUrl"] ?? $"{req.Scheme}://{req.Host.Value}";
            var result = await acl.InviteByUsernameAsync(id, username, role, user.UserId ?? "", publicBase, ct);
            if (!result.Ok) return Results.BadRequest(new { ok = false, error = result.Error });
            return Results.Ok(new {
                ok = true, status = result.Status, userId = result.UserId, role = result.Role,
                token = result.Token, inviteLink = result.InviteLink, emailSent = result.EmailSent,
                message = result.Message, acl = await acl.GetAclAsync(id, ct)
            });
        }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
        catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    }

    private static async Task<IResult> ResendAsync(
        string id, ProjectAclService acl, IUserContext user, IConfiguration config, HttpRequest req, CancellationToken ct)
    {
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;
            string? key;
            if (root.TryGetProperty("username", out var u))
                key = u.GetString();
            else if (root.TryGetProperty("email", out var em))
                key = em.GetString();
            else if (root.TryGetProperty("token", out var tok))
                key = tok.GetString();
            else
                key = null;
            if (string.IsNullOrWhiteSpace(key))
                return Results.BadRequest(new { ok = false, error = "username, email, or token required" });
            var publicBase = config["PublicBaseUrl"] ?? $"{req.Scheme}://{req.Host.Value}";
            var result = await acl.ResendInviteAsync(id, key, user.UserId ?? "", publicBase, ct);
            if (!result.Ok) return Results.BadRequest(new { ok = false, error = result.Error });
            return Results.Ok(new {
                ok = true, status = result.Status, inviteLink = result.InviteLink,
                emailSent = result.EmailSent, message = result.Message, token = result.Token
            });
        }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
        catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    }

    private static async Task<IResult> RevokeAsync(
        string id, string key, ProjectAclService acl, IUserContext user, CancellationToken ct)
    {
        try
        {
            var a = await acl.RevokeInviteAsync(id, Uri.UnescapeDataString(key), user.UserId ?? "", ct);
            return Results.Ok(new { ok = true, acl = a });
        }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
        catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    }

    private static async Task<IResult> PreviewAsync(string token, ProjectAclService acl, CancellationToken ct)
    {
        var found = await acl.FindInviteByTokenAsync(token, ct);
        if (found is null) return Results.NotFound(new { ok = false, error = "Invite not found or already used." });
        var (projectId, inv) = found.Value;
        return Results.Ok(new {
            ok = true, projectId, role = inv.Role, email = inv.Email,
            username = inv.Username, invitedBy = inv.InvitedBy, createdUtc = inv.CreatedUtc
        });
    }

    private static async Task<IResult> AcceptAsync(
        string token, ProjectAclService acl, IUserContext user, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(user.UserId))
                return Results.Json(new { ok = false, error = "Sign in to accept this invite." }, statusCode: 401);
            var (ok, projectId, error) = await acl.AcceptInviteAsync(token, user.UserId, ct: ct);
            if (!ok) return Results.BadRequest(new { ok = false, error });
            return Results.Ok(new { ok = true, projectId });
        }
        catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    }
}
