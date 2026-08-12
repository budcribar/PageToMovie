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
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl,
                "Clips_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

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
        }
        finally { await ctx.CloseAsync(); }
    }
}
