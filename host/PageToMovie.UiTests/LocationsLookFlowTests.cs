using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Locations page depth: generate 3 looks (fake image provider), pick a variant with the lock
/// tile, switch the preferred look — plus the Configuration page's media-folder connect flow
/// (the OPFS stub stands in for the real directory picker).
/// </summary>
[Collection("ui-pipeline")]
public class LocationsLookFlowTests
{
    private readonly PipelineFixture _fx;
    public LocationsLookFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Generate_looks_then_lock_and_switch_preferred_variant()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl,
                "LocLook_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");
            await PipelineFlow.MakeCastReadyForShotsAsync(page);

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/locations");
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();

            // Generate 3 looks for the selected location (fake image provider answers instantly).
            var generate = page.GetByTestId("loc-generate-looks");
            await Assertions.Expect(generate).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await generate.ClickAsync();

            // Variant tiles appear once the job lands.
            await Assertions.Expect(page.GetByTestId("loc-lock-v1")).ToBeVisibleAsync(new() { Timeout = 120_000 });
            await Assertions.Expect(page.GetByTestId("loc-lock-v2")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Lock look #2 → its tile becomes the preferred one.
            await page.GetByTestId("loc-lock-v2").ClickAsync();
            await Assertions.Expect(
                page.Locator(".loc-variant-tile.is-preferred").Filter(new() { HasText = "#2" }))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Switch to look #1 — the preference follows.
            await page.GetByTestId("loc-lock-v1").ClickAsync();
            await Assertions.Expect(
                page.Locator(".loc-variant-tile.is-preferred").Filter(new() { HasText = "#1" }))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("locations-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Configuration_media_folder_connects_and_reports_name()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "CfgMedia_" + Guid.NewGuid().ToString("N")[..6]);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/configuration");
            await Ui.OpenConfigSectionAsync(page, "config-section-storage"); // media folder lives here

            var connect = page.GetByTestId("config-select-media-folder").First;
            await Assertions.Expect(connect).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await connect.ClickAsync();

            // The OPFS stub connects silently; the page shows the connected folder's name.
            // (Re-render can collapse the <details> section — reopen before asserting.)
            await Ui.OpenConfigSectionAsync(page, "config-section-storage");
            await Assertions.Expect(page.GetByTestId("config-media-folder-name")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(page.GetByTestId("config-media-folder-name")).ToContainTextAsync("TestMediaFolder");
        }
        finally { await ctx.CloseAsync(); }
    }
}
