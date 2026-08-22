using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// ffmpeg credits generator upload: same take persist as any other clip.
/// </summary>
public class CreditsTakeUploadApiTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public CreditsTakeUploadApiTests(PageToMovieApiFactory factory) => _factory = factory;

    private static MultipartFormDataContent BuildVideoForm()
    {
        var bytes = Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000));
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        form.Add(content, "video", "scene_02_clip_01.mp4");
        return form;
    }

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var slug = "CreditsTake_" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Credits Take" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("active").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Two_credits_uploads_write_take_01_then_take_02_and_promote_works()
    {
        var client = _factory.CreateUserClient("credits-take-user");
        var projectId = await CreateProjectAsync(client);
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(projectId), "assets", "video");

        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(videoDir).FullName, "scene_02_clip_01_take_09_20260821_120000.clip.json"),
            """{"take":9,"visual_prompt":"stub"}""");

        using (var form1 = BuildVideoForm())
        {
            var resp1 = await client.PostAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/2/clips/1/upload?kind=credits", form1);
            Assert.True(resp1.IsSuccessStatusCode, await resp1.Content.ReadAsStringAsync());
            using var doc1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync());
            Assert.Equal(1, doc1.RootElement.GetProperty("take").GetInt32());
            Assert.Equal(ClipTakeNaming.TakeRelativePath(2, 1, 1), doc1.RootElement.GetProperty("clientRelativePath").GetString());
        }

        using (var form2 = BuildVideoForm())
        {
            var resp2 = await client.PostAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/2/clips/1/upload?kind=credits", form2);
            Assert.True(resp2.IsSuccessStatusCode, await resp2.Content.ReadAsStringAsync());
            using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
            Assert.Equal(2, doc2.RootElement.GetProperty("take").GetInt32());
        }

        Assert.True(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_01.mp4")));
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_02.mp4")));
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_02_clip_01.mp4")));
        Assert.Equal(2, ClipSidecarService.ReadCurrentTake(videoDir, 2, 1));
        Assert.Equal(
            Path.Combine(videoDir, "scene_02_clip_01_take_02.mp4"),
            ClipSidecarService.CurrentTakePath(videoDir, 2, 1));

        var promoted = await store.PromoteClipVersionAsync(projectId, 2, 1, "scene_02_clip_01_take_01.mp4");
        Assert.True(promoted);
        Assert.Equal(1, ClipSidecarService.ReadCurrentTake(videoDir, 2, 1));
    }
}
