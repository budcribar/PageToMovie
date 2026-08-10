using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Broad page-load coverage on the normal fakes host (all capabilities available): every major
/// route hydrates its shell without unexpected console errors.
/// Capability "Set up →" false-positive checks live in <see cref="CapabilityGatingTests"/> (caps off)
/// and <see cref="StudioProcessStripTests"/> / pipeline (caps on after a real shot plan).
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
    public async Task Page_hydrates_without_unexpected_console_errors(string route)
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var errs = Ui.CollectConsoleErrors(page);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, route);
            await page.WaitForTimeoutAsync(1500);
            // Shell nav present — Film may be a disabled span when StudioStateMachine gates it.
            await Assertions.Expect(Ui.ShellReady(page)).ToBeVisibleAsync();
            Assert.True(errs.Unexpected.Count == 0, $"{route} console errors:\n" + string.Join("\n", errs.Unexpected));
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Capability_setup_links_absent_on_film_when_fakes_configured()
    {
        // Shared demo workspaces can leave /scenes stuck on "Loading…" (missing assets, etc.).
        // Assert only that after the shell is up, no capability "Set up →" links appear — the
        // inverse of CapabilityGatingTests on the caps-off host. StudioStateMachine readiness
        // banners are allowed (project may not be cast/shot ready).
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await page.WaitForTimeoutAsync(2500);
            await Assertions.Expect(page.GetByTestId("scenes-generate-batch-cap-setup-link")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("scenes-verify-dialogue-cap-setup-link")).ToHaveCountAsync(0);
            // Any capability setup affordance would use the *-setup-link suffix.
            await Assertions.Expect(page.Locator("[data-testid$='-setup-link']")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }
}
