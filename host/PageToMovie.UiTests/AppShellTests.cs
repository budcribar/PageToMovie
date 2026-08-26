using System.Text.RegularExpressions;
using Microsoft.Playwright;

using PageToMovie.Core.Utils;
namespace PageToMovie.UiTests;

[Collection("ui")]
public class AppShellTests
{
    private readonly AppFixture _fx;
    public AppShellTests(AppFixture fx) => _fx = fx;

    [Fact]
    public async Task Home_renders_tagline_projects_and_steps()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Drop pages" })).ToBeVisibleAsync();
            // View-independent entry points (present in both the easy-start and full-studio landings).
            await Assertions.Expect(page.GetByText("Full studio").First).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Left_nav_navigates_between_core_pages()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/");

            await page.GetByTestId("nav-demo").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/demo", RegexOptions.None, CommonRegex.Timeout));

            await page.Locator("a[href='/configuration']").First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/configuration", RegexOptions.None, CommonRegex.Timeout));
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Home_demo_films_heading_is_the_single_gallery_link()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/");

            var heading = page.GetByTestId("home-demo-films-heading");
            await Assertions.Expect(heading).ToHaveCountAsync(1);
            await Assertions.Expect(heading).ToBeVisibleAsync(new() { Timeout = 20_000 });
            await Assertions.Expect(heading).ToHaveAttributeAsync("href", "/demo");
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Demo films", Exact = true })).ToHaveCountAsync(1);
            await Assertions.Expect(page.GetByTestId("home-open-demo-gallery")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("home-demo-films-card")).ToHaveCountAsync(1);

            await heading.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/demo", RegexOptions.None, CommonRegex.Timeout));
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Demo_film_card_has_one_youtube_watch_control()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/demo");

            await Assertions.Expect(page.Locator(".demo-card-title-link")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".demo-yt-open-link")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByText("Watch on YouTube ↗")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".demo-card-chips")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByText(new System.Text.RegularExpressions.Regex("cinematic short film", System.Text.RegularExpressions.RegexOptions.IgnoreCase))).ToHaveCountAsync(0);

            var cards = page.GetByTestId("demo-card");
            var cardCount = await cards.CountAsync();
            for (var i = 0; i < cardCount; i++)
            {
                var card = cards.Nth(i);
                await Assertions.Expect(card.Locator("h2.demo-card-title a")).ToHaveCountAsync(0);

                var watch = card.GetByTestId("demo-watch-link");
                var watchCount = await watch.CountAsync();
                Assert.InRange(watchCount, 0, 1);
                if (watchCount == 1)
                {
                    await Assertions.Expect(watch).ToHaveClassAsync(new Regex("demo-yt-thumb-link"));
                    await Assertions.Expect(watch.Locator(".visually-hidden")).ToHaveTextAsync("Watch on YouTube");
                    var play = watch.GetByTestId("demo-yt-play");
                    await Assertions.Expect(play).ToHaveCountAsync(1);
                    await Assertions.Expect(card.Locator("[data-testid=demo-yt-play]")).ToHaveCountAsync(1);
                }
            }
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Home_has_no_unexpected_console_errors()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var errs = Ui.CollectConsoleErrors(page);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/");
            await page.WaitForTimeoutAsync(1500);
            Assert.True(errs.Unexpected.Count == 0, "Unexpected console errors:\n" + string.Join("\n", errs.Unexpected));
        }
        finally { await ctx.CloseAsync(); }
    }
}
