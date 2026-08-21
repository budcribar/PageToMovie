using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Auth;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// Verifies the fakes-mode login bypass: when the server runs on fakes (the test factory sets
/// UseFakes=true), POST /api/auth/dev-login issues a deterministic dev-user session. When fakes
/// are off (production), the same endpoint returns HTTP 200 with Ok=false and no token — a
/// successful negative so the WASM boot probe is not a console 404, and impossible to turn
/// into a session.
/// </summary>
public class DevLoginBypassTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public DevLoginBypassTests(PageToMovieApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Dev_login_issues_deterministic_dev_user_in_fakes_mode()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/auth/dev-login", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("token").GetString()));
        Assert.Equal("budcribar@gmail.com", json.GetProperty("userId").GetString());
        // Dev user is granted admin so the whole studio is browsable end-to-end.
        var roles = json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("admin", roles);
    }
}

/// <summary>Production / fakes-off: the boot probe must 200 with a no-session body.</summary>
public class DevLoginProductionSkipTests : IClassFixture<ProductionDevLoginApiFactory>
{
    private readonly ProductionDevLoginApiFactory _factory;

    public DevLoginProductionSkipTests(ProductionDevLoginApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Dev_login_returns_200_ok_false_and_no_token_when_fakes_off()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/auth/dev-login", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Set-Cookie"));
        Assert.False(resp.Content.Headers.Contains("Set-Cookie"));

        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Ok);
        Assert.True(string.IsNullOrWhiteSpace(body.Token));
        Assert.True(string.IsNullOrWhiteSpace(body.UserId));

        // The 200-no body cannot become a session — /api/auth/me stays anonymous.
        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.NotNull(me);
        Assert.False(me!.IsAuthenticated);
    }
}
