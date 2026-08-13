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

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/users/{id}/terms", GetUsersIdTerms);
        app.MapPost("/api/users/terms/accept", PostUsersTermsAccept);
        app.MapGet("/api/creators/{handle}", GetCreatorsHandle);
        // Phase 6: Privacy Search & Invite Delivery — handles only (never emails)
        app.MapGet("/api/users/search", GetUsersSearch);
        // <summary>Accept an invite (must be signed in): forks the project under the accepting user.</summary>
        app.MapPost("/api/invites/accept", PostInvitesAccept);
        app.MapGet("/api/user/settings", GetUserSettings);
        app.MapPost("/api/user/settings", PostUserSettings);
        return app;
    }

    private static async Task<IResult> GetUsersIdTerms(string id, UserDatabaseService userDb)
    {
    var hasAccepted = await userDb.HasAcceptedTermsAsync(id);
    return Results.Ok(new { hasAccepted, accepted = hasAccepted });
}

    private static async Task<IResult> PostUsersTermsAccept(AcceptTermsRequest body, UserDatabaseService userDb)
    {
    var ok = await userDb.AcceptTermsAsync(body.UserId, body.Version ?? "1.0");
    return Results.Ok(new { ok });
}

    private static async Task<IResult> GetCreatorsHandle(string handle,
    CreatorProfileService creatorService,
    CancellationToken ct)
    {
    var profile = await creatorService.GetProfileAsync(handle, ct);
    if (profile == null)
        return Results.NotFound(new { ok = false, error = "Creator profile not found." });
    return Results.Ok(profile);
}

    private static async Task<IResult> GetUsersSearch(string? q,
    UserDatabaseService userDb,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (string.IsNullOrWhiteSpace(q) || q.Trim().TrimStart('@').Length < 1)
        return Results.Ok(new { ok = true, handles = Array.Empty<string>() });

    var found = await userDb.SearchUsernamesAsync(q, take: 15, ct);
    var handles = found.Select(u => u.StartsWith('@') ? u : "@" + u).ToList();
    return Results.Ok(new { ok = true, handles });
}

    private static async Task<IResult> PostInvitesAccept(AcceptInviteApiRequest? body,
    ProjectInviteService invites,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var token = (body?.Token ?? "").Trim();
    if (token.Length < 10)
        return Results.BadRequest(new { ok = false, error = "Invalid or missing invite token." });

    var outcome = await invites.ConsumeAsync(token, user.UserId ?? "", ct);
    if (!outcome.Ok || outcome.ProjectId is null)
        return Results.BadRequest(new { ok = false, error = outcome.Error ?? "Could not accept this invite." });

    try
    {
        var fork = await store.ForkProjectAsync(outcome.ProjectId, user.UserId!, isInvite: true, ct);
        await books.LinkForkAsync(outcome.ProjectId, user.UserId!, fork.Id, invitationAuthorized: true, ct);
        return Results.Ok(new { ok = true, projectId = fork.Id, title = fork.Title });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetUserSettings(IUserContext userCtx,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(userCtx, opts) is { } denied)
        return denied;
    try
    {
        var settings = await userDb.GetUserSettingsDtoAsync(userCtx.UserId, ct);
        return Results.Ok(new { ok = true, settings });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostUserSettings(UpdateUserSettingsRequest req,
    IUserContext userCtx,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(userCtx, opts) is { } denied)
        return denied;
    try
    {
        // Null fields leave existing keys; empty string clears that provider's personal key.
        await userDb.UpdateUserSettingsAsync(userCtx.UserId, req, ct);
        var updated = await userDb.GetUserSettingsDtoAsync(userCtx.UserId, ct);
        var saved = new List<string>();
        if (req.XaiApiKey is not null) saved.Add("xAI / Grok");
        if (req.GeminiApiKey is not null) saved.Add("Gemini");
        if (req.AnthropicApiKey is not null) saved.Add("Claude");
        if (req.FalApiKey is not null) saved.Add("Fal.ai");
        var msg = saved.Count > 0
            ? $"Saved personal key(s): {string.Join(", ", saved)}."
            : "No key fields provided.";
        return Results.Ok(new { ok = true, settings = updated, message = msg });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}
