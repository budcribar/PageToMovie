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

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/signup", PostAuthSignup);
        app.MapPost("/api/auth/login", PostAuthLogin);
        app.MapPost("/api/auth/logout", PostAuthLogout);
        // <summary>
        // Forgot password — emails a reset link when the account has an email; also marks admin request.
        // Always returns the same generic success message (no user enumeration).
        // </summary>
        app.MapPost("/api/auth/forgot-password", PostAuthForgotPassword);
        // <summary>Confirm email with one-time token from signup email.</summary>
        app.MapPost("/api/auth/confirm-email", PostAuthConfirmEmail);
        // <summary>Resend confirmation email (by username or email).</summary>
        app.MapPost("/api/auth/resend-confirmation", PostAuthResendConfirmation);
        // <summary>Complete password reset with token from email.</summary>
        app.MapPost("/api/auth/reset-password", PostAuthResetPassword);
        // <summary>
        // Short-lived media token for &lt;img&gt;/&lt;video src&gt; query auth (?mt=).
        // Requires a full session Bearer JWT. Media tokens carry token_use=media and expire in ~30m.
        // </summary>
        app.MapPost("/api/auth/media-token", PostAuthMediaToken);
        // <summary>
        // Operator override: POST { "secret": "…" } matching PageToMovie_LOGIN_OVERRIDE.
        // Used by <c>?me=SECRET</c> bootstrap on Railway (not localhost-only).
        // </summary>
        app.MapPost("/api/auth/operator-override", PostAuthOperatorOverride);
        // DEV ONLY: fakes-mode login bypass. When the whole server runs on fakes
        // (PageToMovie:UseFakes), the WASM UI calls this on boot to auto-sign-in a deterministic dev user
        // so the app is browsable end-to-end without a login screen. Hard-gated on UseFakes at BOTH the
        // endpoint (returns 404) and the service (IssueDevFakesLogin fails closed) — a real (non-fakes)
        // deployment can never mint a session here.
        app.MapPost("/api/auth/dev-login", PostAuthDevLogin);
        app.MapGet("/api/auth/me", GetAuthMe);
        return app;
    }

    private static async Task<IResult> PostAuthSignup(LoginRequest body, IAdminAuthService auth, LoginRateLimiter limiter, HttpContext http)
    {
    var key = $"{body.Username ?? ""}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new LoginResponse { Ok = false, Error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var result = await auth.SignupAsync(body.Username ?? "", body.Password ?? "", body.Email, http.RequestAborted);
    if (!result.Ok)
    {
        limiter.RecordFailure(key);
        return Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
    }
    limiter.RecordSuccess(key);
    return Results.Ok(result);
}

    private static async Task<IResult> PostAuthLogin(LoginRequest body, IAdminAuthService auth, LoginRateLimiter limiter, HttpContext http)
    {
    var key = $"{body.Username ?? ""}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new LoginResponse { Ok = false, Error = $"Too many login attempts. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var result = await auth.LoginAsync(body.Username ?? "", body.Password ?? "", http.RequestAborted);
    if (!result.Ok)
    {
        limiter.RecordFailure(key);
        return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
    }
    limiter.RecordSuccess(key);
    return Results.Ok(result);
}

    private static IResult PostAuthLogout() =>
        Results.Ok(new { ok = true, message = "Client should discard JWT" });

    private static async Task<IResult> PostAuthForgotPassword(ForgotPasswordRequest? body,
    UserDatabaseService userDb,
    IAdminAuthService auth,
    LoginRateLimiter limiter,
    HttpContext http)
    {
    var name = (body?.Username ?? "").Trim();
    var key = $"forgot|{name}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new { ok = false, error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (name.Length >= 3 || name.Contains('@'))
    {
        try
        {
            await userDb.NotePasswordResetRequestedAsync(name, http.RequestAborted);
            var user = await userDb.ResolveUserAsync(name, http.RequestAborted)
                       ?? await userDb.GetUserByEmailAsync(name, http.RequestAborted);
            if (user is not null && !user.IsDisabled && !string.IsNullOrWhiteSpace(user.Email) &&
                auth is AdminAuthService concrete)
            {
                await concrete.SendPasswordResetEmailAsync(user, http.RequestAborted);
            }
        }
        catch { /* never leak */ }
    }

    limiter.RecordSuccess(key);
    return Results.Ok(new
    {
        ok = true,
        message = "If that account exists and has a confirmed email, a reset link was sent to your inbox.",
    });
}

    private static async Task<IResult> PostAuthConfirmEmail(ConfirmEmailRequest? body,
    UserDatabaseService userDb)
    {
    var token = (body?.Token ?? "").Trim();
    if (token.Length < 10)
        return Results.BadRequest(new { ok = false, error = "Invalid or missing token." });

    var userId = await userDb.ConsumeAuthTokenAsync(token, UserDatabaseService.AuthPurposeEmailConfirm);
    if (userId is null)
    {
        var existingUserId = await userDb.GetUserIdFromAuthTokenHashAsync(token, UserDatabaseService.AuthPurposeEmailConfirm);
        if (existingUserId is not null)
        {
            var user = await userDb.ResolveUserAsync(existingUserId);
            if (UserDatabaseService.IsEmailConfirmed(user))
            {
                return Results.Ok(new { ok = true, message = "Email is already confirmed. You can sign in now." });
            }
        }
        return Results.BadRequest(new { ok = false, error = "This confirmation link is invalid or expired." });
    }

    await userDb.ConfirmEmailAsync(userId);
    return Results.Ok(new { ok = true, message = "Email confirmed. You can sign in now." });
}

    private static async Task<IResult> PostAuthResendConfirmation(ForgotPasswordRequest? body,
    UserDatabaseService userDb,
    IAdminAuthService auth,
    LoginRateLimiter limiter,
    HttpContext http)
    {
    var name = (body?.Username ?? "").Trim();
    var key = $"reconfirm|{name}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new { ok = false, error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    try
    {
        var user = await userDb.ResolveUserAsync(name, http.RequestAborted) ?? await userDb.GetUserByEmailAsync(name, http.RequestAborted);
        if (user is not null && !UserDatabaseService.IsEmailConfirmed(user) && auth is AdminAuthService concrete)
            await concrete.SendEmailConfirmAsync(user, http.RequestAborted);
    }
    catch (Exception ex)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PageToMovie.Api")
            .LogError(ex, "Failed to resend confirmation email to user={Name}", name);
    }

    limiter.RecordSuccess(key);
    return Results.Ok(new
    {
        ok = true,
        message = "If that account needs confirmation, a new email was sent (or logged in development).",
    });
}

    private static async Task<IResult> PostAuthResetPassword(ResetPasswordWithTokenRequest? body,
    UserDatabaseService userDb)
    {
    var token = (body?.Token ?? "").Trim();
    var pw = body?.NewPassword ?? "";
    if (token.Length < 10)
        return Results.BadRequest(new { ok = false, error = "Invalid or missing token." });
    if (pw.Length < 4)
        return Results.BadRequest(new { ok = false, error = "Password must be at least 4 characters." });

    var userId = await userDb.ConsumeAuthTokenAsync(token, UserDatabaseService.AuthPurposePasswordReset);
    if (userId is null)
        return Results.BadRequest(new { ok = false, error = "This reset link is invalid or expired." });

    if (!await userDb.SetPasswordAsync(userId, pw))
        return Results.BadRequest(new { ok = false, error = "Could not update password." });

    // If they had unconfirmed email, allow login after proving inbox via reset link
    await userDb.ConfirmEmailAsync(userId);

    return Results.Ok(new { ok = true, message = "Password updated. You can sign in." });
}

    private static IResult PostAuthMediaToken(HttpContext http, IAdminAuthService auth, IUserContext user)
    {
    if (http.User?.Identity?.IsAuthenticated != true)
        return Results.Json(new { ok = false, error = "Sign in required" }, statusCode: StatusCodes.Status401Unauthorized);
    // Must be a full session token, not another media token (prevents refresh loops with weak tokens).
    if (auth.IsMediaToken(http.User))
        return Results.Json(new { ok = false, error = "Use a session JWT (Authorization: Bearer)" }, statusCode: StatusCodes.Status401Unauthorized);

    try
    {
        var token = auth.IssueMediaToken(http.User);
        var expires = DateTimeOffset.UtcNow.AddMinutes(IAdminAuthService.MediaTokenMinutes);
        return Results.Ok(new
        {
            ok = true,
            token,
            expiresAt = expires,
            tokenUse = IAdminAuthService.TokenUseMedia,
            minutes = IAdminAuthService.MediaTokenMinutes,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult PostAuthOperatorOverride(OperatorOverrideRequest? body, IAdminAuthService auth, LoginRateLimiter limiter, HttpContext http)
    {
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var key = $"override|{ip}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new LoginResponse { Ok = false, Error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var result = auth.LoginWithOperatorOverride(body?.Secret ?? "");
    if (!result.Ok)
    {
        limiter.RecordFailure(key);
        return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
    }
    limiter.RecordSuccess(key);
    return Results.Ok(result);
}

    private static IResult PostAuthDevLogin(IAdminAuthService auth, IOptions<PageToMovieOptions> opts)
    {
    if (!opts.Value.UseFakes)
        return Results.NotFound();
    var result = auth.IssueDevFakesLogin();
    return result.Ok ? Results.Ok(result) : Results.NotFound();
}

    private static async Task<IResult> GetAuthMe(IUserContext user, IUserApiKeyProvider keys, UserDatabaseService userDb)
    {
    var roles = user.Roles.ToList();
    var personal = false;
    try
    {
        personal = !string.IsNullOrWhiteSpace(
            await userDb.GetDecryptedXaiApiKeyAsync(user.UserId).ConfigureAwait(false));
    }
    catch { /* ignore */ }

    return Results.Ok(new MeResponse
    {
        Ok = true,
        UserId = user.UserId,
        Roles = roles,
        IsAdmin = user.IsAdmin,
        IsAuthenticated = user.IsAuthenticated,
        // Personal key only when signed in; otherwise false even if server env has XAI_API_KEY.
        HasApiKey = user.IsAuthenticated && (personal || !string.IsNullOrWhiteSpace(user.RequestApiKey)),
    });
}
}
