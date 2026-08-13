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

public static class MiscEndpoints
{
    public static IEndpointRouteBuilder MapMiscEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me/api-calls", GetMeApiCalls);
        app.MapGet("/api/capabilities", GetCapabilities);
        // <summary>
        // Signed-in user's spend: grand total, by project, by vendor, by category.
        // Optional <c>?projectId=</c> filters to one project.
        // </summary>
        app.MapGet("/api/me/spend", GetMeSpend);
        // <summary>Public stream for a shared WIP (no login — token is the capability).</summary>
        app.MapGet("/api/share/{token}", GetShareToken);
        // <summary>CORS-safe download of provider video URL (short-lived ticket from gen job).</summary>
        // Speech-to-text (ElevenLabs Scribe) for voice-capture verification: the client uploads an
        // extracted dialogue segment and we return the transcript (+ word timings). Used to confirm a
        // detected window contains the expected narrator line — never for the user's own takes.
        app.MapPost("/api/transcribe", PostTranscribe);
        return app;
    }

    private static async Task<IResult> GetMeApiCalls(int? take,
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var rows = await userDb.ListUserApiCallsAsync(user.UserId, take ?? 100, ct);
    var totalUsd = rows.Where(r => r.EstimatedUsd is > 0).Sum(r => r.EstimatedUsd.GetValueOrDefault());
    return Results.Ok(new
    {
        ok = true,
        userId = user.UserId,
        count = rows.Count,
        estimatedUsdSum = Math.Round(totalUsd, 4),
        notes = "List-rate estimates at call time (catalog). Not provider invoices. Full prompts stay on the project telemetry file.",
        calls = rows,
    });
}

    private static IResult GetCapabilities(IVideoClient video,
    IImageClient image,
    IVisionClient vision,
    IAudioClient audio,
    IVoiceClient voice,
    IChatClient chat)
    {
    var caps = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        [ApiText.VideoFolder] = video.IsConfigured,
        ["image"] = image.IsConfigured,
        ["vision"] = vision.IsConfigured,
        ["review"] = vision.IsConfigured,   // multimodal auto-review runs on the vision client
        ["music"] = audio.IsConfigured,
        ["voice"] = voice.IsConfigured,
        ["planning"] = chat.IsConfigured,
    };

    // Dev/testing affordance: force capabilities off (comma-separated) to preview and test the
    // gated UI — fakes mode reports everything configured, so the disabled state is otherwise
    // unreachable locally. No effect in production unless the env var is set.
    var forcedOff = Environment.GetEnvironmentVariable("PAGETOMOVIE_FAKE_DISABLED_CAPABILITIES");
    if (!string.IsNullOrWhiteSpace(forcedOff))
    {
        foreach (var c in forcedOff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(caps.ContainsKey))
            caps[c] = false;
    }

    return Results.Ok(new { ok = true, capabilities = caps });
}

    private static async Task<IResult> GetMeSpend(IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    string? projectId,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var uid = user.UserId?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(uid))
        return Results.Unauthorized();
    var summary = await userDb.GetUserSpendSummaryAsync(uid, projectId, ct);
    return Results.Ok(new
    {
        ok = true,
        summary,
        notes = "Per-user tracking from user_api_calls. Charge = list × admin multiplier. Provider = catalog vendor id (xai, google, elevenlabs, …).",
    });
}

    private static async Task<IResult> GetShareToken(string token, MediaShareService shares, ProjectStore store, CancellationToken ct)
    {
    var rec = await shares.TryGetAsync(token, ct);
    if (rec is null)
        return Results.NotFound(new { ok = false, error = "Share link not found or expired" });
    if (!string.Equals(rec.Kind, "wip", StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(new { ok = false, error = "Unsupported share kind" });
    try
    {
        var path = store.ResolveWipMoviePath(rec.ProjectId);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "Shared movie is no longer available" });
        return Results.File(path, SpecializedMimeType.VideoMp4.ToMimeTypeString(), enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostTranscribe(HttpRequest request,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    ElevenLabsScribeClient scribe,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!request.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form with audio 'file' required" });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "audio file required" });

    var lang = form["language_code"].ToString();
    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);

    var result = await scribe.TranscribeAsync(
        ms.ToArray(), file.FileName, string.IsNullOrWhiteSpace(lang) ? null : lang, ct);
    if (!result.Ok)
        return Results.Json(new { ok = false, error = result.Error }, statusCode: StatusCodes.Status502BadGateway);

    return Results.Ok(new
    {
        ok = true,
        text = result.Text,
        languageCode = result.LanguageCode,
        words = result.Words.Select(w => new { text = w.Text, start = w.Start, end = w.End, type = w.Type }),
    });
}
}
