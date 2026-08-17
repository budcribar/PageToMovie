using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Server-outage prognosis end to end: while every API call answers 502 (a redeploy behind the
/// reverse proxy) the layout shows one "restarting — reconnecting" strip and Home stops at its
/// loading state; once the API answers again the probe notices, the strip disappears and Home
/// re-hydrates on its own — no page reload.
/// </summary>
[Collection("ui")]
public class ServerHealthBannerTests
{
    private readonly AppFixture _fx;
    public ServerHealthBannerTests(AppFixture fx) => _fx = fx;

    private static readonly string[] OutageGlobs = { "**/api/**", "**/health", "**/hubs/**" };

    [Fact]
    public async Task Banner_shows_during_502_outage_and_home_rehydrates_after_recovery()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            // Home's heading is a state-independent readiness marker (works with an empty workspace).
            await page.GotoAsync($"{_fx.BaseUrl}/?admin=1");
            await page.GetByRole(AriaRole.Heading, new() { Name = "Drop pages" })
                      .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
            await Ui.DismissTermsAsync(page);
            var banner = page.GetByTestId("server-health-banner");
            await Assertions.Expect(banner).ToBeHiddenAsync();

            // Leave Home so the next in-app return re-runs its load against the "down" server.
            await page.GetByTestId("nav-demo").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/demo"));

            // Proxy-style outage: every API answer is a bare 502.
            foreach (var g in OutageGlobs)
                await page.RouteAsync(g, r => r.FulfillAsync(new() { Status = 502, ContentType = "text/plain", Body = "Bad Gateway" }));

            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(banner).ToBeVisibleAsync(new() { Timeout = 20_000 });
            await Assertions.Expect(banner).ToHaveAttributeAsync("data-state", "down");
            await Assertions.Expect(banner).ToContainTextAsync("Server is restarting");
            await Assertions.Expect(page.GetByTestId("server-health-elapsed")).ToBeVisibleAsync();

            // Home fell back to its loading card — nothing loaded while the server was down.
            await Assertions.Expect(page.Locator(".home-studio-card").First).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-full-studio-card")).ToBeHiddenAsync();

            // Server back: the /health probe (3s first retry) must clear the banner and Home must
            // reload its projects without a page refresh.
            foreach (var g in OutageGlobs)
                await page.UnrouteAsync(g);

            await Assertions.Expect(banner).ToBeHiddenAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(
                page.Locator("[data-testid='home-full-studio-card'], [data-testid='home-studio-card']").First)
                .ToBeVisibleAsync(new() { Timeout = 20_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
