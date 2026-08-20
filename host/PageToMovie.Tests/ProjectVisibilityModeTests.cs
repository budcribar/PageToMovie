using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectVisibilityModeTests
{
    private static (ProjectStore Store, string Root) MakeHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_visibility_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
        var store = new ProjectStore(opts);
        return (store, root);
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(root, true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Saves_And_Reads_VisibilityMode_Defaulting_To_Private()
    {
        var (store, root) = MakeHarness();
        try
        {
            var proj = await store.CreateProjectAsync("orig_proj", "Original Project", ownerUserId: "alice");

            Assert.Equal(ProjectVisibility.Private, proj.VisibilityMode);
            Assert.Equal("alice/orig_proj", proj.Id);
            Assert.Contains(Path.Combine("projects", "alice", "orig_proj"), proj.Path);

            // Change to Public Read-Only
            var updated = await store.SetProjectVisibilityModeAsync(proj.Id, "Public");
            Assert.Equal(ProjectVisibility.Public, updated.VisibilityMode);

            // Re-read project
            var reloaded = await store.GetProjectAsync(proj.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(ProjectVisibility.Public, reloaded!.VisibilityMode);

            // Change to Public (Forkable) — a REAL Open mode, distinct from read-only Public.
            var openProj = await store.SetProjectVisibilityModeAsync(proj.Id, "Open");
            Assert.Equal(ProjectVisibility.Open, openProj.VisibilityMode);
            var reloadedOpen = await store.GetProjectAsync(proj.Id);
            Assert.Equal(ProjectVisibility.Open, reloadedOpen!.VisibilityMode);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ForkProjectAsync_Enforces_Open_Visibility_For_Non_Owners()
    {
        var (store, root) = MakeHarness();
        try
        {
            var source = await store.CreateProjectAsync("private_proj", "Private Project", ownerUserId: "alice");
            Assert.Equal("alice/private_proj", source.Id);

            // Attempt to fork Private project by bob (should throw)
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ForkProjectAsync(source.Id, "bob"));
            Assert.Contains("Forking disabled", ex.Message);

            // Read-only Public is viewable but NOT forkable — the distinction Open exists for.
            await store.SetProjectVisibilityModeAsync(source.Id, "Public");
            var exPublic = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ForkProjectAsync(source.Id, "bob"));
            Assert.Contains("Forking disabled", exPublic.Message);

            // Change visibility to Open
            await store.SetProjectVisibilityModeAsync(source.Id, "Open");

            // Fork Open project by bob (should succeed under bob's namespace)
            var forked = await store.ForkProjectAsync(source.Id, "bob");
            Assert.NotNull(forked);
            Assert.Equal("bob", forked.OwnerUserId);
            Assert.Equal(source.Id, forked.ParentProjectId);
            Assert.StartsWith("bob/", forked.Id, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine("projects", "bob"), forked.Path);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(ProjectVisibility.Private, "Private")]
    [InlineData(ProjectVisibility.Public, "Public")]
    [InlineData(ProjectVisibility.Unlisted, "Unlisted")]
    [InlineData(ProjectVisibility.Open, "Open")]
    public void Json_roundtrips_canonical_visibility(ProjectVisibility value, string expectedName)
    {
        var json = JsonSerializer.Serialize(new ProjectInfo { Id = "p", VisibilityMode = value }, EngineApiClient.JsonOpts);
        Assert.Contains($"\"visibilityMode\":\"{expectedName}\"", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<ProjectInfo>(json, EngineApiClient.JsonOpts);
        Assert.NotNull(roundTrip);
        Assert.Equal(value, roundTrip!.VisibilityMode);
    }

    [Theory]
    [InlineData("\"publicforkable\"", ProjectVisibility.Open)]
    [InlineData("\"PublicForkable\"", ProjectVisibility.Open)]
    [InlineData("\"forkable\"", ProjectVisibility.Open)]
    [InlineData("\"Forkable\"", ProjectVisibility.Open)]
    [InlineData("\"open\"", ProjectVisibility.Open)]
    [InlineData("\"not-a-real-mode\"", ProjectVisibility.Private)]
    [InlineData("\"\"", ProjectVisibility.Private)]
    [InlineData("null", ProjectVisibility.Private)]
    public void Json_maps_aliases_and_unknown_visibility_without_throwing(string jsonValue, ProjectVisibility expected)
    {
        var json = $$"""{"id":"p","visibilityMode":{{jsonValue}}}""";
        var parsed = JsonSerializer.Deserialize<ProjectInfo>(json, EngineApiClient.JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.VisibilityMode);
    }

    [Fact]
    public void Project_list_still_loads_when_one_visibility_is_unknown()
    {
        // WASM GetProjectsAsync deserializes this shape with EngineApiClient.JsonOpts.
        // A single leftover alias / unknown string used to throw DeserializeUnableToConvertValue
        // at $.projects[n].visibilityMode and drop the whole picker payload.
        const string json = """
            {
              "ok": true,
              "projects": [
                { "id": "a", "visibilityMode": "Private" },
                { "id": "b", "visibilityMode": "Public" },
                { "id": "c", "visibilityMode": "Unlisted" },
                { "id": "d", "visibilityMode": "Open" },
                { "id": "alias", "visibilityMode": "publicforkable" },
                { "id": "alias2", "visibilityMode": "forkable" },
                { "id": "bad", "visibilityMode": "totally-unknown" }
              ]
            }
            """;

        var dto = JsonSerializer.Deserialize<ProjectsDto>(json, EngineApiClient.JsonOpts);
        Assert.NotNull(dto);
        Assert.True(dto!.Ok);
        Assert.Equal(7, dto.Projects.Count);
        Assert.Equal(ProjectVisibility.Private, dto.Projects[0].VisibilityMode);
        Assert.Equal(ProjectVisibility.Public, dto.Projects[1].VisibilityMode);
        Assert.Equal(ProjectVisibility.Unlisted, dto.Projects[2].VisibilityMode);
        Assert.Equal(ProjectVisibility.Open, dto.Projects[3].VisibilityMode);
        Assert.Equal(ProjectVisibility.Open, dto.Projects[4].VisibilityMode);
        Assert.Equal(ProjectVisibility.Open, dto.Projects[5].VisibilityMode);
        Assert.Equal(ProjectVisibility.Private, dto.Projects[6].VisibilityMode);
    }
}
