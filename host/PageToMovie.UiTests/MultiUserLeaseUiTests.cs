using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// P5 dual-user Playwright tests: two browser contexts (alice / bob) with
/// distinct X-User-Id headers, exercising project leases end-to-end.
/// </summary>
[Collection("ui-multiuser-lease")]
public sealed class MultiUserLeaseUiTests
{
    private readonly MultiUserLeaseFixture _fx;

    public MultiUserLeaseUiTests(MultiUserLeaseFixture fx) => _fx = fx;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Two_contexts_have_independent_user_headers()
    {
        await using var alice = await UserContext.CreateAsync(_fx, "alice");
        await using var bob = await UserContext.CreateAsync(_fx, "bob");

        // Each context can reach the app
        await alice.Page.GotoAsync(_fx.BaseUrl + "/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        await bob.Page.GotoAsync(_fx.BaseUrl + "/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });

        Assert.DoesNotContain("error", (alice.Page.Url ?? "").ToLowerInvariant());
        Assert.DoesNotContain("error", (bob.Page.Url ?? "").ToLowerInvariant());
    }

    [Fact]
    public async Task Lease_second_user_gets_423_conflict()
    {
        await using var alice = await UserContext.CreateAsync(_fx, "alice");
        await using var bob = await UserContext.CreateAsync(_fx, "bob");

        var projectId = await alice.CreateProjectAsync("dual-lease-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(string.IsNullOrWhiteSpace(projectId));

        // Owner grants bob editor so he can attempt the lease
        var granted = await alice.GrantEditorAsync(projectId!, "bob");
        Assert.True(granted, "alice should be able to grant bob editor on her project");

        var resource = "scene:dual-test";

        var (aliceStatus, aliceBody) = await alice.AcquireLeaseAsync(projectId!, resource);
        Assert.Equal(HttpStatusCode.OK, aliceStatus);
        Assert.Equal("alice", aliceBody.GetProperty("holderUserId").GetString());

        var (bobStatus, bobBody) = await bob.AcquireLeaseAsync(projectId!, resource);
        Assert.Equal(HttpStatusCode.Locked, bobStatus); // 423
        Assert.Equal("lease_held", bobBody.GetProperty("error").GetString());
        Assert.Equal("alice", bobBody.GetProperty("holderUserId").GetString());

        // Alice releases → bob can acquire
        var released = await alice.ReleaseLeaseAsync(projectId!, resource);
        Assert.True(released);

        var (bob2Status, bob2Body) = await bob.AcquireLeaseAsync(projectId!, resource);
        Assert.Equal(HttpStatusCode.OK, bob2Status);
        Assert.Equal("bob", bob2Body.GetProperty("holderUserId").GetString());
    }

    [Fact]
    public async Task Lease_same_user_refreshes_without_conflict()
    {
        await using var alice = await UserContext.CreateAsync(_fx, "alice-refresh");

        var projectId = await alice.CreateProjectAsync("refresh-lease-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(string.IsNullOrWhiteSpace(projectId));

        var resource = "scene:refresh";
        var (s1, b1) = await alice.AcquireLeaseAsync(projectId!, resource);
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal("alice-refresh", b1.GetProperty("holderUserId").GetString());

        var (s2, b2) = await alice.AcquireLeaseAsync(projectId!, resource);
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal("alice-refresh", b2.GetProperty("holderUserId").GetString());
    }

    [Fact]
    public async Task Independent_resources_do_not_conflict_across_users()
    {
        await using var alice = await UserContext.CreateAsync(_fx, "alice-indep");
        await using var bob = await UserContext.CreateAsync(_fx, "bob-indep");

        var projectId = await alice.CreateProjectAsync("indep-lease-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(string.IsNullOrWhiteSpace(projectId));
        Assert.True(await alice.GrantEditorAsync(projectId!, "bob-indep"));

        var (aStatus, _) = await alice.AcquireLeaseAsync(projectId!, "scene:1");
        var (bStatus, bBody) = await bob.AcquireLeaseAsync(projectId!, "scene:2");

        Assert.Equal(HttpStatusCode.OK, aStatus);
        Assert.Equal(HttpStatusCode.OK, bStatus);
        Assert.Equal("bob-indep", bBody.GetProperty("holderUserId").GetString());
    }

    [Fact]
    public async Task List_active_leases_shows_holder()
    {
        await using var alice = await UserContext.CreateAsync(_fx, "alice-list");
        await using var bob = await UserContext.CreateAsync(_fx, "bob-list");

        var projectId = await alice.CreateProjectAsync("list-lease-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(string.IsNullOrWhiteSpace(projectId));
        Assert.True(await alice.GrantEditorAsync(projectId!, "bob-list"));

        await alice.AcquireLeaseAsync(projectId!, "scene:listed");

        // No bulk-list endpoint exists server-side (CollaborationEndpoints only exposes
        // single-resource GET /leases/{resourceKey}); query the held resource directly.
        var (status, body) = await bob.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId!)}/leases/{Uri.EscapeDataString("scene:listed")}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("scene:listed", body.GetProperty("resourceKey").GetString());
        Assert.Equal("alice-list", body.GetProperty("holderUserId").GetString());
    }

    [Fact]
    public async Task Dual_browser_can_open_share_page()
    {
        await using var alice = await UserContext.CreateAsync(_fx, "alice-ui");
        await using var bob = await UserContext.CreateAsync(_fx, "bob-ui");

        var projectId = await alice.CreateProjectAsync("share-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(string.IsNullOrWhiteSpace(projectId));
        Assert.True(await alice.GrantEditorAsync(projectId!, "bob-ui"));

        // Navigate both to studio share (Blazor host)
        var shareUrl = _fx.BaseUrl.TrimEnd('/') + "/studio/share";
        await alice.Page.GotoAsync(shareUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        await bob.Page.GotoAsync(shareUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });

        // Pages should render something (not a hard 500)
        var aliceContent = await alice.Page.ContentAsync();
        var bobContent = await bob.Page.ContentAsync();
        Assert.False(string.IsNullOrWhiteSpace(aliceContent));
        Assert.False(string.IsNullOrWhiteSpace(bobContent));
        Assert.DoesNotContain("HTTP ERROR 500", aliceContent);
        Assert.DoesNotContain("HTTP ERROR 500", bobContent);
    }

    /// <summary>One Playwright browser context bound to a single user id via X-User-Id.</summary>
    private sealed class UserContext : IAsyncDisposable
    {
        private readonly AppFixture _fx;
        public string UserId { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; }
        private readonly HttpClient _http;

        private UserContext(AppFixture fx, string userId, IBrowserContext context, IPage page, HttpClient http)
        {
            _fx = fx;
            UserId = userId;
            Context = context;
            Page = page;
            _http = http;
        }

        public static async Task<UserContext> CreateAsync(AppFixture fx, string userId)
        {
            var context = await fx.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["X-User-Id"] = userId
                }
            });
            var page = await context.NewPageAsync();
            var http = new HttpClient { BaseAddress = new Uri(fx.BaseUrl.TrimEnd('/') + "/") };
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", userId);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return new UserContext(fx, userId, context, page, http);
        }

        public async Task<string?> CreateProjectAsync(string title)
        {
            var payload = JsonSerializer.Serialize(new { name = title });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/projects", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("active", out var active) &&
                active.TryGetProperty("id", out var id))
                return id.GetString();
            if (doc.RootElement.TryGetProperty("id", out var topId))
                return topId.GetString();
            if (doc.RootElement.TryGetProperty("projectId", out var pid))
                return pid.GetString();
            return null;
        }

        public async Task<bool> GrantEditorAsync(string projectId, string userId)
        {
            var payload = JsonSerializer.Serialize(new { userId });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"api/projects/{Uri.EscapeDataString(projectId)}/acl/editors", content);
            return resp.IsSuccessStatusCode;
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> AcquireLeaseAsync(string projectId, string resourceKey)
        {
            var encoded = Uri.EscapeDataString(resourceKey);
            using var resp = await _http.PostAsync($"api/projects/{Uri.EscapeDataString(projectId)}/leases/{encoded}/acquire", content: null);
            var text = await resp.Content.ReadAsStringAsync();
            JsonElement body;
            try { body = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); }
            catch { body = default; }
            return (resp.StatusCode, body);
        }

        public async Task<bool> ReleaseLeaseAsync(string projectId, string resourceKey)
        {
            var encoded = Uri.EscapeDataString(resourceKey);
            using var resp = await _http.PostAsync($"api/projects/{Uri.EscapeDataString(projectId)}/leases/{encoded}/release", content: null);
            return resp.IsSuccessStatusCode;
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string relativePath)
        {
            using var resp = await _http.GetAsync(relativePath.TrimStart('/'));
            var text = await resp.Content.ReadAsStringAsync();
            JsonElement body;
            try { body = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); }
            catch { body = default; }
            return (resp.StatusCode, body);
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await Context.DisposeAsync();
        }
    }
}
