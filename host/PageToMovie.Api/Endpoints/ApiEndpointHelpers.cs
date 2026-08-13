using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Api.Collaboration;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;

namespace PageToMovie.Api;

internal static class ApiEndpointHelpers
{
    /// <summary>Picture-book PDF / fountain / txt import. Matches Adaptation Import UI (80 MB).</summary>
    public const long BookImportBytes = 80L * 1024 * 1024;

    /// <summary>Voice-clone sample audio. Matches Characters voice UI (15 MB).</summary>
    public const long VoiceSampleBytes = 15L * 1024 * 1024;

    /// <summary>
    /// Per-route Kestrel + multipart cap. Tightens the app-wide 512 MB ceiling for this endpoint.
    /// </summary>
    public static RouteHandlerBuilder WithUploadSizeLimit(this RouteHandlerBuilder builder, long bytes) =>
        builder
            .WithMetadata(new RequestSizeLimitAttribute(bytes))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = bytes });

    public static string WindowsExplorerPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public static string UnixOpenPath() =>
        OperatingSystem.IsMacOS() ? Path.Combine(Path.DirectorySeparatorChar.ToString(), "usr", "bin", "open")
                                  : Path.Combine(Path.DirectorySeparatorChar.ToString(), "usr", "bin", "xdg-open");

    // Shared admin/operator authorization gate. Returns null when the caller is authorized (admin
    // role, or the operator override secret supplied via ?me / ?admin_key / X-Admin-Key header), or a
    // 403 JSON result to short-circuit the endpoint otherwise. Secret comparison is Ordinal.
    public static IResult? RequireAdminOrOperator(HttpContext http, IUserContext user, IOptions<PageToMovieOptions> opts)
    {
        var secret = AuthOptions.ResolveOperatorOverrideSecret(opts.Value.Auth);
        var isOperator = !string.IsNullOrWhiteSpace(secret) &&
            (string.Equals(http.Request.Query["me"].ToString(), secret, StringComparison.Ordinal) ||
             string.Equals(http.Request.Query["admin_key"].ToString(), secret, StringComparison.Ordinal) ||
             string.Equals(http.Request.Headers["X-Admin-Key"].ToString(), secret, StringComparison.Ordinal));

        if (!user.IsAdmin && !isOperator)
            return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    // Shared owner/admin gate for project-mutating endpoints. Loads the project (throwing the usual
    // not-found when absent) and returns null when the caller may mutate it, or a 403 JSON result
    // carrying the endpoint-specific <paramref name="denyMessage"/> otherwise. Mirrors the inline
    // RequireProject + CanUserPublishDemo prologue these endpoints previously repeated verbatim.
    public static async Task<IResult?> RequireProjectOwnerOrAdmin(
        string id, ProjectStore store, IUserContext user, string denyMessage, CancellationToken ct)
    {
        await store.RequireProjectAsync(id, ct);
        if (!await store.CanUserPublishDemoAsync(id, user.UserId, user.IsAdmin, ct))
            return Results.Json(new { ok = false, error = denyMessage },
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    // Shared body for the clip-version / audio-take mutation endpoints (promote / soft-delete /
    // restore / trash-restore): login gate → load project → run the store mutation → map its
    // success bool to the standard fail/success JSON. Only the store call and the two messages vary.
    public static async Task<IResult> RunProjectVersionActionAsync(
        string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts,
        Func<Task<bool>> action, string failureError, string successMessage, CancellationToken ct)
    {
        if (AuthGate.RequireLogin(user, opts) is { } denied)
            return denied;
        try
        {
            await store.RequireProjectAsync(id, ct);
            var success = await action();
            if (!success)
                return Results.BadRequest(new { ok = false, error = failureError });
            return Results.Ok(new { ok = true, message = successMessage });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message });
        }
    }

    // Shared response shaping for the adaptation draft-edit endpoints (reskin / embellish / trim):
    // they all run a ScreenplayService.*DraftAsync, then map the shared DraftEditResult to the same
    // fail/success JSON and (on apply) auto-commit with an endpoint-specific tag.
    public static async Task<IResult> DraftEditResponseAsync(
        ScreenplayService.DraftEditResult result, string id, string commitTag,
        ProjectStore store, IUserContext user, CancellationToken ct = default)
    {
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        if (result.Applied)
            store.TriggerAutoGitCommit(id, commitTag);

        return Results.Ok(new
        {
            ok = true,
            applied = result.Applied,
            projectId = id,
            message = result.Message,
            sceneCountBefore = result.SceneCountBefore,
            sceneCountAfter = result.SceneCountAfter,
            screenplay = result.Status,
            adaptation = result.Applied ? await store.GetAdaptationStatusAsync(id, user.UserId, ct) : null,
        });
    }

    /// <summary>
    /// Record a user override of the portrait style classifier into the AI-call telemetry stream —
    /// the highest-signal feedback there is (a human explicitly overruling a model verdict). The
    /// reason distinguishes "classifier was wrong" (a defect to tune) from "my creative choice"
    /// (the classifier was right and the user wants mixed media — not a defect).
    /// </summary>

    /// <summary>Shared body parse for lock-variant / lock-bookref (index + style override fields).</summary>
    public static async Task<(int Index, bool OverrideStyle, string? Reason, string? Note)> ParseCharacterLockBodyAsync(
        HttpRequest req, int defaultIndex, bool acceptVariantIndexAlias = false)
    {
        var index = defaultIndex;
        var overrideStyle = false;
        string? overrideReason = null, overrideNote = null;
        if (req.HasJsonContentType())
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            if (doc.RootElement.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n))
                index = n;
            else if (acceptVariantIndexAlias
                     && doc.RootElement.TryGetProperty("variantIndex", out var vx)
                     && vx.TryGetInt32(out var n2))
                index = n2;
            if (doc.RootElement.TryGetProperty("overrideStyle", out var os) && os.ValueKind is JsonValueKind.True or JsonValueKind.False)
                overrideStyle = os.GetBoolean();
            if (doc.RootElement.TryGetProperty("overrideReason", out var orr) && orr.ValueKind == JsonValueKind.String)
                overrideReason = orr.GetString();
            if (doc.RootElement.TryGetProperty("overrideNote", out var onote) && onote.ValueKind == JsonValueKind.String)
                overrideNote = onote.GetString();
        }
        return (index, overrideStyle, overrideReason, overrideNote);
    }

    /// <summary>
    /// Acquire the cast resource lease for a mutating character endpoint. Returns a 423 result when
    /// another user holds the lease; null when the caller may proceed (including when user id or
    /// char key is empty — those call sites skip the lease, matching the previous inline checks).
    /// </summary>
    public static async Task<IResult?> TryAcquireCastLeaseAsync(
        string projectId,
        string charKey,
        IProjectLeaseService leases,
        IUserContext user,
        CancellationToken ct)
    {
        var uid = user.UserId ?? "";
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(charKey))
            return null;
        var (okLease, lease) = await leases.TryAcquireAsync(
            projectId, ProjectLeaseKeys.Cast(charKey), uid,
            CollaborationEndpoints.DefaultLeaseTtl, ct);
        if (okLease)
            return null;
        return Results.Json(new
        {
            ok = false,
            error = "cast_locked",
            message = $"Cast is locked by {lease.HolderUserId}.",
            holderUserId = lease.HolderUserId,
        }, statusCode: StatusCodes.Status423Locked);
    }

    public static async Task LogStyleOverrideAsync(
        ProjectTelemetryService telemetry,
        IOptions<PageToMovieOptions> opts,
        string projectId,
        string charKey,
        string? reason,
        string? note)
    {
        try
        {
            await telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "style_gate_override",
                ProjectId = projectId,
                CharKey = charKey,
                // ai_wrong | user_preference | other — the user's stated reason for overriding.
                Mode = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim().ToLowerInvariant(),
                Error = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                Fakes = opts.Value.UseFakes,
                Ok = true,
            });
        }
        catch { /* telemetry is best-effort */ }
    }

    public static IResult JobStartError(Exception ex, FilmJobService jobService) => ex switch
    {
        LockConflictException lx => Results.Conflict(new
        {
            ok = false,
            error = lx.Message,
            code = "lock_conflict",
            resource = lx.Resource,
            ownerUserId = lx.OwnerUserId,
            expiresAt = lx.ExpiresAt,
            job = jobService.GetSnapshot(),
        }),
        CapacityRejectedException cx => Results.Conflict(new
        {
            ok = false,
            error = cx.Message,
            code = "capacity",
            job = jobService.GetSnapshot(),
        }),
        _ => Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() }),
    };


    // immutable=true is only correct for files that can never change at the same URL (e.g. book
    // page images extracted once at import). Character ref/variant/book-ref images are overwritten
    // in place at the same path on regeneration — "public, max-age=31536000, immutable" would tell
    // the browser to never even ask the server again, silently showing a stale portrait for up to a
    // year after regeneration. Those use "no-cache" instead: still ETag/304-validated (saves the body
    // transfer when unchanged), but always revalidated so a regeneration is picked up immediately.
    public static IResult ServeCachedFile(
        HttpContext ctx, string path, string? contentType = null, bool enableRangeProcessing = false, bool immutable = false)
    {
        try
        {
            if (!File.Exists(path))
                return Results.NotFound(new { ok = false, error = "File not found" });
            var lastWrite = File.GetLastWriteTimeUtc(path);
            var etag = $"\"{lastWrite.Ticks:x}\"";
            if (ctx.Request.Headers.IfNoneMatch == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = immutable
                ? "public, max-age=31536000, immutable"
                : "no-cache";
            return Results.File(path, contentType ?? GuessImageContentType(path), enableRangeProcessing: enableRangeProcessing);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message });
        }
    }

    public static string GuessImageContentType(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? SpecializedMimeType.ImagePng.ToMimeTypeString()
        : path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
          path.EndsWith(ApiText.JpegExtension, StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
        : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
        : SpecializedMimeType.ApplicationOctetStream.ToMimeTypeString();

    public static object DemoPublicDto(
        DemoCatalogService.DemoEntry d,
        int upvoteCount = 0,
        bool upvotedByMe = false,
        bool canFork = false,
        string visibilityMode = "Private") => new
    {
        d.Id,
        d.Title,
        d.Description,
        d.ProjectId,
        d.CreatedBy,
        d.CreatedAt,
        d.SizeBytes,
        d.Status,
        d.ReportCount,
        upvoteCount,
        upvotedByMe,
        // True when this public film's studio project still exists (gallery Fork button).
        canFork,
        // YouTube is gallery playback SoT. Local videoPath only for staging (owner) before upload finishes.
        videoPath = string.IsNullOrWhiteSpace(d.YoutubeId)
            ? $"/api/demos/{Uri.EscapeDataString(d.Id)}/video"
            : null,
        d.YoutubeId,
        d.YoutubeUrl,
        d.Category,
        d.Tags,
        youtubeWatchUrl = string.IsNullOrWhiteSpace(d.YoutubeId)
            ? null
            : (string.IsNullOrWhiteSpace(d.YoutubeUrl) ? $"https://www.youtube.com/watch?v={d.YoutubeId}" : d.YoutubeUrl),
        d.YoutubeLikeCount,
        d.YoutubeViewCount,
        visibilityMode,
    };

    public static object DemoAdminDto(DemoCatalogService.DemoEntry d) => new
    {
        d.Id,
        d.Title,
        d.Description,
        d.ProjectId,
        d.CreatedBy,
        d.CreatedAt,
        d.SizeBytes,
        d.Status,
        d.AcceptedGuidelines,
        d.ReportCount,
        d.ReportNotes,
        d.ReviewedBy,
        d.ReviewedAt,
        d.ReviewNote,
        videoPath = $"/api/demos/{Uri.EscapeDataString(d.Id)}/video",
        d.YoutubeId,
        d.YoutubeUrl,
        d.YoutubeUploadStatus,
        d.YoutubeUploadError,
        d.MadeForKids,
        d.IsAiSyntheticContent,
        d.PrivacyStatus,
    };

}
