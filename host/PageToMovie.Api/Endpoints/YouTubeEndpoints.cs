using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Api;

public static class YouTubeEndpoints
{
    public static IEndpointRouteBuilder MapYouTubeEndpoints(this IEndpointRouteBuilder app)
    {
        // Shared, instance-wide YouTube channel connection (not per-user). Status is readable by
        // anyone; connecting/disconnecting the channel is admin-only.
        app.MapGet("/api/youtube/status", GetYoutubeStatus);
        app.MapGet("/api/youtube/connect-url", GetYoutubeConnectUrl);
        app.MapGet("/api/youtube/oauth2callback/{*remainder}", ProcessYouTubeOAuthCallbackAsync);
        app.MapGet("/api/youtube/oauth2callback", ProcessYouTubeOAuthCallbackAsync);
        app.MapPost("/api/youtube/disconnect", PostYoutubeDisconnect);
        return app;
    }

    private static async Task<IResult> GetYoutubeStatus(YouTubeAuthService youTube, CancellationToken ct)
    {
    var connected = youTube.IsConfigured && await youTube.IsConnectedAsync(ct);
    return Results.Ok(new { ok = true, configured = youTube.IsConfigured, connected });
}

    private static IResult GetYoutubeConnectUrl(IUserContext user, YouTubeAuthService youTube, string? returnTo)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    if (!youTube.IsConfigured)
        return Results.Json(new
        {
            ok = false,
            error = "YouTube OAuth is not configured (PageToMovie:YouTube:ClientId/ClientSecret/RedirectUri).",
        }, statusCode: StatusCodes.Status409Conflict);
    var state = Guid.NewGuid().ToString("N");
    return Results.Ok(new { ok = true, url = youTube.BuildAuthorizationUrl(state, returnTo) });
}

    private static async Task<IResult> PostYoutubeDisconnect(IUserContext user, YouTubeAuthService youTube, CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    await youTube.DisconnectAsync(ct);
    return Results.Ok(new { ok = true });
}

    private static readonly TimeSpan OAuthTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex OAuthCodeParamRegex = new(@"code=([^&]+)", RegexOptions.Compiled, OAuthTimeout);
    private static readonly Regex OAuthStateParamRegex = new(@"state=([^&]+)", RegexOptions.Compiled, OAuthTimeout);
    private static readonly Regex OAuthErrorParamRegex = new(@"error=([^&]+)", RegexOptions.Compiled, OAuthTimeout);

    internal static async Task ProcessYouTubeOAuthCallbackAsync(HttpContext http, YouTubeAuthService youTube, CancellationToken ct)
    {
        var code = http.Request.Query["code"].FirstOrDefault();
        var state = http.Request.Query["state"].FirstOrDefault();
        var error = http.Request.Query["error"].FirstOrDefault();

        // Fallback: If parameters were not bound from query (e.g. proxy path normalization), extract from raw request URL
        var rawUrl = (http.Request.Path.Value ?? "") + (http.Request.QueryString.Value ?? "");
        if (string.IsNullOrWhiteSpace(code))
        {
            var mCode = OAuthCodeParamRegex.Match(rawUrl);
            if (mCode.Success)
                code = Uri.UnescapeDataString(mCode.Groups[1].Value);
        }
        if (string.IsNullOrWhiteSpace(state))
        {
            var mState = OAuthStateParamRegex.Match(rawUrl);
            if (mState.Success)
                state = Uri.UnescapeDataString(mState.Groups[1].Value);
        }
        if (string.IsNullOrWhiteSpace(error))
        {
            var mErr = OAuthErrorParamRegex.Match(rawUrl);
            if (mErr.Success)
                error = Uri.UnescapeDataString(mErr.Groups[1].Value);
        }

        var returnPath = "/review";
        var stateOk = !string.IsNullOrWhiteSpace(state) && youTube.TryConsumeState(state, out returnPath);

        if (!string.IsNullOrWhiteSpace(error))
        {
            http.Response.Redirect($"{returnPath}?youtube=error&message={Uri.EscapeDataString(error)}");
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            http.Response.Redirect(returnPath + "?youtube=error&message=" + Uri.EscapeDataString("Missing authorization code from Google."));
            return;
        }

        if (!stateOk)
        {
            http.Response.Redirect(returnPath + "?youtube=error&message=" + Uri.EscapeDataString("Invalid or expired request."));
            return;
        }

        try
        {
            await youTube.ExchangeCodeAsync(code, ct);
            http.Response.Redirect($"{returnPath}?youtube=connected");
        }
        catch (Exception ex)
        {
            http.Response.Redirect($"{returnPath}?youtube=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }
}
