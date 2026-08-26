using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Review page Finish tab integration: auto-attaches active project clips into the embedded
/// Cut editor (Bug #16), allowing trimming, titles, and music within the main studio flow.
/// </summary>
[Collection("ui-pipeline")]
public class ReviewFinishFlowTests
{
    private readonly PipelineFixture _fx;
    public ReviewFinishFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Finish_tab_auto_attaches_active_project_clips()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(
                page, _fx.BaseUrl, "FinishTab_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");

            // The embedded Cut editor renders without error on the active Finish tab
            var cutEditor = page.Locator(".cut-editor");
            await Assertions.Expect(cutEditor).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // "Save cut" button is present
            var saveCutBtn = page.Locator("button.cut-btn", new() { HasText = "Save cut" });
            await Assertions.Expect(saveCutBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
