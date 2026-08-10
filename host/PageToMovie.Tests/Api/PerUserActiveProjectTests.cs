using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PageToMovie.Tests.Api;

public class PerUserActiveProjectTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public PerUserActiveProjectTests(PageToMovieApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Active_project_is_isolated_per_user()
    {
        var userDb = _factory.Services.GetRequiredService<UserDatabaseService>();
        var user1 = "user1_" + Guid.NewGuid().ToString("N")[..6];
        var user2 = "user2_" + Guid.NewGuid().ToString("N")[..6];

        await userDb.InsertUserAsync(new UserEntity
        {
            UserId = user1,
            Username = user1,
            PasswordHash = "hash",
            Role = "User",
            CreatedAt = DateTime.UtcNow,
        });

        await userDb.InsertUserAsync(new UserEntity
        {
            UserId = user2,
            Username = user2,
            PasswordHash = "hash",
            Role = "User",
            CreatedAt = DateTime.UtcNow,
        });

        using var client1 = _factory.CreateUserClient(user1);
        using var client2 = _factory.CreateUserClient(user2);

        // User 1 creates Project 1
        var proj1Resp = await client1.PostAsJsonAsync("/api/projects", new { name = "ProjectOne", title = "Project One" });
        Assert.Equal(HttpStatusCode.OK, proj1Resp.StatusCode);
        var proj1Json = await proj1Resp.Content.ReadFromJsonAsync<JsonElement>();
        var proj1Id = proj1Json.GetProperty("active").GetProperty("id").GetString();
        Assert.NotNull(proj1Id);

        // User 2 creates Project 2
        var proj2Resp = await client2.PostAsJsonAsync("/api/projects", new { name = "ProjectTwo", title = "Project Two" });
        Assert.Equal(HttpStatusCode.OK, proj2Resp.StatusCode);
        var proj2Json = await proj2Resp.Content.ReadFromJsonAsync<JsonElement>();
        var proj2Id = proj2Json.GetProperty("active").GetProperty("id").GetString();
        Assert.NotNull(proj2Id);

        // Verify User 1's active project is Project 1
        var list1Resp = await client1.GetAsync("/api/projects");
        var list1Json = await list1Resp.Content.ReadFromJsonAsync<JsonElement>();
        var active1 = list1Json.GetProperty("active").GetProperty("id").GetString();
        Assert.Equal(proj1Id, active1);

        // Verify User 2's active project is Project 2 (not affected by User 1)
        var list2Resp = await client2.GetAsync("/api/projects");
        var list2Json = await list2Resp.Content.ReadFromJsonAsync<JsonElement>();
        var active2 = list2Json.GetProperty("active").GetProperty("id").GetString();
        Assert.Equal(proj2Id, active2);
    }

    [Fact]
    public async Task Delete_response_does_not_leak_another_users_active_project_or_inventory()
    {
        var userDb = _factory.Services.GetRequiredService<UserDatabaseService>();
        var user1 = "delu1_" + Guid.NewGuid().ToString("N")[..6];
        var user2 = "delu2_" + Guid.NewGuid().ToString("N")[..6];

        await userDb.InsertUserAsync(new UserEntity
        {
            UserId = user1, Username = user1, PasswordHash = "hash", Role = "User", CreatedAt = DateTime.UtcNow,
        });
        await userDb.InsertUserAsync(new UserEntity
        {
            UserId = user2, Username = user2, PasswordHash = "hash", Role = "User", CreatedAt = DateTime.UtcNow,
        });

        using var client1 = _factory.CreateUserClient(user1);
        using var client2 = _factory.CreateUserClient(user2);

        // User 1 creates two projects; user 2 creates one — user 2's becomes the process-wide
        // ProjectStore.ActiveProjectId fallback simply by being created last.
        var proj1AResp = await client1.PostAsJsonAsync("/api/projects", new { name = "DelOne" });
        var proj1AId = (await proj1AResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("active").GetProperty("id").GetString();
        var proj1BResp = await client1.PostAsJsonAsync("/api/projects", new { name = "DelTwo" });
        var proj1BId = (await proj1BResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("active").GetProperty("id").GetString();

        var proj2Resp = await client2.PostAsJsonAsync("/api/projects", new { name = "DelOther" });
        Assert.Equal(HttpStatusCode.OK, proj2Resp.StatusCode);
        var proj2Id = (await proj2Resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("active").GetProperty("id").GetString();
        Assert.NotNull(proj2Id);

        // User 1 re-activates their first project, then deletes their second.
        await client1.PostAsync($"/api/projects/{Uri.EscapeDataString(proj1AId!)}/activate", content: null);
        var delResp = await client1.DeleteAsync($"/api/projects/{Uri.EscapeDataString(proj1BId!)}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
        var delJson = await delResp.Content.ReadFromJsonAsync<JsonElement>();

        // active must be user1's own project, never user2's (the process-global fallback).
        var active = delJson.GetProperty("active").GetProperty("id").GetString();
        Assert.Equal(proj1AId, active);

        // projects must only list user1's own inventory, never user2's project.
        var listedIds = delJson.GetProperty("projects").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .ToList();
        Assert.DoesNotContain(proj2Id, listedIds);
        Assert.Contains(proj1AId, listedIds);
    }

    [Fact]
    public async Task Stage2_status_reflects_the_calling_users_own_active_project()
    {
        var userDb = _factory.Services.GetRequiredService<UserDatabaseService>();
        var user1 = "s2u1_" + Guid.NewGuid().ToString("N")[..6];
        var user2 = "s2u2_" + Guid.NewGuid().ToString("N")[..6];

        await userDb.InsertUserAsync(new UserEntity
        {
            UserId = user1, Username = user1, PasswordHash = "hash", Role = "User", CreatedAt = DateTime.UtcNow,
        });
        await userDb.InsertUserAsync(new UserEntity
        {
            UserId = user2, Username = user2, PasswordHash = "hash", Role = "User", CreatedAt = DateTime.UtcNow,
        });

        using var client1 = _factory.CreateUserClient(user1);
        using var client2 = _factory.CreateUserClient(user2);

        var proj1Resp = await client1.PostAsJsonAsync("/api/projects", new { name = "S2One" });
        var proj1Id = (await proj1Resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("active").GetProperty("id").GetString();

        // User 2 activates last — becomes the process-global ProjectStore.ActiveProjectId fallback.
        var proj2Resp = await client2.PostAsJsonAsync("/api/projects", new { name = "S2Two" });
        var proj2Id = (await proj2Resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("active").GetProperty("id").GetString();
        Assert.NotNull(proj2Id);

        var statusResp = await client1.GetAsync("/api/stage2-status");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var statusJson = await statusResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(proj1Id, statusJson.GetProperty("project_id").GetString());
    }
}
