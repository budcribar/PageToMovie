using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// The take-addressed clip route. The caller names the take, so the server cannot answer with a
/// different one — which is what let Review and the Film page show different footage for the same
/// clip while each believed it had the current take.
/// </summary>
public class ClipTakeVideoRouteApiTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public ClipTakeVideoRouteApiTests(PageToMovieApiFactory factory) => _factory = factory;

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var slug = "TakeRoute_" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Take Route Test" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("active").GetProperty("id").GetString()!;
    }

    private string WriteTake(string projectId, int scene, int clip, int take, string marker)
    {
        var videoDir = Path.Combine(_factory.WorkspaceRoot, "projects", projectId, "assets", "video");
        Directory.CreateDirectory(videoDir);
        var path = Path.Combine(videoDir, ClipTakeNaming.TakeMp4FileName(scene, clip, take));
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(marker + new string('x', 2000)));
        return videoDir;
    }

    [Fact]
    public async Task Serves_the_take_that_was_asked_for_not_the_current_one()
    {
        var client = _factory.CreateUserClient("take-route-user");
        var projectId = await CreateProjectAsync(client);
        var videoDir = WriteTake(projectId, 1, 1, take: 1, marker: "TAKE-ONE");
        WriteTake(projectId, 1, 1, take: 2, marker: "TAKE-TWO");
        ClipSidecarService.WriteCurrentTake(videoDir, 1, 1, 2);

        var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/1/clips/1/takes/1/video";
        var resp = await client.GetAsync(url);

        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        Assert.StartsWith("TAKE-ONE", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_take_that_is_not_there_is_a_404_not_a_substitute()
    {
        var client = _factory.CreateUserClient("take-route-user-2");
        var projectId = await CreateProjectAsync(client);
        var videoDir = WriteTake(projectId, 2, 1, take: 1, marker: "TAKE-ONE");
        ClipSidecarService.WriteCurrentTake(videoDir, 2, 1, 1);

        var resp = await client.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/2/clips/1/takes/9/video");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
