using PageToMovie.Core.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PageToMovie.Tests.Api;

/// <summary>In-process API host with fakes + isolated temp workspace.</summary>
public sealed class PageToMovieApiFactory : WebApplicationFactory<PageToMovie.Api.Program>
{
    private readonly string _workspace;

    public PageToMovieApiFactory()
    {
        // Program.cs decides which concrete client gets registered for every provider interface
        // (FakeGrokVideoClient vs. the real GrokVideoClient/MultiProviderVideoClient, etc.) from a
        // `useFakes` boolean read off `builder.Configuration` in its own top-level statements —
        // BEFORE WebApplicationFactory's ConfigureWebHost customizations below (AddInMemoryCollection
        // / PostConfigure<PageToMovieOptions>) are layered into that configuration. Relying on those
        // alone silently leaves the real (non-fake) provider clients registered, so a "fakes-only"
        // test can end up making real, unauthenticated calls to api.x.ai. Program.cs's useFakes check
        // also does a direct Environment.GetEnvironmentVariable("PageToMovie_USE_FAKES") read, which
        // has none of that timing dependency, so set it here — before anything below can trigger a
        // lazy host build — to guarantee fakes actually activate.
        Environment.SetEnvironmentVariable("PageToMovie_USE_FAKES", "1");
        _workspace = Path.Combine(Path.GetTempPath(), "fs_api_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_workspace, "projects"));
        Directory.CreateDirectory(Path.Combine(_workspace, "prompts"));
    }

    public string WorkspaceRoot => _workspace;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PageToMovie:WorkspaceRoot"] = _workspace,
                ["PageToMovie:UseFakes"] = "true",
                ["PageToMovie:EnableReadCaches"] = "true",
                ["PageToMovie:Auth:AllowDevBypass"] = "true",
                ["PageToMovie:Auth:RequireLogin"] = "false",
                ["PageToMovie:Auth:AdminUsername"] = "admin",
                ["PageToMovie:Auth:AdminPassword"] = "admin",
                ["PageToMovie:Auth:DefaultUserId"] = "test-user",
                ["PageToMovie:Auth:AdminUserIds:0"] = AdminFixtureUserId,
                // Force YouTube OAuth to "unconfigured" regardless of the host machine's real
                // environment variables (e.g. PageToMovie__YouTube__ClientId/ClientSecret/RedirectUri
                // set for local dev use of the actual product) — otherwise a dev/CI box with real
                // credentials in its environment leaks them into this isolated test host and the
                // "unconfigured" gating tests (YouTubeUploadTests) become flaky/false-negative.
                ["PageToMovie:YouTube:ClientId"] = "",
                ["PageToMovie:YouTube:ClientSecret"] = "",
                ["PageToMovie:YouTube:RedirectUri"] = "",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<PageToMovieOptions>(o =>
            {
                o.WorkspaceRoot = _workspace;
                o.UseFakes = true;
                o.EnableReadCaches = true;
                o.Auth ??= new AuthOptions();
                o.Auth.RequireLogin = false;
                o.Auth.AdminUserIds ??= new List<string>();
                if (!o.Auth.AdminUserIds.Contains(AdminFixtureUserId, StringComparer.OrdinalIgnoreCase))
                    o.Auth.AdminUserIds.Add(AdminFixtureUserId);
                o.YouTube ??= new YouTubeOptions();
                o.YouTube.ClientId = "";
                o.YouTube.ClientSecret = "";
                o.YouTube.RedirectUri = "";
            });
        });
    }

    /// <summary>Non-owner admin id used by ACL/activate tests — not a production default.</summary>
    public const string AdminFixtureUserId = "acl-admin-fixture";

    public HttpClient CreateUserClient(string userId = "test-user")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Id");
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        return client;
    }

    public HttpClient CreateAdminClient(string userId = AdminFixtureUserId) =>
        CreateUserClient(userId);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch { /* temp */ }
    }
}
