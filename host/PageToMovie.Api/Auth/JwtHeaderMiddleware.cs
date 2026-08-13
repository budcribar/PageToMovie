using System.Security.Claims;
using PageToMovie.Engine;

namespace PageToMovie.Api.Auth;

/// <summary>
/// Accepts Authorization: Bearer JWT (full session) and short-lived media tokens
/// via query <c>mt</c> for &lt;video&gt;/&lt;img&gt; (cannot send Authorization).
/// Full session JWTs are never accepted from the query string.
/// Rejects principals for accounts that have been admin-disabled.
/// </summary>
public sealed class JwtHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public JwtHeaderMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, IAdminAuthService auth, UserDatabaseService userDb)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            var principal = TryAuthenticateFromHeader(ctx, auth)
                            ?? TryAuthenticateFromMediaQuery(ctx, auth);

            if (principal is not null)
            {
                if (await TryRejectDisabledAccount(ctx, principal, userDb).ConfigureAwait(false))
                    return;

                ctx.User = principal;
            }
        }

        await _next(ctx);
    }

    private static ClaimsPrincipal? TryAuthenticateFromHeader(HttpContext ctx, IAdminAuthService auth)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var bearer = header["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(bearer))
            return null;

        return auth.ValidateToken(bearer);
    }

    private static ClaimsPrincipal? TryAuthenticateFromMediaQuery(HttpContext ctx, IAdminAuthService auth)
    {
        var mediaRaw = TryQueryToken(ctx, "mt")
                       ?? TryQueryToken(ctx, "media_token");
        // Legacy name: still parse but only accept if media-scoped.
        mediaRaw ??= TryQueryToken(ctx, "access_token");

        if (string.IsNullOrWhiteSpace(mediaRaw))
            return null;

        var candidate = auth.ValidateToken(mediaRaw);
        if (candidate is not null && auth.IsMediaToken(candidate))
            return candidate;

        return null;
    }

    private static async Task<bool> TryRejectDisabledAccount(
        HttpContext ctx, ClaimsPrincipal principal, UserDatabaseService userDb)
    {
        var uid = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub")
                  ?? principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(uid) ||
            !await userDb.IsUserDisabledAsync(uid, ctx.RequestAborted).ConfigureAwait(false))
            return false;

        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "This account has been disabled.",
            code = "account_disabled",
        }, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
        return true;
    }

    private static string? TryQueryToken(HttpContext ctx, string name)
    {
        if (!ctx.Request.Query.TryGetValue(name, out var q) || string.IsNullOrWhiteSpace(q))
            return null;
        return q.ToString().Trim();
    }
}
