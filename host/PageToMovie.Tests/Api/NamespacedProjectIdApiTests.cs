using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// owner/Name project ids must activate and heartbeat as the owner — not 403
/// because routing captured only the owner segment (or rejected %2F).
/// </summary>
public class NamespacedProjectIdApiTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public NamespacedProjectIdApiTests(PageToMovieApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Owner_can_activate_and_heartbeat_owner_slash_name_via_literal_slash_path()
    {
        var (ownerClient, ownerId, projectId) = await CreateNamespacedProjectAsync();
        Assert.Contains('/', projectId);

        // Literal slash: /api/projects/owner/Name/... — the form proxies emit after decoding %2F.
        var activate = await ownerClient.PostAsync($"/api/projects/{projectId}/activate", content: null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var heartbeat = await ownerClient.PostAsync($"/api/projects/{projectId}/presence/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        var presence = await ownerClient.GetAsync($"/api/projects/{projectId}/presence");
        Assert.Equal(HttpStatusCode.OK, presence.StatusCode);
        var list = await presence.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Contains(list.EnumerateArray(), p =>
        {
            var uid = p.TryGetProperty("userId", out var camel) ? camel.GetString()
                : p.TryGetProperty("UserId", out var pascal) ? pascal.GetString()
                : null;
            return string.Equals(uid, ownerId, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Owner_can_activate_and_heartbeat_via_encoded_slash_path()
    {
        var (ownerClient, _, projectId) = await CreateNamespacedProjectAsync();
        var encoded = Uri.EscapeDataString(projectId);
        Assert.Contains("%2F", encoded, StringComparison.OrdinalIgnoreCase);

        var activate = await ownerClient.PostAsync($"/api/projects/{encoded}/activate", content: null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var heartbeat = await ownerClient.PostAsync($"/api/projects/{encoded}/presence/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        var presence = await ownerClient.GetAsync($"/api/projects/{encoded}/presence");
        Assert.Equal(HttpStatusCode.OK, presence.StatusCode);
    }

    [Fact]
    public async Task Stranger_gets_403_with_full_namespaced_id_not_owner_segment_only()
    {
        var (ownerClient, ownerId, projectId) = await CreateNamespacedProjectAsync();
        _ = ownerClient;
        var slash = projectId.IndexOf('/');
        Assert.True(slash > 0);
        var ownerSegment = projectId[..slash];

        var strangerId = "stranger_" + Guid.NewGuid().ToString("N")[..8];
        await InsertUserAsync(strangerId);
        using var stranger = _factory.CreateUserClient(strangerId);

        var heartbeat = await stranger.PostAsync($"/api/projects/{projectId}/presence/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, heartbeat.StatusCode);
        var body = await heartbeat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("project_access_denied", body.GetProperty("error").GetString());
        Assert.Equal(projectId, body.GetProperty("projectId").GetString());
        Assert.NotEqual(ownerSegment, body.GetProperty("projectId").GetString());
        Assert.NotEqual(ownerId, body.GetProperty("projectId").GetString());

        var activate = await stranger.PostAsync($"/api/projects/{projectId}/activate", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, activate.StatusCode);

        var presence = await stranger.GetAsync($"/api/projects/{projectId}/presence");
        Assert.Equal(HttpStatusCode.Forbidden, presence.StatusCode);
    }

    private async Task<(HttpClient Client, string UserId, string ProjectId)> CreateNamespacedProjectAsync()
    {
        var userId = "nsowner_" + Guid.NewGuid().ToString("N")[..8];
        await InsertUserAsync(userId);
        var client = _factory.CreateUserClient(userId);

        var slug = "Mary3_" + Guid.NewGuid().ToString("N")[..6];
        var create = await client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Namespaced" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var json = await create.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = json.GetProperty("active").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(projectId));
        Assert.Contains('/', projectId);
        return (client, userId, projectId!);
    }

    private async Task InsertUserAsync(string userId)
    {
        var userDb = _factory.Services.GetRequiredService<UserDatabaseService>();
        await userDb.InsertUserAsync(new UserEntity
        {
            UserId = userId,
            Username = userId,
            PasswordHash = "hash",
            Role = "User",
            CreatedAt = DateTime.UtcNow,
        });
    }
}
