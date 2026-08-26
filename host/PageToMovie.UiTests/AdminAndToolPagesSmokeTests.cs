using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Smoke coverage for the admin surfaces (behind the <c>?admin=1</c> dev bypass), the account /
/// cost / locations / about pages, and the voice tools — each page hydrates without unexpected
/// console errors, does NOT bounce to a "sign in" gate, shows its key control, and where there is a
/// cheap primary action (refresh) it works against the fakes host.
/// </summary>
[Collection("ui")]
public class AdminAndToolPagesSmokeTests
{
    private readonly AppFixture _fx;
    public AdminAndToolPagesSmokeTests(AppFixture fx) => _fx = fx;

    /// <summary>route → (heading text | null, testid of a key control | null)</summary>
    [Theory]
    [InlineData("/admin", "Admin dashboard", "admin-link-ai-calls")]
    [InlineData("/admin/users", "Users & credits", null)]
    [InlineData("/admin/config", "Server configuration", "admin-config-masonry")]
    [InlineData("/admin/models-catalog", "Models catalog manager", null)]
    [InlineData("/admin/learning", "Learning", "learning-project-filter")]
    [InlineData("/admin/book-cache", null, null)]
    [InlineData("/admin/demos", "Demo gallery", null)]
    [InlineData("/admin/ai-calls", "AI calls", "ai-calls-refresh")]
    [InlineData("/admin/generation-errors", "Generation errors", "gen-errors-refresh")]
    public async Task Admin_page_hydrates_for_admin_and_shows_its_key_control(string route, string? heading, string? keyControl)
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var errs = Ui.CollectConsoleErrors(page);
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, route);
            await page.WaitForTimeoutAsync(1500);

            // The dev bypass makes the session an admin: no "Admin session required" gate.
            await Assertions.Expect(page.GetByText("Admin session required")).ToHaveCountAsync(0);
            if (heading is not null)
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading }).First).ToBeVisibleAsync(new() { Timeout = 20_000 });
            else
                await Assertions.Expect(page.Locator("h1").First).ToBeVisibleAsync(new() { Timeout = 20_000 });
            if (keyControl is not null)
                await Assertions.Expect(page.GetByTestId(keyControl)).ToBeVisibleAsync(new() { Timeout = 20_000 });

            Assert.True(errs.Unexpected.Count == 0, $"{route} console errors:\n" + string.Join("\n", errs.Unexpected));
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Ai_calls_refresh_renders_status_and_either_empty_or_ops_table()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/admin/ai-calls");
            await page.GetByTestId("ai-calls-refresh").ClickAsync();
            var status = page.GetByTestId("ai-calls-status");
            await status.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });
            var total = int.Parse(await status.GetAttributeAsync("data-total") ?? "0");
            if (total == 0)
                await Assertions.Expect(page.GetByTestId("ai-calls-empty")).ToBeVisibleAsync();
            else
                await Assertions.Expect(page.GetByTestId("ai-calls-ops")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("ai-calls-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Generation_errors_refresh_renders_empty_or_table()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/admin/generation-errors");
            await page.GetByTestId("gen-errors-refresh").ClickAsync();
            var either = page.GetByTestId("gen-errors-empty").Or(page.GetByTestId("gen-errors-table"));
            await Assertions.Expect(either.First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("gen-errors-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Theory]
    [InlineData("/dialogue-timing", "Dialogue timing", "dt-project")]
    [InlineData("/simple-voice", null, null)]
    [InlineData("/simple-revoice", "Your voice on the film", null)]
    [InlineData("/account/costs", null, "account-back")]
    [InlineData("/cost", "", "cost-page")]
    [InlineData("/locations", "Locations", "locations-page")]
    [InlineData("/about", null, null)]
    public async Task Tool_page_hydrates_and_shows_its_key_control(string route, string? heading, string? keyControl)
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var errs = Ui.CollectConsoleErrors(page);
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, route);
            await page.WaitForTimeoutAsync(1500);
            // heading: text → that heading; null → any h1; "" → page has no h1 (key control only).
            if (!string.IsNullOrEmpty(heading))
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading }).First).ToBeVisibleAsync(new() { Timeout = 20_000 });
            else if (heading is null)
                await Assertions.Expect(page.Locator("h1").First).ToBeVisibleAsync(new() { Timeout = 20_000 });
            if (keyControl is not null)
                await Assertions.Expect(page.GetByTestId(keyControl)).ToBeVisibleAsync(new() { Timeout = 30_000 });
            Assert.True(errs.Unexpected.Count == 0, $"{route} console errors:\n" + string.Join("\n", errs.Unexpected));
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Configuration → API keys: the studio coverage card lists every capability row, and
    /// the product deep link (<c>?focus=video</c> — what the process strip's "no model" badge uses)
    /// opens the just-in-time key panel for that row. Keys are never typed here — the fakes host is
    /// key-free.</summary>
    [Fact]
    public async Task Configuration_focus_deep_link_opens_the_key_panel_for_a_coverage_row()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/configuration?focus=video");
            var slow = new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 };
            await Assertions.Expect(page.GetByTestId("studio-coverage-card")).ToBeVisibleAsync(slow);
            await Assertions.Expect(page.GetByTestId("config-theme")).ToBeAttachedAsync();
            foreach (var cap in new[] { "video", "image", "planning", "voice" })
                await Assertions.Expect(page.GetByTestId($"coverage-{cap}")).ToBeVisibleAsync(slow);
            await Assertions.Expect(page.GetByTestId("coverage-key-panel-video")).ToBeVisibleAsync(slow);
            // The panel offers provider choices (buttons per provider) — the add-key affordance is live.
            await Assertions.Expect(page.Locator("[data-testid^='coverage-pick-'], [data-testid^='coverage-use-saved-'], [data-testid^='coverage-provider-']").First).ToBeVisibleAsync(slow);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Admin_models_catalog_search_and_capability_filter_narrows_table()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/admin/models-catalog");
            await Assertions.Expect(page.GetByTestId("catalog-filters")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var rows = page.Locator("table tbody tr");
            await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 15_000 });
            var unfiltered = await rows.CountAsync();

            var searchInput = page.GetByPlaceholder("Search model ID, name, provider…");
            await Assertions.Expect(searchInput).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await searchInput.FillAsync("fake");

            var capSelect = page.GetByTestId("catalog-filters").Locator("select").First;
            await Assertions.Expect(capSelect).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await capSelect.SelectOptionAsync("Video");

            // "Narrows" is the claim, so assert it: fewer rows than unfiltered, and the one we want.
            await Assertions.Expect(page.Locator("table tbody")).ToContainTextAsync("fake-video");
            var filtered = await rows.CountAsync();
            Assert.True(
                filtered < unfiltered,
                $"search + capability filter did not narrow the catalog: {unfiltered} → {filtered} rows");
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Admin_book_cache_renders_and_refreshes()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/admin/book-cache");
            var header = page.Locator("h1, h2, h3, h4").First;
            await Assertions.Expect(header).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Refresh is the only action this page offers; it must round-trip cleanly.
            var refresh = page.Locator("button", new() { HasText = "Refresh" }).First;
            await Assertions.Expect(refresh).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await refresh.ClickAsync();
            await Assertions.Expect(refresh).ToBeEnabledAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.Locator(".alert-danger")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Admin_server_configuration_renders_with_a_live_save_action()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppLoggedInAsync(page, _fx.BaseUrl, "/admin/config");
            await Assertions.Expect(page.GetByTestId("admin-config-masonry")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Deliberately not clicked: "Save & hot-apply" rewrites SERVER config on the host every
            // other test in this run shares, so a green test here could break an unrelated one.
            var saveBtn = page.Locator("button", new() { HasText = "Save & hot-apply" });
            await Assertions.Expect(saveBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(saveBtn).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator(".alert-danger")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }
}
