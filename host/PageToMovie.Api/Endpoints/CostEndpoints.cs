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

public static class CostEndpoints
{
    public static IEndpointRouteBuilder MapCostEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{id}/cost", GetProjectsIdCost);
        // <summary>
        // H3 — optional one-click reason after user regen (dialogue / look / motion / audio / other).
        // Never blocks gen if this fails.
        // </summary>
        app.MapPost("/api/projects/{id}/cost/take-reason", PostProjectsIdCostTakeReason);
        // <summary>
        // Actual spend by provider for this project.
        // Default: <b>signed-in user's</b> spend on this project. Pass <c>?all=true</c> (admin) for every user.
        // </summary>
        app.MapGet("/api/projects/{id}/cost/by-provider", GetProjectsIdCostByProvider);
        app.MapPost("/api/projects/{id}/cost/backfill", PostProjectsIdCostBackfill);
        app.MapGet("/api/projects/{id}/costs/summary", GetProjectsIdCostsSummary);
        app.MapPost("/api/projects/{id}/costs/record", PostProjectsIdCostsRecord);
        return app;
    }

    private static async Task<IResult> GetProjectsIdCost(string id,
    ProjectStore store,
    CostReportService costs,
    string? draftResolution,
    string? heroResolution,
    double? assumeAvgRetries,
    CancellationToken ct)
    {
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        var report = await costs.GetReportAsync(id, draftResolution, heroResolution, assumeAvgRetries, ct: ct);
        return Results.Ok(new { ok = true, projectId = id, cost = report });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCostTakeReason(string id,
    TakeReasonBody body,
    ProjectStore store,
    CostReportService costs,
    CancellationToken ct)
    {
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        if (body.Scene <= 0 || body.Clip <= 0)
            return Results.BadRequest(new { ok = false, error = "scene and clip required" });
        var ok = await costs.SetTakeReasonAsync(id, body.Scene, body.Clip, body.Reason ?? "", body.TakeIndex, ct);
        return Results.Ok(new { ok, projectId = id, scene = body.Scene, clip = body.Clip, reason = body.Reason });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdCostByProvider(string id,
    ProjectStore store,
    UserDatabaseService userDb,
    IUserContext user,
    bool? all,
    CancellationToken ct)
    {
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        var allUsers = all == true && user.IsAdmin;
        var userId = allUsers ? null : (string.IsNullOrWhiteSpace(user.UserId) ? null : user.UserId);
        var stats = await userDb.GetApiCostByProviderAsync(userId: userId, projectId: id, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            userId = userId,
            scope = allUsers ? "all_users" : "current_user",
            notes = "List vs charge: list_usd = vendor catalog; charge = list × admin multiplier. Grouped by provider (xAI, Google, ElevenLabs, …).",
            stats,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCostBackfill(string id, ProjectStore store, CostReportService costs, CancellationToken ct)
    {
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        var result = await costs.BackfillFromDiskAsync(id, onlyMissing: true, ct);
        return Results.Ok(new { ok = true, projectId = id, backfill = result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdCostsSummary(string id,
    CostLedgerService ledger,
    ProjectStore store,
    CancellationToken ct)
    {
    try
    {
        // Same root convention as ProjectStore itself (WorkspaceRoot/projects) — ContentRootPath
        // would point at the wrong directory whenever PageToMovie__WorkspaceRoot differs from the
        // app's own content root (fakes-mode tests, /data mount in production).
        var root = Path.Combine(store.WorkspaceRoot, ApiText.ProjectsFolder);
        var summary = await ProjectCostAggregator.BuildSummaryAsync(id, root, ledger, ct);
        return Results.Ok(summary);
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCostsRecord(string id,
    CostLedgerService ledger,
    HttpRequest req,
    CancellationToken ct)
    {
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var category = root.TryGetProperty("category", out var c) ? c.GetString() ?? ApiText.VideoFolder : ApiText.VideoFolder;
        var usd = root.TryGetProperty("usd", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        var note = root.TryGetProperty("note", out var n) ? n.GetString() : null;
        var modelId = root.TryGetProperty("modelId", out var m) ? m.GetString() : null;
        ledger.Record(id, category, usd, note, modelId);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}
}
