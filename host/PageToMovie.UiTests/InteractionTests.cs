using System.Text.RegularExpressions;
using Microsoft.Playwright;

using PageToMovie.Core.Utils;
namespace PageToMovie.UiTests;

/// <summary>
/// Click-through interaction flows on the normal fakes host: selection state, modal open/cancel,
/// and the account menu. These exercise the @code wiring the component-extraction refactor will
/// move, so they guard behavior beyond "the page rendered".
/// </summary>
[Collection("ui")]
public class InteractionTests
{
    private readonly AppFixture _fx;
    public InteractionTests(AppFixture fx) => _fx = fx;

    [Fact]
    public async Task Selecting_all_scenes_updates_the_generate_button_label()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            var generate = page.GetByTestId("scenes-generate-batch");
            await Assertions.Expect(generate).ToContainTextAsync("Batch");

            await page.GetByTestId("scenes-select-all").CheckAsync();
            await Assertions.Expect(generate).ToContainTextAsync(new Regex(@"Generate \d+ scene", RegexOptions.None, CommonRegex.Timeout));

            await page.GetByTestId("scenes-select-all").UncheckAsync();
            await Assertions.Expect(generate).ToContainTextAsync("Batch");
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Generate_confirm_offers_scope_radio_and_regenerate_toolbar_is_gone()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await Assertions.Expect(page.GetByTestId("scenes-regenerate-selected")).ToHaveCountAsync(0);

            await page.GetByTestId("scenes-select-all").CheckAsync();
            await page.GetByTestId("scenes-generate-batch").ClickAsync();

            var modal = page.GetByTestId("generate-confirm-modal");
            await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 15_000 });
            var missing = page.GetByTestId("generate-confirm-scope-missing");
            var allTakes = page.GetByTestId("generate-confirm-scope-all");
            await Assertions.Expect(missing).ToBeVisibleAsync();
            await Assertions.Expect(allTakes).ToBeVisibleAsync();
            // Seeded project usually has every clip; then Missing is disabled and All is the default.
            if (await missing.IsDisabledAsync())
                await Assertions.Expect(allTakes).ToBeCheckedAsync();
            else
                await Assertions.Expect(missing).ToBeCheckedAsync();
            await Assertions.Expect(page.GetByTestId("generate-confirm-resolution")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("generate-confirm-cost")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("generate-confirm-go")).ToBeEnabledAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Delete_scene_modal_opens_and_cancels_without_deleting()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            // Delete scene lives in the scene header's ⋯ menu now (rare action).
            await page.GetByTestId("scene-menu").ClickAsync();
            await page.GetByTestId("scene-delete").ClickAsync();

            // Confirm button carries the exact label "Delete Scene" (the 🗑️ triggers are "Delete scene S..").
            var confirm = page.GetByRole(AriaRole.Button, new() { Name = "Delete Scene", Exact = true });
            await Assertions.Expect(confirm).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(confirm).ToBeHiddenAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Account_menu_opens_and_closes()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/");
            await page.GetByTestId("nav-user-menu").ClickAsync();
            await Assertions.Expect(page.GetByTestId("nav-user-configuration")).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Close account menu" }).ClickAsync();
            await Assertions.Expect(page.GetByTestId("nav-user-configuration")).ToBeHiddenAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Opening_a_scene_reveals_its_clip_detail()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            // Click the first scene row's badge → opens its detail card (the "+ Add clip" ghost row
            // is always present once a scene's detail is loaded, unlike clip-select-bar which only
            // renders when a clip is missing on disk or checked).
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();
            // The detail header keeps Play/Regen/Verify; rarer actions (Screenplay drawer, Score,
            // deletes) fold into the ⋯ menu — open it to reach the drawer toggle.
            await Assertions.Expect(page.GetByTestId("scene-menu")).ToBeVisibleAsync();
            await page.GetByTestId("scene-menu").ClickAsync();
            await Assertions.Expect(page.GetByTestId("toggle-fountain-drawer")).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Applying_a_character_filter_reveals_clear_filters()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await page.GetByTestId("scenes-select-toggle").ClickAsync();
            var panel = page.Locator("[data-testid='scenes-select-panel']");
            await Assertions.Expect(panel).ToBeVisibleAsync();

            // First select in the panel is Character; index 0 is "All characters…", 1 is a real one.
            await panel.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Clear filters" })).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }
}
