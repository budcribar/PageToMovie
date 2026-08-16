using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// The regular-user experience (admin "view as user"). This is the surface real users see,
/// where product rules apply (no admin detail / provider jargon), so it's the highest-value
/// place to catch UI bugs.
/// </summary>
[Collection("ui")]
public class UserModeTests
{
    private readonly AppFixture _fx;
    public UserModeTests(AppFixture fx) => _fx = fx;

    [Fact]
    public async Task View_as_user_hides_admin_surfaces()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/");
            // Admin nav is present in real-admin mode.
            await Assertions.Expect(page.Locator("a[href='/admin']").First).ToBeVisibleAsync();

            await Ui.EnterUserModeAsync(page);

            // Admin-only nav entries disappear once viewing as a regular user.
            await Assertions.Expect(page.Locator("a[href='/admin']")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task User_mode_scenes_page_renders_and_is_jargon_free()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var errs = Ui.CollectConsoleErrors(page);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/");
            await Ui.EnterUserModeAsync(page);

            // Navigate via the in-app link so the client-side user-mode state persists.
            await page.Locator("a[href='/scenes']").First.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Film", Exact = true })).ToBeVisibleAsync();

            // Product rule: user-facing pages must not leak provider/model jargon.
            var body = await page.EvalOnSelectorAsync<string>("body", "el => el.innerText");
            foreach (var banned in new[] { "Grok", "Gemini", "xAI", "Veo", "blueprint.clips" })
                Assert.DoesNotContain(banned, body, System.StringComparison.OrdinalIgnoreCase);

            Assert.True(errs.Unexpected.Count == 0, "Unexpected console errors:\n" + string.Join("\n", errs.Unexpected));
        }
        finally { await ctx.CloseAsync(); }
    }
}
