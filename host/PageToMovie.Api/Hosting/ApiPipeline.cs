using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Api.Collaboration;
using PageToMovie.Api.Hubs;
using PageToMovie.Api.Services;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Web.Components;

namespace PageToMovie.Api;

internal static class ApiPipeline
{
    public static async Task UseFilmStudioPipelineAsync(this WebApplication app)
    {
        app.UseMiddleware<ProjectAccessMiddleware>();

        if (ApiRuntime.UseFakes)
            app.Logger.LogWarning("DEV: fakes mode — login bypass ENABLED (auto dev-user sign-in via /api/auth/dev-login; provider calls resolve to fakes)");

        // Cross-Origin Isolation headers required for SharedArrayBuffer (ffmpeg.wasm, WebAssembly threads).
        // Must be applied to every response, including the Blazor index.html and all static assets.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            await next();
        });

        var staticFileProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        staticFileProvider.Mappings[".wasm"] = "application/wasm";
        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = staticFileProvider
        });
        app.MapStaticAssets();
        app.UseAntiforgery();

        // Map Blazor UI (PageToMovie.Web WASM) — same origin as REST + SignalR.
        // App lives in PageToMovie.Web; do not AddAdditionalAssemblies for that assembly
        // (duplicate registration → "Assembly already defined" at startup).
        app.MapRazorComponents<App>()
            .AddInteractiveWebAssemblyRenderMode();

        app.UseCors();

        // Wire SignalR sink into job service
        var jobs = app.Services.GetRequiredService<FilmJobService>();
        jobs.SetProgressSink(app.Services.GetRequiredService<IJobProgressSink>());
        await SeedBundledDemosAsync(app);

        app.UseMiddleware<HttpRequestMetricsMiddleware>();
        app.UseMiddleware<JwtHeaderMiddleware>();
        UsePerRequestApiKeyScope(app);
        app.MapHub<JobHub>("/hubs/jobs");
    }

    public static async Task RunFilmStudioStartupAsync(this WebApplication app)
    {
        // ── One-Time Startup Migration: catch up every project's schema_version ───────────────────
        // ProjectMigrationService already versions each project via a "schema_version" field in
        // project.json (mirrors UserDatabaseService's PRAGMA user_version approach for the SQL DB) —
        // today it's only invoked from ProjectArchiveService's export/import paths, so a project that's
        // never been exported/imported could sit on an old schema indefinitely. Running it for every
        // project at startup closes that gap and is how the v1 -> v2 visual_prompt tag migration
        // (Camera directive:/Performance:/Optics: -> <Camera>/<Performance>/<Optics>) actually reaches
        // existing projects. Idempotent — MigrateIfNeededAsync no-ops once a project is already current.
        try
        {
            var opts = app.Services.GetRequiredService<IOptions<PageToMovieOptions>>().Value;
            var workspaceRoot = opts.WorkspaceRoot ?? Directory.GetCurrentDirectory();
            var projectsDir = Path.Combine(workspaceRoot, ApiText.ProjectsFolder);
            var projectMigrations = app.Services.GetRequiredService<ProjectMigrationService>();

            if (Directory.Exists(projectsDir))
            {
                var migratedCount = 0;
                foreach (var projectJsonPath in Directory.EnumerateFiles(projectsDir, "project.json", SearchOption.AllDirectories))
                {
                    var projectDir = Path.GetDirectoryName(projectJsonPath);
                    if (string.IsNullOrWhiteSpace(projectDir)) continue;
                    try
                    {
                        if (await projectMigrations.MigrateIfNeededAsync(projectDir))
                            migratedCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Project migration skipped for {projectDir}: {ex.Message}");
                    }
                }
                if (migratedCount > 0)
                    Console.WriteLine($"Startup schema migration: upgraded {migratedCount} project(s) to {ProjectMigrationService.CurrentSchemaVersion}.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Project schema migration error: {ex.Message}");
        }

        // Clean up any leftover staged demo movie files under _demos to reclaim server volume space
        try
        {
            var demosService = app.Services.GetRequiredService<DemoCatalogService>();
            demosService.CleanupStagedDemoMovies();
        }
        catch { /* non-fatal */ }

        // One-time self-heal: legacy demo records may store an email in CreatedBy (before ownership ids
        // were normalized to a non-email UserId). Rewrite each to the account's canonical id so the public
        // byline shows a handle and ownership checks line up. Idempotent — no-ops once records are clean.
        try
        {
            var demosService = app.Services.GetRequiredService<DemoCatalogService>();
            var userDb = app.Services.GetRequiredService<UserDatabaseService>();
            var migrated = await demosService.MigrateEmailCreatedByAsync(async (email, ct) =>
            {
                var u = await userDb.GetUserByEmailAsync(email, ct).ConfigureAwait(false);
                if (u is null) return null;
                return string.IsNullOrWhiteSpace(u.UserId)
                    ? (string.IsNullOrWhiteSpace(u.Username) ? null : u.Username.Trim())
                    : u.UserId.Trim();
            });
            if (migrated > 0)
                Console.WriteLine($"Startup demo migration: healed CreatedBy on {migrated} demo record(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Demo CreatedBy migration error: {ex.Message}");
        }
    }

    static void UsePerRequestApiKeyScope(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var keyProvider = context.RequestServices.GetService<IUserApiKeyProvider>();
            var user = context.RequestServices.GetService<IUserContext>();
            var uid = user?.UserId;
            // Request header override is treated as xAI/Grok (legacy X-Api-Key).
            var xai = !string.IsNullOrWhiteSpace(user?.RequestApiKey)
                ? user.RequestApiKey
                : (keyProvider is not null ? await keyProvider.GetKeyAsync(uid, "grok") : null);
            var gemini = keyProvider is not null ? await keyProvider.GetKeyAsync(uid, "gemini") : null;
            var anthropic = keyProvider is not null ? await keyProvider.GetKeyAsync(uid, "anthropic") : null;
            var fal = keyProvider is not null ? await keyProvider.GetKeyAsync(uid, "fal") : null;
            var suno = keyProvider is not null ? await keyProvider.GetKeyAsync(uid, "suno") : null;
            var aimusicapi = keyProvider is not null ? await keyProvider.GetKeyAsync(uid, "aimusicapi") : null;
            var elevenlabs = keyProvider is not null ? await keyProvider.GetKeyAsync(uid, ApiText.ElevenLabsClient) : null;
            using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["grok"] = xai,
                ["gemini"] = gemini,
                ["anthropic"] = anthropic,
                ["fal"] = fal,
                ["suno"] = suno,
                ["aimusicapi"] = aimusicapi,
                [ApiText.ElevenLabsClient] = elevenlabs,
            }))
            using (UserApiCallScope.Push(uid))
            {
                await next();
            }
        });
    }

    static async Task SeedBundledDemosAsync(WebApplication app)
    {
        // Copy any bundled seed_demos/* entries into /data/_demos/ if not already present.
        // This ensures public demos are available for new deployments without manual admin steps.
        try
        {
            var store = app.Services.GetRequiredService<ProjectStore>();
            var demoCatalog = app.Services.GetRequiredService<DemoCatalogService>();
            var demosDir = demoCatalog.DemosDir;
            Directory.CreateDirectory(demosDir);

            var seedRoot = Path.Combine(AppContext.BaseDirectory, "seed_demos");
            if (Directory.Exists(seedRoot))
            {
                foreach (var seedDir in Directory.EnumerateDirectories(seedRoot))
                {
                    var id = Path.GetFileName(seedDir);
                    var targetDir = Path.Combine(demosDir, id);
                    var targetMeta = Path.Combine(targetDir, "meta.json");
                    var targetMovie = Path.Combine(targetDir, "movie.mp4");

                    if (File.Exists(targetMeta) && File.Exists(targetMovie))
                        continue; // already seeded — never overwrite user data

                    Directory.CreateDirectory(targetDir);

                    var srcMeta = Path.Combine(seedDir, "meta.json");
                    if (File.Exists(srcMeta))
                        File.Copy(srcMeta, targetMeta, overwrite: true);

                    var srcMovie = Path.Combine(seedDir, "movie.mp4");
                    if (!File.Exists(srcMovie))
                    {
                        try
                        {
                            var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                                await File.ReadAllTextAsync(srcMeta));
                            if (meta.TryGetProperty(ApiText.ProjectIdKey, out var pidEl) &&
                                pidEl.GetString() is { Length: > 0 } pid)
                            {
                                var wipPath = store.ResolveWipMoviePath(pid);
                                if (wipPath is not null && File.Exists(wipPath))
                                    srcMovie = wipPath;
                            }
                        }
                        catch { /* ignore — seed gracefully skipped if movie unavailable */ }
                    }

                    if (File.Exists(srcMovie))
                        File.Copy(srcMovie, targetMovie, overwrite: true);

                    if (File.Exists(targetMeta) && File.Exists(targetMovie))
                        app.Logger.LogInformation("Seeded demo {Id} into {TargetDir}", id, targetDir);
                    else
                        app.Logger.LogWarning("Demo seed {Id} skipped — movie not found at {Src}", id, srcMovie);
                }
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Demo seeding failed (non-fatal)");
        }
    }
}
