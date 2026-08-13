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

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", GetHealth);
        app.MapGet("/api/capacity", GetCapacity);
        return app;
    }

    private static async Task<IResult> GetHealth(ProjectStore store, IOptions<PageToMovieOptions> opts, IUserContext user, IUserApiKeyProvider keyProvider)
    {
    var hasKey = await keyProvider.HasKeyAsync(user.UserId);
    return Results.Ok(new
    {
        ok = true,
        service = "PageToMovie.Api",
        workspace = store.WorkspaceRoot,
        activeProject = store.ActiveProjectId,
        useFakes = opts.Value.UseFakes || ApiRuntime.UseFakes,
        enableReadCaches = store.ReadCachesEnabled,
        capacity = opts.Value.Capacity,
        xaiConfigured = hasKey || (opts.Value.AllowServerApiKeyFallback && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"))) || ApiRuntime.UseFakes,
        xaiKeyPresent = hasKey || (opts.Value.AllowServerApiKeyFallback && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"))),
        userId = user.UserId,
        isAdmin = user.IsAdmin,
    });
}

    private static IResult GetCapacity(FilmJobService jobService, IOptions<PageToMovieOptions> opts)
    {
    var cap = opts.Value.Capacity ?? new CapacityOptions();
    // Use O(1) counters — do not scan job list on this hot browse path
    var runningCount = jobService.RunningCount;
    return Results.Ok(new
    {
        ok = true,
        capacity = cap,
        running = runningCount > 0,
        runningCount,
        useFakes = opts.Value.UseFakes || ApiRuntime.UseFakes,
    });
}
}
