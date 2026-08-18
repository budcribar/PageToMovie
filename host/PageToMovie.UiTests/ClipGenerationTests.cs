using System.Text.RegularExpressions;
using Microsoft.Playwright;

using PageToMovie.Core.Utils;
namespace PageToMovie.UiTests;

/// <summary>
/// A-1b: the full pipeline through portrait lock + voice (ready-for-shots) + clip generation (fake
/// video), then the Scenes page showing generated clips. Exercises the deepest end-to-end path and
/// the Scenes "generated" display state that the component refactor must preserve.
/// </summary>
[Collection("ui-pipeline")]
public class ClipGenerationTests
{
    private readonly PipelineFixture _fx;
    public ClipGenerationTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Full_pipeline_generates_clips_and_scenes_show_complete()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var projectName = "Clips_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl,
                projectName, "tell_tale_heart.fountain");

            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var scenes = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            var clips = int.Parse(await status.GetAttributeAsync("data-clip-count") ?? "0");
            var onDisk = int.Parse(await status.GetAttributeAsync("data-clips-on-disk") ?? "0");
            var complete = int.Parse(await status.GetAttributeAsync("data-scenes-complete") ?? "0");

            Assert.True(scenes >= 1, $"expected scenes, got {scenes}");
            Assert.True(clips >= 1, $"expected planned clips, got {clips}");
            // Clip generation ran: every planned clip is recorded on disk and every scene complete.
            Assert.True(onDisk == clips, $"expected all {clips} clips generated, got {onDisk} on disk");
            Assert.True(complete == scenes, $"expected all {scenes} scenes complete, got {complete}");

            // The first scene's badge is the success (generated) style, and the Play control is present.
            var badge = page.GetByTestId("scene-row").First.Locator("span.badge").First;
            await Assertions.Expect(badge).ToHaveClassAsync(new Regex("bg-success", RegexOptions.None, CommonRegex.Timeout), new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("scenes-play-selected")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Verify generated project files on disk
            // Projects are namespaced (projects/{owner}/{slug}); find the folder by slug.
            var projectsRoot = Path.Combine(_fx.WorkspaceRootPath, "projects");
            var projectDir = Directory.Exists(projectsRoot)
                ? Directory.GetDirectories(projectsRoot, projectName, SearchOption.AllDirectories).FirstOrDefault()
                : null;
            Assert.False(projectDir is null, $"Project folder '{projectName}' not found under {projectsRoot}");
            var videoDir = Path.Combine(projectDir!, "assets", "video");
            Assert.True(Directory.Exists(videoDir), $"Expected video directory at {videoDir}");

            // Verify .clip.json sidecar files were created for every generated clip: one per clip across
            // ALL scenes ("data-clip-count" is the whole plan, not scene 1), each naming its scene/clip.
            var sidecarFiles = Directory.GetFiles(videoDir, "scene_*_clip_*.clip.json");
            // The end-credits card renders client-side (canvas → ffmpeg.wasm), so it has no server sidecar.
            Assert.True(sidecarFiles.Length >= clips - 1, $"Expected at least {clips - 1} sidecar manifests (all clips but the client-rendered credits), found {sidecarFiles.Length}");
            var seen = new HashSet<(int Scene, int Clip)>();
            foreach (var f in sidecarFiles)
            {
                using var sidecarDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(f));
                var root = sidecarDoc.RootElement;
                var sc = root.GetProperty("scene").GetInt32();
                var cl = root.GetProperty("clip").GetInt32();
                Assert.True(sc >= 1 && cl >= 1, $"sidecar {Path.GetFileName(f)} has scene {sc} / clip {cl}");
                Assert.True(root.TryGetProperty("duration_seconds", out var dur) && dur.GetDouble() > 0, $"sidecar {Path.GetFileName(f)} has no duration");
                seen.Add((sc, cl));
            }
            Assert.Contains((1, 1), seen);
        }
        finally { await ctx.CloseAsync(); }
    }
}
