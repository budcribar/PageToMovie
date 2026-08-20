using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Broad page-load coverage on the normal fakes host (all capabilities available): every major
/// route hydrates its shell without unexpected console errors, and the capability gates do NOT
/// fire when the capability is available (no false "Set up →").
/// </summary>
[Collection("ui")]
public class PageSmokeTests
{
    private readonly AppFixture _fx;
    public PageSmokeTests(AppFixture fx) => _fx = fx;

    [Theory]
    [InlineData("/")]
    [InlineData("/configuration")]
    [InlineData("/characters")]
    [InlineData("/scenes")]
    [InlineData("/review")]
    [InlineData("/cost")]
    [InlineData("/adaptation")]
    [InlineData("/demo")]
    [InlineData("/locations")]
    [InlineData("/dialogue-timing")]
    [InlineData("/simple-revoice")]
    [InlineData("/simple-voice")]
    [InlineData("/cost/breakdown")]
    [InlineData("/account/costs")]
    [InlineData("/about")]
    public async Task Page_hydrates_without_unexpected_console_errors(string route)
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var errs = Ui.CollectConsoleErrors(page);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, route);
            await page.WaitForTimeoutAsync(1500);
            // Shell nav present (GotoAppAsync already waited for it) → the WASM app didn't crash.
            await Assertions.Expect(page.Locator("a[data-testid='nav-studio'], a[href='/']").First).ToBeVisibleAsync();
            Assert.True(errs.Unexpected.Count == 0, $"{route} console errors:\n" + string.Join("\n", errs.Unexpected));
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Gates_do_not_fire_when_capabilities_available()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            // Fakes reports every capability configured → the gate must NOT show a "Set up →" link.
            await Assertions.Expect(page.GetByTestId("scenes-generate-batch-cap-setup-link")).Not.ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("scenes-verify-dialogue-cap-setup-link")).Not.ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }
}
