using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Structural depth on the big refactor targets (Configuration/Characters/Review) beyond a bare
/// page-load, so the upcoming component-extraction can be verified as behavior-preserving.
/// </summary>
[Collection("ui")]
public class PageDepthTests
{
    private readonly AppFixture _fx;
    public PageDepthTests(AppFixture fx) => _fx = fx;

    [Fact]
    public async Task Configuration_shows_studio_coverage_rows()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/configuration");
            await Assertions.Expect(page.GetByTestId("settings-back")).ToBeVisibleAsync();
            // The coverage card + rows load after the models fetch — give it room.
            var slow = new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 };
            await Assertions.Expect(page.GetByTestId("studio-coverage-card")).ToBeVisibleAsync(slow);
            // Studio coverage starts open; still open via helper so a later collapse cannot hide rows.
            await Ui.OpenConfigSectionAsync(page, "config-section-coverage");
            foreach (var cap in new[] { "video", "image", "review", "voice" })
                await Assertions.Expect(page.GetByTestId($"coverage-{cap}")).ToBeVisibleAsync(slow);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Characters_page_renders_cast_heading()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/characters");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Cast", Exact = true })).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Review_tab_strip_renders()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");
            // The tab strip renders (Play may be disabled without clips — assert presence, don't click).
            await Assertions.Expect(page.GetByTestId("review-tab-review")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("review-tab-play")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("review-tab-share")).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }
}
