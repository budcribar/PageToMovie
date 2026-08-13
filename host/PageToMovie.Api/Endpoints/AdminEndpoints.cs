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

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Live admin state (Phase C metrics + locks + jobs)
        app.MapGet("/api/admin/state", GetAdminState);
        app.MapGet("/api/locks", GetLocks);
        // LoadSim live telemetry (no admin auth — sim posts from CLI)
        app.MapPost("/api/loadsim/progress", PostLoadsimProgress);
        app.MapGet("/api/admin/loadsim", GetAdminLoadsim);
        // <summary>
        // Admin: book text registry + adaptation_conversion artifacts + xAI provider file_id handles.
        // </summary>
        app.MapGet("/api/admin/book-cache", GetAdminBookCache);
        app.MapGet("/api/admin/learning/insights", GetAdminLearningInsights);
        app.MapGet("/api/admin/learning/events", GetAdminLearningEvents);
        app.MapGet("/api/admin/learning/review-comparison", GetAdminLearningReviewComparison);
        app.MapPost("/api/admin/learning/synthesize-prompt-improvements", PostAdminLearningSynthesizePromptImprovements);
        app.MapPost("/api/admin/learning/propose", PostAdminLearningPropose);
        app.MapGet("/api/admin/learning/proposal-checklist", GetAdminLearningProposalChecklist);
        app.MapPost("/api/admin/learning/proposal-checklist", PostAdminLearningProposalChecklist);
        app.MapPost("/api/admin/learning/proposal-checklist/toggle", PostAdminLearningProposalChecklistToggle);
        // <summary>Mark checklist items done when matching project-rule text is approved.</summary>
        app.MapPost("/api/admin/learning/proposal-checklist/accept-matching", PostAdminLearningProposalChecklistAcceptMatching);
        app.MapGet("/api/admin/learning/project-rules/{projectId}", GetAdminLearningProjectRulesProjectId);
        app.MapPost("/api/admin/learning/project-rules/{projectId}/suggest", PostAdminLearningProjectRulesProjectIdSuggest);
        app.MapPost("/api/admin/learning/project-rules/{projectId}/approve", PostAdminLearningProjectRulesProjectIdApprove);
        app.MapPost("/api/admin/learning/project-rules/{projectId}/reject", PostAdminLearningProjectRulesProjectIdReject);
        // Users & credits overview (admin)
        app.MapGet("/api/admin/users", GetAdminUsers);
        // <summary>Admin: download full project folder as zip for local debug.</summary>
        app.MapGet("/api/admin/projects/{id}/export", GetAdminProjectsIdExport);
        // <summary>Admin: Download all server diagnostic logs (jobs, edit logs, prompts, system info) as a zip archive.</summary>
        app.MapGet("/api/admin/logs/export", GetAdminLogsExport);
        // <summary>Admin: Get JSON summary of server diagnostic state and active job logs.</summary>
        app.MapGet("/api/admin/logs", GetAdminLogs);
        app.MapGet("/api/admin/timing-telemetry/trend", GetAdminTimingTelemetryTrend);
        app.MapPost("/api/admin/timing-telemetry/seed", PostAdminTimingTelemetrySeed);
        // <summary>Admin: recent generation_errors rows (partial-coverage / structural-gate / transient-retry events).</summary>
        app.MapGet("/api/admin/generation-errors", GetAdminGenerationErrors);
        // <summary>Aggregated AI/model-call telemetry (user_api_calls table) for the admin AI-Calls analytics page.</summary>
        app.MapGet("/api/admin/ai-calls", GetAdminAiCalls);
        // <summary>
        // Admin: import a project zip (full folder). Multipart field <c>file</c>;
        // optional form fields <c>projectId</c>, <c>overwrite</c>=true|false.
        // </summary>
        app.MapPost("/api/admin/projects/import", PostAdminProjectsImport);
        app.MapPost("/api/admin/users/credits", PostAdminUsersCredits);
        // <summary>Admin: set a user's password (forgot-password completion or support).</summary>
        app.MapPost("/api/admin/users/set-password", PostAdminUsersSetPassword);
        // <summary>Admin: disable or re-enable a user account.</summary>
        app.MapPost("/api/admin/users/disabled", PostAdminUsersDisabled);
        // <summary>
        // Admin hard-delete: requires typing the target username + the acting admin's password
        // (or operator override secret). Cascades credit ledger, demos, and owned projects.
        // </summary>
        app.MapPost("/api/admin/users/delete", PostAdminUsersDelete);
        app.MapGet("/api/admin/config", GetAdminConfig);
        app.MapPut("/api/admin/config", PutAdminConfig);
        app.MapGet("/api/admin/models-catalog", GetAdminModelsCatalog);
        app.MapPut("/api/admin/models-catalog", PutAdminModelsCatalog);
        app.MapPost("/api/admin/models-catalog/reload", PostAdminModelsCatalogReload);
        app.MapPost("/api/admin/models-catalog/validate", PostAdminModelsCatalogValidate);
        app.MapPost("/api/admin/models-catalog/check-updates", PostAdminModelsCatalogCheckUpdates);
        app.MapPost("/api/admin/chat-cache/clear", PostAdminChatCacheClear);
        app.MapPost("/api/admin/test-email", PostAdminTestEmail);
        app.MapPost("/api/admin/jobs/{jobId}/cancel", PostAdminJobsJobIdCancel);
        app.MapPost("/api/admin/locks/release", PostAdminLocksRelease);
        // <summary>H4/H7/H8 — takes-per-clip telemetry (global aggregates never include other users' project ids).</summary>
        app.MapGet("/api/admin/takes-telemetry", GetAdminTakesTelemetry);
        // <summary>Admin list of demos (reports/removed). Content approval queue is retired — YouTube is the gate.</summary>
        app.MapGet("/api/admin/demos", GetAdminDemos);
        // <summary>
        // Admin: register an existing YouTube video on the public gallery (no local MP4 upload).
        // Body: { youtubeIdOrUrl, title, description?, projectId? }
        // </summary>
        app.MapPost("/api/admin/demos/from-youtube", PostAdminDemosFromYoutube);
        // <summary>Admin: pull every upload from the connected YouTube channel into the public gallery catalog.</summary>
        app.MapPost("/api/admin/demos/sync-youtube", PostAdminDemosSyncYoutube);
        // <summary>Admin: approve / reject / re-queue a demo (no AI).</summary>
        app.MapPost("/api/admin/demos/{demoId}/review", PostAdminDemosDemoIdReview);
        return app;
    }

    private static IResult GetAdminState(IUserContext user,
    ProjectStore store,
    AdminMetricsPushService metricsPush,
    HttpRequestMetrics httpMetrics,
    LoadSimLiveStore loadSimStore,
    ProcessHistoryStore processHistory,
    VolumeDiskTelemetryService diskTelemetry)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    diskTelemetry.RecordDailySnapshotIfNeeded();
    var disk = diskTelemetry.GetDiskStatus();
    var diskHistory = diskTelemetry.GetDiskHistory(30);

    var snap = metricsPush.BuildSnapshot();
    var traffic = httpMetrics.Snapshot();
    // Ensure at least one memory sample even before background tick
    if (processHistory.GetHistory().Count == 0)
        processHistory.Sample();
    return Results.Ok(new
    {
        ok = true,
        state = snap,
        projects = new
        {
            active = store.ActiveProjectId,
            workspace = store.WorkspaceRoot,
        },
        caller = new { userId = user.UserId, roles = user.Roles },
        disk,
        diskHistory,
        // Flatten common fields for Blazor DTO
        generatedAt = DateTimeOffset.UtcNow,
        process = snap.Process,
        capacity = snap.Capacity,
        jobs = new
        {
            running = snap.Jobs.Any(j =>
                string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase)),
            count = snap.Jobs.Count,
            items = snap.Jobs.Select(j => new
            {
                j.JobId,
                j.UserId,
                j.ProjectId,
                j.Kind,
                j.Scene,
                j.Clip,
                j.Status,
                j.Message,
                j.Index,
                j.Total,
                j.StartedAt,
                ageMs = j.StartedAt is DateTimeOffset s
                    ? (long)(DateTimeOffset.UtcNow - s).TotalMilliseconds
                    : (long?)null,
            }),
        },
        locks = snap.Locks,
        queueByUser = snap.QueueByUser,
        timings = snap.TimingsByKind,
        apiInFlight = snap.ApiInFlight,
        capacityRejects = snap.CapacityRejects,
        lockConflicts = snap.LockConflicts,
        http = traffic,
        loadSim = loadSimStore.GetState(),
        processHistory = processHistory.GetHistory(),
    });
}

    private static IResult GetLocks(ILockService locks, IUserContext user)
    {
    var list = locks.ListActive();
    return Results.Ok(new { ok = true, locks = list, userId = user.UserId });
}

    private static IResult PostLoadsimProgress(LoadSimProgressDto body, LoadSimLiveStore store)
    {
    if (body is null)
        return Results.BadRequest(new { ok = false, error = "body required" });
    store.Publish(body);
    return Results.Accepted("/api/admin/loadsim", new { ok = true, runId = body.RunId, status = body.Status });
}

    private static IResult GetAdminLoadsim(IUserContext user, LoadSimLiveStore store)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    var state = store.GetState();
    return Results.Ok(new { ok = true, loadSim = state });
}

    private static async Task<IResult> GetAdminBookCache(IUserContext user,
    BookTextRegistryService books,
    int? take,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    var snap = await books.GetAdminCacheSnapshotAsync(take ?? 100, ct).ConfigureAwait(false);
    return Results.Ok(new
    {
        ok = true,
        bookCount = snap.BookCount,
        artifactCount = snap.ArtifactCount,
        providerFileCount = snap.ProviderFileCount,
        totalBookBytes = snap.TotalBookBytes,
        books = snap.Books.Select(b => new
        {
            bookId = b.BookId,
            sha256 = b.Sha256,
            bookTitle = b.BookTitle,
            projects = b.Projects,
            byteCount = b.ByteCount,
            createdAt = b.CreatedAt,
            artifactCount = b.ArtifactCount,
            accessLinkCount = b.AccessLinkCount,
            provider = b.Provider,
            providerFileId = b.ProviderFileId,
            fileExpiresAtUnix = b.FileExpiresAtUnix,
            lastResponseId = b.LastResponseId,
            providerFileUpdatedAt = b.ProviderFileUpdatedAt,
        }),
        recentArtifacts = snap.RecentArtifacts.Select(a => new
        {
            artifactId = a.ArtifactId,
            bookId = a.BookId,
            artifactKind = a.ArtifactKind,
            modelId = a.ModelId,
            promptVersion = a.PromptVersion,
            temperature = a.Temperature,
            createdAt = a.CreatedAt,
            contentBytes = a.ContentBytes,
        }),
    });
}

    private static async Task<IResult> GetAdminLearningInsights(IUserContext user,
    ReviewEventStore learning,
    string? projectId,
    int? take,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    var insights = await learning.BuildInsightsAsync(projectId, recentTake: take ?? 40, ct: ct);
    return Results.Ok(new { ok = true, insights });
}

    private static async Task<IResult> GetAdminLearningEvents(IUserContext user,
    ReviewEventStore learning,
    string? projectId,
    string? type,
    string? category,
    int? take,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    var events = await learning.QueryAsync(projectId, type, category, take: take ?? 100, ct: ct);
    return Results.Ok(new { ok = true, events });
}

    private static async Task<IResult> GetAdminLearningReviewComparison(IUserContext user,
    ReviewEventStore learning,
    string? projectId,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    var comparison = await learning.GetReviewComparisonAsync(projectId, ct: ct);
    return Results.Ok(comparison);
}

    private static async Task<IResult> PostAdminLearningSynthesizePromptImprovements(IUserContext user,
    LearningProposalService proposals,
    string? projectId,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    var result = await proposals.SynthesizePromptImprovementsAsync(projectId, ct);
    return Results.Ok(result);
}

    private static async Task<IResult> PostAdminLearningPropose(ProposeLearningRulesRequest body,
    IUserContext user,
    LearningProposalService proposals,
    ProposalChecklistService checklist,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    var result = await proposals.ProposeAsync(body, ct);
    if (result.Ok && !string.IsNullOrWhiteSpace(result.Proposal))
    {
        try
        {
            var list = checklist.IngestProposal(
                result.Proposal,
                sourceLabel: $"propose_fails_n{body.LastNFails}");
            return Results.Ok(new
            {
                result.Ok,
                result.Proposal,
                result.FailEventsUsed,
                result.Categories,
                result.Error,
                checklist = list,
            });
        }
        catch { /* still return proposal */ }
    }
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
}

    private static IResult GetAdminLearningProposalChecklist(IUserContext user,
    ProposalChecklistService checklist)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(new { ok = true, checklist = checklist.Load() });
}

    private static IResult PostAdminLearningProposalChecklist(ProposalChecklistUpsertRequest body,
    IUserContext user,
    ProposalChecklistService checklist)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = checklist.Upsert(body ?? new ProposalChecklistUpsertRequest());
        return Results.Ok(new { ok = true, checklist = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult PostAdminLearningProposalChecklistToggle(ProposalChecklistToggleRequest body,
    IUserContext user,
    ProposalChecklistService checklist)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = checklist.Toggle(body ?? new ProposalChecklistToggleRequest());
        return Results.Ok(new { ok = true, checklist = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult PostAdminLearningProposalChecklistAcceptMatching(ProposalChecklistAcceptMatchingRequest body,
    IUserContext user,
    ProposalChecklistService checklist)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = checklist.MarkAcceptedFromRuleTexts(
            body?.Texts ?? new List<string>(),
            body?.Disposition ?? "accepted",
            body?.Note);
        return Results.Ok(new { ok = true, checklist = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetAdminLearningProjectRulesProjectId(string projectId,
    IUserContext user,
    ProjectRulesService rules,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(new { ok = true, projectId, rules = await rules.LoadAsync(projectId, ct) });
}

    private static async Task<IResult> PostAdminLearningProjectRulesProjectIdSuggest(string projectId,
    IUserContext user,
    ProjectRulesService rules,
    int? minFails,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = await rules.SuggestFromFailsAsync(projectId, minFails ?? ProjectRulesService.DefaultMinFailsForSuggest, ct);
        return Results.Ok(new { ok = true, projectId, rules = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostAdminLearningProjectRulesProjectIdApprove(string projectId,
    ApproveProjectRuleRequest body,
    IUserContext user,
    ProjectRulesService rules,
    ProposalChecklistService checklist,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        // Capture text before approve (suggestion removed from pending)
        var before = await rules.LoadAsync(projectId, ct);
        var sug = before.Pending.FirstOrDefault(p =>
            string.Equals(p.Id, body.SuggestionId, StringComparison.OrdinalIgnoreCase));
        var approvedText = !string.IsNullOrWhiteSpace(body.Text)
            ? body.Text.Trim()
            : (sug?.Text ?? "").Trim();

        var doc = await rules.ApproveAsync(projectId, body.SuggestionId, body.Text, user.UserId, ct);

        // Keep admin checklist in sync (theme match) so Propose doesn't look "reset"
        ProposalChecklistDocument? checklistDoc = null;
        if (!string.IsNullOrWhiteSpace(approvedText))
        {
            try
            {
                checklistDoc = checklist.MarkAcceptedFromRuleTexts(
                    new[] { approvedText },
                    disposition: "accepted",
                    note: $"Approved project rule on {projectId}");
            }
            catch
            {
                /* non-fatal */
            }
        }

        return Results.Ok(new { ok = true, projectId, rules = doc, checklist = checklistDoc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostAdminLearningProjectRulesProjectIdReject(string projectId,
    RejectProjectRuleRequest body,
    IUserContext user,
    ProjectRulesService rules,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = await rules.RejectAsync(projectId, body.SuggestionId, ct);
        return Results.Ok(new { ok = true, projectId, rules = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetAdminUsers(IUserContext user, CreditService credits)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    var overview = await credits.GetAdminOverviewAsync(recentLedger: 50);
    return Results.Ok(new { ok = true, overview });
}

    private static async Task<IResult> GetAdminProjectsIdExport(string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    HttpContext http,
    ProjectArchiveService archives,
    CancellationToken ct)
    {
    if (ApiEndpointHelpers.RequireAdminOrOperator(http, user, opts) is { } forbidden)
        return forbidden;
    try
    {
        var exp = await archives.ExportAsync(id, ct);
        return Results.File(
            exp.Stream,
            exp.ContentType,
            exp.FileName,
            enableRangeProcessing: false);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetAdminLogsExport(IUserContext user,
    IOptions<PageToMovieOptions> opts,
    HttpContext http,
    ServerLogExportService logExporter,
    CancellationToken ct)
    {
    if (ApiEndpointHelpers.RequireAdminOrOperator(http, user, opts) is { } forbidden)
        return forbidden;

    try
    {
        var bytes = await logExporter.ExportLogsZipAsync(ct);
        var fileName = $"pagetomovie-server-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
        return Results.File(bytes, "application/zip", fileName);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetAdminLogs(IUserContext user,
    IOptions<PageToMovieOptions> opts,
    HttpContext http,
    FilmJobService jobs,
    ProjectStore projects,
    CancellationToken ct)
    {
    if (ApiEndpointHelpers.RequireAdminOrOperator(http, user, opts) is { } forbidden)
        return forbidden;

    var projectList = await projects.ListProjectsAsync(ct);

    return Results.Ok(new
    {
        ok = true,
        exportUrl = "/api/admin/logs/export",
        system = new
        {
            machineName = Environment.MachineName,
            osVersion = Environment.OSVersion.ToString(),
            activeProject = projects.ActiveProjectId,
            utcTime = DateTimeOffset.UtcNow,
        },
        jobs = jobs.ListJobs(take: 50),
        projects = projectList.Select(p => p.Id),
    });
}

    private static async Task<IResult> GetAdminTimingTelemetryTrend(IUserContext user,
    GlobalTimingCalibrationService calibration)
    {
    var stats = await calibration.GetStatsAsync();
    var trend = await calibration.GetTrendAsync(maxPoints: 30);
    return Results.Ok(new
    {
        ok = true,
        stats,
        trend
    });
}

    private static async Task<IResult> PostAdminTimingTelemetrySeed(IUserContext user,
    GlobalTimingCalibrationService calibration)
    {
    int count = await calibration.SeedDefaultBenchmarksAsync();
    return Results.Ok(new
    {
        ok = true,
        message = $"Seeded {count} empirical benchmark entries into SQLite database.",
        count
    });
}

    private static async Task<IResult> GetAdminGenerationErrors(IUserContext user,
    UserDatabaseService userDb,
    string? errorType,
    string? projectId,
    int? take,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    var rows = await userDb.ListGenerationErrorsAsync(errorType, projectId, take ?? 100, ct);
    return Results.Ok(new { ok = true, rows });
}

    private static async Task<IResult> GetAdminAiCalls(IUserContext user, AiCallAnalyticsService analytics, int? maxRows, CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var data = await analytics.BuildAsync(Math.Clamp(maxRows ?? 4000, 100, 20000), AnalyticsWindow.All, ct);
        return Results.Ok(new { ok = true, data });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
}

    private static async Task<IResult> PostAdminProjectsImport(HttpRequest req,
    IUserContext user,
    ProjectArchiveService archives,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    if (!req.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form with file required" });

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "file required (project zip)" });

    var preferredId = form[ApiText.ProjectIdKey].ToString();
    if (string.IsNullOrWhiteSpace(preferredId))
        preferredId = form["id"].ToString();

    var targetUserId = form["targetUserId"].ToString();
    if (string.IsNullOrWhiteSpace(targetUserId))
        targetUserId = form["userId"].ToString();
    if (string.IsNullOrWhiteSpace(targetUserId))
        targetUserId = form["ownerUserId"].ToString();

    var overwrite = string.Equals(form[ApiText.OverwriteKey].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(form[ApiText.OverwriteKey].ToString(), "1", StringComparison.OrdinalIgnoreCase);

    try
    {
        await using var stream = file.OpenReadStream();
        var result = await archives.ImportAsync(
            stream,
            preferredId: string.IsNullOrWhiteSpace(preferredId) ? null : preferredId.Trim(),
            overwrite: overwrite,
            targetUserId: string.IsNullOrWhiteSpace(targetUserId) ? null : targetUserId.Trim(),
            ct: ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = result.ProjectId,
            active = result.Project,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostAdminUsersCredits(AdminGrantCreditsRequest body,
    IUserContext user,
    CreditService credits)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = ApiText.UserIdRequired });
    if (Math.Abs(body.AmountUsd) < 0.0001)
        return Results.BadRequest(new { ok = false, error = "amountUsd must be non-zero" });

    var summary = await credits.GrantAsync(body.UserId.Trim(), body.AmountUsd, body.Note);
    if (summary is null)
        return Results.NotFound(new { ok = false, error = ApiText.UserNotFound });

    return Results.Ok(new { ok = true, user = summary });
}

    private static async Task<IResult> PostAdminUsersSetPassword(AdminSetUserPasswordRequest body,
    IUserContext user,
    UserDatabaseService userDb,
    IAdminAuthService auth)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = ApiText.UserIdRequired });
    if (string.IsNullOrWhiteSpace(body.NewPassword) || body.NewPassword.Length < 4)
        return Results.BadRequest(new { ok = false, error = "New password must be at least 4 characters." });
    if (!await auth.VerifyCallerPasswordAsync(user.UserId, body.AdminPassword ?? ""))
        return Results.Json(new { ok = false, error = "Admin password is incorrect." },
            statusCode: StatusCodes.Status403Forbidden);

    var target = await userDb.ResolveUserAsync(body.UserId.Trim());
    if (target is null)
        return Results.NotFound(new { ok = false, error = ApiText.UserNotFound });

    var ok = await userDb.SetPasswordAsync(target.UserId, body.NewPassword);
    if (!ok)
        return Results.BadRequest(new { ok = false, error = "Could not update password." });

    return Results.Ok(new
    {
        ok = true,
        userId = target.UserId,
        username = target.Username,
        message = $"Password updated for {target.Username}.",
    });
}

    private static async Task<IResult> PostAdminUsersDisabled(AdminSetUserDisabledRequest body,
    IUserContext user,
    UserDatabaseService userDb)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = ApiText.UserIdRequired });

    var target = await userDb.ResolveUserAsync(body.UserId.Trim());
    if (target is null)
        return Results.NotFound(new { ok = false, error = ApiText.UserNotFound });

    if (string.Equals(target.UserId, user.UserId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Username, user.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { ok = false, error = "You cannot disable your own account." });

    if (body.Disabled &&
        string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase))
    {
        var activeAdmins = await userDb.CountActiveAdminsAsync();
        // If this admin is currently active, disabling them must leave ≥1 admin.
        if (!target.IsDisabled && activeAdmins <= 1)
            return Results.BadRequest(new { ok = false, error = "Cannot disable the last active admin." });
    }

    var summary = await userDb.SetUserDisabledAsync(target.UserId, body.Disabled);
    if (summary is null)
        return Results.NotFound(new { ok = false, error = ApiText.UserNotFound });

    return Results.Ok(new
    {
        ok = true,
        user = summary,
        message = body.Disabled
            ? $"Disabled {summary.Username}."
            : $"Re-enabled {summary.Username}.",
    });
}

    private static async Task<IResult> PostAdminUsersDelete(AdminDeleteUserRequest body,
    IUserContext user,
    IAdminAuthService auth,
    UserDatabaseService userDb,
    ProjectStore projects,
    DemoCatalogService demos,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = ApiText.UserIdRequired });
    if (string.IsNullOrWhiteSpace(body.ConfirmUsername))
        return Results.BadRequest(new { ok = false, error = "confirmUsername is required" });
    if (string.IsNullOrEmpty(body.AdminPassword))
        return Results.BadRequest(new { ok = false, error = "adminPassword is required" });

    if (!await auth.VerifyCallerPasswordAsync(user.UserId, body.AdminPassword, ct))
        return Results.Json(new { ok = false, error = "Admin password is incorrect." },
            statusCode: StatusCodes.Status403Forbidden);

    var target = await userDb.ResolveUserAsync(body.UserId.Trim(), ct);
    if (target is null)
        return Results.NotFound(new { ok = false, error = ApiText.UserNotFound });

    if (!string.Equals(body.ConfirmUsername.Trim(), target.Username, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(body.ConfirmUsername.Trim(), target.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new
        {
            ok = false,
            error = "confirmUsername must match the target username exactly.",
        });

    if (string.Equals(target.UserId, user.UserId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Username, user.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { ok = false, error = "You cannot delete your own account." });

    if (string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase))
    {
        var activeAdmins = await userDb.CountActiveAdminsAsync(ct);
        var countsAsActive = !target.IsDisabled;
        if (countsAsActive && activeAdmins <= 1)
            return Results.BadRequest(new { ok = false, error = "Cannot delete the last active admin." });
    }

    var deletedProjects = 0;
    var projectErrors = new List<string>();
    if (body.DeleteOwnedProjects)
    {
        var all = await projects.ListProjectsAsync(ct);
        var owned = all.Where(p =>
            !string.IsNullOrWhiteSpace(p.OwnerUserId) &&
            (string.Equals(p.OwnerUserId, target.UserId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.OwnerUserId, target.Username, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var id in owned.Select(p => p.Id))
        {
            try
            {
                await projects.DeleteProjectAsync(id, ct);
                deletedProjects++;
            }
            catch (Exception ex)
            {
                projectErrors.Add($"{id}: {ex.Message}");
            }
        }
    }

    var deletedDemos = await demos.HardDeleteAllByUserAsync(target.UserId, ct);
    // Also match demos stored under username if different from user_id.
    if (!string.Equals(target.UserId, target.Username, StringComparison.OrdinalIgnoreCase))
        deletedDemos += await demos.HardDeleteAllByUserAsync(target.Username, ct);

    var removed = await userDb.HardDeleteUserAsync(target.UserId, ct);
    if (!removed)
        return Results.NotFound(new { ok = false, error = "user not found or already deleted" });

    return Results.Ok(new
    {
        ok = true,
        userId = target.UserId,
        username = target.Username,
        deletedProjects,
        deletedDemos,
        projectErrors = projectErrors.Count > 0 ? projectErrors : null,
        message = $"Deleted {target.Username} (projects: {deletedProjects}, demos: {deletedDemos}).",
    });
}

    private static IResult GetAdminConfig(IUserContext user, IRuntimeConfigStore config)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(config.Get());
}

    private static async Task<IResult> PutAdminConfig(RuntimeConfigUpdateRequest body,
    IUserContext user,
    IRuntimeConfigStore config,
    IHubContext<JobHub> hub,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var updated = await config.UpdateAsync(body, user.UserId, ct);
        _ = hub.Clients.Group(JobHub.AdminOpsGroup)
            .SendAsync(JobHubEvents.AdminState, new { configChanged = true, config = updated }, ct);
        return Results.Ok(updated);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetAdminModelsCatalog(IUserContext user)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired }, statusCode: StatusCodes.Status403Forbidden);

    return Results.Ok(new
    {
        ok = true,
        catalogPath = SupportedModelCatalog.GetCatalogSourceLabel(),
        rawJson = SupportedModelCatalog.GetEmbeddedCatalogJson(),
        editable = false,
        models = SupportedModelCatalog.Entries,
        capabilities = SupportedModelCatalog.RegisteredCapabilities,
        taskRankings = SupportedModelCatalog.TaskRankings,
    });
}

    private static IResult PutAdminModelsCatalog(IUserContext user)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired }, statusCode: StatusCodes.Status403Forbidden);

    // The catalog is the single source of truth, embedded at build time. Runtime edits are gone:
    // change PageToMovie.Core/config/models_catalog.json in git and redeploy.
    return Results.Json(new
    {
        ok = false,
        error = "The models catalog is embedded at build time and cannot be edited at runtime. " +
                "Edit PageToMovie.Core/config/models_catalog.json in git and redeploy.",
    }, statusCode: StatusCodes.Status405MethodNotAllowed);
}

    private static IResult PostAdminModelsCatalogReload(IUserContext user)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired }, statusCode: StatusCodes.Status403Forbidden);

    SupportedModelCatalog.ReloadCatalog();
    return Results.Ok(new
    {
        ok = true,
        message = "Models catalog reloaded successfully.",
        modelsCount = SupportedModelCatalog.Entries.Count,
    });
}

    private static async Task<IResult> PostAdminModelsCatalogValidate(HttpContext http, IUserContext user)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired }, statusCode: StatusCodes.Status403Forbidden);

    using var reader = new StreamReader(http.Request.Body);
    var rawJson = await reader.ReadToEndAsync(http.RequestAborted);
    try
    {
        if (!SupportedModelCatalog.TryLoadFromJson(rawJson))
            return Results.BadRequest(new { ok = false, error = "Invalid catalog JSON" });
        var errors = SupportedModelCatalog.ValidateEnabledModels();
        // Reload real on-disk catalog so in-memory state is not left on the draft payload
        SupportedModelCatalog.ReloadCatalog();
        return Results.Ok(new
        {
            ok = errors.Count == 0,
            errorCount = errors.Count,
            errors,
            message = errors.Count == 0
                ? "All enabled models have required fields."
                : $"{errors.Count} validation issue(s) — fix before save.",
        });
    }
    catch (Exception ex)
    {
        try { SupportedModelCatalog.ReloadCatalog(); } catch { /* best effort */ }
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostAdminModelsCatalogCheckUpdates(IUserContext user, CatalogUpdateProbeService probe, CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var result = await probe.ScanAsync(user.UserId, ct).ConfigureAwait(false);
        return Results.Ok(new { ok = true, result });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
}

    private static IResult PostAdminChatCacheClear(IUserContext user, IServiceProvider sp)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    // Not registered under PageToMovie:UseFakes (fakes never hit the network, so there's nothing
    // to cache) — report that plainly instead of a DI resolution error.
    var cache = sp.GetService<CachingChatClient>();
    if (cache is null)
        return Results.Ok(new { ok = true, filesRemoved = 0, note = "chat cache not active (fakes mode)" });
    var removed = cache.ClearCache();
    return Results.Ok(new { ok = true, filesRemoved = removed });
}

    private static async Task<IResult> PostAdminTestEmail(TestEmailRequest? body,
    IUserContext user,
    IEmailSender sender,
    IOptions<PageToMovieOptions> opts)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired }, statusCode: StatusCodes.Status403Forbidden);

    var to = (body?.ToEmail ?? "").Trim();
    if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
        return Results.BadRequest(new { ok = false, error = "Valid recipient email address (toEmail) is required." });

    var senderType = sender.GetType().Name;
    var resolvedKey = MailOptions.ResolveResendApiKey(opts.Value.Mail);
    var resendKeyResolved = !string.IsNullOrWhiteSpace(resolvedKey);

    var checkedEnvs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
    {
        var k = de.Key?.ToString();
        if (!string.IsNullOrWhiteSpace(k) && (k.StartsWith("Resend", StringComparison.OrdinalIgnoreCase) || k.Contains("Mail", StringComparison.OrdinalIgnoreCase)))
        {
            checkedEnvs[k] = !string.IsNullOrWhiteSpace(de.Value?.ToString());
        }
    }

    try
    {
        await sender.SendAsync(
            to,
            "PageToMovie Resend Test Email",
            $"<h1>PageToMovie Email Test</h1><p>This email was successfully sent via <strong>{senderType}</strong> on Railway.</p>",
            $"PageToMovie Email Test: Sent via {senderType} on Railway.");

        return Results.Ok(new
        {
            ok = true,
            message = $"Test email sent to {to} via {senderType}.",
            senderType,
            resendKeyResolved,
            checkedEnvs,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            ok = false,
            error = ex.Message,
            senderType,
            resendKeyResolved,
            checkedEnvs,
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
}

    private static async Task<IResult> PostAdminJobsJobIdCancel(string jobId, IUserContext user, FilmJobService jobService)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    await jobService.CancelAsync(jobId);
    return Results.Ok(new { ok = true, jobId, job = jobService.GetJob(jobId) });
}

    private static IResult PostAdminLocksRelease(AdminReleaseLockRequest body, IUserContext user, ILockService locks)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminRoleRequired },
            statusCode: StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(body.Resource))
        return Results.BadRequest(new { ok = false, error = "resource required" });
    var ok = locks.Release(body.Resource.Trim(), user.UserId, force: true);
    return Results.Ok(new { ok, resource = body.Resource, locks = locks.ListActive() });
}

    private static async Task<IResult> GetAdminTakesTelemetry(IUserContext user,
    UserDatabaseService userDb,
    string? projectId,
    CancellationToken ct)
    {
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "Admin required" }, statusCode: 403);
    try
    {
        var global = await userDb.GetTakesTelemetryStatsAsync(projectId: null, ct);
        TakesTelemetryStats? project = null;
        if (!string.IsNullOrWhiteSpace(projectId))
            project = await userDb.GetTakesTelemetryStatsAsync(projectId.Trim(), ct);
        return Results.Ok(new { ok = true, global, project });
    }
    catch (Exception ex)
    {
        // H9 fail-open
        return Results.Ok(new { ok = true, global = new TakesTelemetryStats { Notes = "unavailable: " + ex.Message }, project = (TakesTelemetryStats?)null });
    }
}

    private static async Task<IResult> GetAdminDemos(DemoCatalogService demos,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    string? status,
    int? take,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminOnly }, statusCode: StatusCodes.Status403Forbidden);

    var st = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
    var list = await demos.ListAsync(take ?? 100, st, ct);
    return Results.Ok(new
    {
        ok = true,
        status = st,
        demos = list.Select(ApiEndpointHelpers.DemoAdminDto),
        pendingCount = (await demos.ListAsync(200, DemoCatalogService.DemoStatuses.Pending, ct)).Count,
    });
}

    private static async Task<IResult> PostAdminDemosFromYoutube(DemoCatalogService demos,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    RegisterYouTubeDemoRequest? body,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminOnly }, statusCode: StatusCodes.Status403Forbidden);
    if (body is null || string.IsNullOrWhiteSpace(body.YoutubeIdOrUrl) || string.IsNullOrWhiteSpace(body.Title))
        return Results.BadRequest(new { ok = false, error = "youtubeIdOrUrl and title are required" });
    try
    {
        var entry = await demos.RegisterFromYouTubeAsync(
            body.YoutubeIdOrUrl,
            body.Title,
            body.Description,
            createdBy: user.UserId,
            projectId: body.ProjectId,
            ct: ct);
        return Results.Ok(new
        {
            ok = true,
            message = $"“{entry.Title}” is on the public gallery (YouTube).",
            demo = ApiEndpointHelpers.DemoAdminDto(entry),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostAdminDemosSyncYoutube(YouTubeChannelGallerySync channelSync,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminOnly }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var (added, updated, total, skipped) = await channelSync.EnsureSyncedAsync(
            force: true,
            createdBy: user.UserId,
            maxVideos: 100,
            ct: ct);
        return Results.Ok(new
        {
            ok = true,
            added,
            updated,
            total,
            skipped,
            message = total == 0 && skipped
                ? "Nothing to sync (channel not connected or empty)."
                : $"Synced {total} channel video(s): {added} new, {updated} updated.",
            lastError = channelSync.LastError,
            lastSuccessUtc = channelSync.LastSuccessUtc,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostAdminDemosDemoIdReview(string demoId,
    DemoReviewRequest? body,
    DemoCatalogService demos,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    DemoYouTubePublisherService youTubePublisher,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = ApiText.AdminOnly }, statusCode: StatusCodes.Status403Forbidden);

    var status = (body?.Status ?? "").Trim().ToLowerInvariant();
    if (status is not (
        DemoCatalogService.DemoStatuses.Public
        or DemoCatalogService.DemoStatuses.Rejected
        or DemoCatalogService.DemoStatuses.Pending
        or DemoCatalogService.DemoStatuses.Removed))
    {
        return Results.BadRequest(new
        {
            ok = false,
            error = "status must be public, rejected, pending, or removed",
        });
    }

    try
    {
        var d = await demos.SetStatusAsync(demoId, status, user.UserId, body?.Note, ct);
        if (d is null)
            return Results.NotFound(new { ok = false, error = ApiText.DemoNotFound });

        // Newly approved, or re-approved with a new local movie (V2 replace) → publish in the background.
        // Publisher no-ops when already on YouTube with no local movie.mp4.
        if (status == DemoCatalogService.DemoStatuses.Public)
            _ = Task.Run(() => youTubePublisher.PublishAsync(demoId, ct));

        return Results.Ok(new { ok = true, demo = ApiEndpointHelpers.DemoAdminDto(d) });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}
