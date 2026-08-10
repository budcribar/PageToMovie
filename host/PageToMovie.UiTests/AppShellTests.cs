using System.Text.RegularExpressions;
using Microsoft.Playwright;

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
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Drop a book" })).ToBeVisibleAsync();
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

            // Prefer always-available Estimate + Settings — Film/Cast may be StudioStateMachine-gated
            // (disabled spans without href) when the shared demo project is not cast/shot ready.
            await page.GetByTestId("nav-cost").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/cost"));

            await page.GetByTestId("nav-configuration").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/configuration"));

            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/?$|/$"));
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
