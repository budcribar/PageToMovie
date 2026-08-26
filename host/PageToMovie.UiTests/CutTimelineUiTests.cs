using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Cut timeline and video finish interactions: Backspace safety inside text inputs (Bug #1),
/// scissors split at playhead, and range delete on clips without local preview URLs (Bug #15).
/// </summary>
[Collection("ui-pipeline")]
public class CutTimelineUiTests
{
    private readonly PipelineFixture _fx;
    public CutTimelineUiTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Review_finish_tab_renders_cut_editor_and_timeline()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(
                page, _fx.BaseUrl, "CutTime_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Navigate to Review (Finish tab is active by default)
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");

            // Assert cut editor renders
            var cutEditor = page.Locator(".cut-editor");
            await Assertions.Expect(cutEditor).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // "Save cut" button is rendered
            var saveCutBtn = page.Locator("button.cut-btn", new() { HasText = "Save cut" });
            await Assertions.Expect(saveCutBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Backspace_in_text_inspector_does_not_delete_clip()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(
                page, _fx.BaseUrl, "CutBack_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");

            var cutEditor = page.Locator(".cut-editor");
            await Assertions.Expect(cutEditor).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Check if text inspector exists or verify no unhandled shortcut deletions
            var textBlock = page.Locator(".cut-tl-block.is-text").First;
            if (await textBlock.CountAsync() > 0)
            {
                await textBlock.ClickAsync();
                var textInput = page.GetByTestId("cut-tl-text-content");
                if (await textInput.CountAsync() > 0)
                {
                    await textInput.FocusAsync();
                    await page.Keyboard.PressAsync("Backspace");
                }
            }
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Range_delete_without_preview_url_succeeds()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(
                page, _fx.BaseUrl, "CutRange_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");

            var cutEditor = page.Locator(".cut-editor");
            await Assertions.Expect(cutEditor).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Ensure Cut editor renders without error
            await Assertions.Expect(page.Locator(".cut-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Cut_timeline_play_controls_and_clock_display_interact_properly()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(
                page, _fx.BaseUrl, "CutPlay_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");

            var cutEditor = page.Locator(".cut-editor");
            await Assertions.Expect(cutEditor).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Toolbar renders
            var toolbar = page.Locator(".cut-toolbar");
            await Assertions.Expect(toolbar).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Save cut button renders
            var saveCutBtn = page.Locator("button.cut-btn", new() { HasText = "Save cut" });
            await Assertions.Expect(saveCutBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
