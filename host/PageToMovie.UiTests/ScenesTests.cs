using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

[Collection("ui")]
public class ScenesTests
{
    private readonly AppFixture _fx;
    public ScenesTests(AppFixture fx) => _fx = fx;

    [Fact]
    public async Task Renders_toolbar_controls()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Film", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Generate Batch" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Add scene" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Regenerate Selected Scenes" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Filters" })).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Filters_button_toggles_panel()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            var panel = page.Locator("[data-testid='scenes-select-panel']");
            await Assertions.Expect(panel).ToBeHiddenAsync();
            await page.Locator("[data-testid='scenes-select-toggle']").ClickAsync();
            await Assertions.Expect(panel).ToBeVisibleAsync();
            await Assertions.Expect(panel.GetByText("Setting text")).ToBeVisibleAsync();
            // Collapse via the panel's Hide button.
            await page.Locator("[data-testid='scenes-filters-collapse']").ClickAsync();
            await Assertions.Expect(panel).ToBeHiddenAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Scenes_page_has_no_unexpected_console_errors()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var errs = Ui.CollectConsoleErrors(page);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await page.WaitForTimeoutAsync(1500);
            Assert.True(errs.Unexpected.Count == 0, "Unexpected console errors:\n" + string.Join("\n", errs.Unexpected));
        }
        finally { await ctx.CloseAsync(); }
    }
}
