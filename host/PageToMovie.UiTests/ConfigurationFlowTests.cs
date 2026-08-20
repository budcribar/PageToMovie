using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// A-4: depth coverage for the Configuration page's two save paths — the debounced autosave used by
/// the Format/Pipeline-Behavior fields (no bottom Save button), and the immediate save fired by a
/// studio-coverage model/provider change — plus the optional-capability (music/voice) on/off toggle,
/// which is the one place on this page that mutates provider wiring, not just a scalar setting. The
/// upcoming component-extraction must preserve both save paths and the Ready/Off/Need-key badge logic.
/// </summary>
[Collection("ui-pipeline")]
public class ConfigurationFlowTests
{
    private readonly PipelineFixture _fx;
    public ConfigurationFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Debounced_autosave_persists_format_and_pipeline_fields()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl,
                "Config_" + Guid.NewGuid().ToString("N")[..6]);

            // Navigate via the in-app nav link, not a full page load — CreateFreshProjectAsync already
            // booted the WASM shell and established the admin-bypass session on this same page, and a
            // second full reload of "/" to re-establish it (as Ui.GotoAppLoggedInAsync does) is slow
            // enough under load to blow past the 30s shell-ready wait. In-app routing is instant.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await Assertions.Expect(page.GetByTestId("studio-coverage-card"))
                .ToBeVisibleAsync(new() { Timeout = 20_000 });

            // Settings sections start collapsed by design (acf910b5) — open the two we touch.
            await Ui.OpenConfigSectionAsync(page, "config-section-format");
            await Ui.OpenConfigSectionAsync(page, "config-section-pipeline");
            // Change a Format & Resolution field (plain @bind, no testid) and a Pipeline Behavior checkbox.
            var aspect = page.Locator("select").Filter(new() { Has = page.Locator("option[value='9:16']") }).First;
            await aspect.SelectOptionAsync("9:16");

            var qaRetry = page.Locator("#qaRetry");
            var wasChecked = await qaRetry.IsCheckedAsync();
            var expectChecked = !wasChecked;
            await qaRetry.SetCheckedAsync(expectChecked);

            // Debounced save (450ms) fires once and settles on "Saved".
            await Assertions.Expect(page.GetByTestId("settings-autosave-status"))
                .ToHaveTextAsync("· Saved", new() { Timeout = 10_000 });

            // Navigate away and back in-app: OnInitializedAsync re-fetches _cfg from the server, so this
            // proves the values round-tripped through the server rather than just surviving in client state.
            await page.GetByTestId("nav-studio").ClickAsync();
            await page.GetByTestId("nav-configuration").ClickAsync();
            await Assertions.Expect(page.GetByTestId("studio-coverage-card"))
                .ToBeVisibleAsync(new() { Timeout = 20_000 });
            await Ui.OpenConfigSectionAsync(page, "config-section-format");
            await Ui.OpenConfigSectionAsync(page, "config-section-pipeline");
            var aspectAfterReload = page.Locator("select").Filter(new() { Has = page.Locator("option[value='9:16']") }).First;
            await Assertions.Expect(aspectAfterReload).ToHaveValueAsync("9:16");
            await Assertions.Expect(page.Locator("#qaRetry")).ToBeCheckedAsync(new() { Checked = expectChecked });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Optional_capability_toggle_updates_ready_badge_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl,
                "ConfigMusic_" + Guid.NewGuid().ToString("N")[..6]);

            // In-app nav, not a full reload — see the comment in the autosave test for why.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await Ui.OpenConfigSectionAsync(page, "config-section-coverage");
            var musicRow = page.GetByTestId("coverage-music");
            await Assertions.Expect(musicRow).ToBeVisibleAsync(new() { Timeout = 20_000 });

            // Fresh project: background music defaults off ("Off" badge, no Turn-off button yet).
            await Assertions.Expect(musicRow).ToContainTextAsync("Off");

            // Pick the fake provider, then its model — this is the only pair of controls on the page
            // that wires a new provider, not just flips a scalar setting.
            await page.GetByTestId("coverage-provider-music").SelectOptionAsync(new SelectOptionValue { Value = "fake" });
            await page.GetByTestId("coverage-model-music").SelectOptionAsync("fake-music");

            // OnCoverageModelChangedAsync saves immediately (no debounce) — badge flips to Ready.
            await Assertions.Expect(musicRow.Locator(".badge")).ToHaveTextAsync("Ready", new() { Timeout = 15_000 });

            // Navigate away and back — the provider/model choice round-tripped, not just in-memory state.
            await page.GetByTestId("nav-studio").ClickAsync();
            await page.GetByTestId("nav-configuration").ClickAsync();
            await Ui.OpenConfigSectionAsync(page, "config-section-coverage");
            musicRow = page.GetByTestId("coverage-music");
            await Assertions.Expect(musicRow.Locator(".badge")).ToHaveTextAsync("Ready", new() { Timeout = 20_000 });
            await Assertions.Expect(page.GetByTestId("coverage-model-music")).ToHaveValueAsync("fake-music");

            // Turn it back off — scoped to this row: "voice" can independently carry a "Turn off"
            // button too (e.g. leftover state from another test sharing this fixture's admin account),
            // so an unscoped role lookup is a strict-mode violation waiting to happen.
            await musicRow.GetByRole(AriaRole.Button, new() { Name = "Turn off" }).ClickAsync();
            await Assertions.Expect(musicRow.Locator(".badge")).ToHaveTextAsync("Off", new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Studio_coverage_starts_open_and_header_toggles_keys_table()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl,
                "ConfigKeys_" + Guid.NewGuid().ToString("N")[..6]);

            await page.GetByTestId("nav-configuration").ClickAsync();
            await Assertions.Expect(page.GetByTestId("studio-coverage-card"))
                .ToBeVisibleAsync(new() { Timeout = 20_000 });

            var keysTable = page.GetByTestId("capability-coverage");
            await Assertions.Expect(keysTable).ToBeVisibleAsync(new() { Timeout = 20_000 });
            // Wait until the first coverage row is painted so the initial catalog load
            // cannot re-apply <details open> and undo the collapse click below.
            await Assertions.Expect(page.GetByTestId("coverage-video"))
                .ToBeVisibleAsync(new() { Timeout = 20_000 });

            var header = page.GetByTestId("config-section-coverage");
            await header.ClickAsync();
            await Assertions.Expect(keysTable).ToBeHiddenAsync(new() { Timeout = 10_000 });

            await header.ClickAsync();
            await Assertions.Expect(keysTable).ToBeVisibleAsync(new() { Timeout = 10_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Add_or_replace_key_opens_paste_panel()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl,
                "ConfigPaste_" + Guid.NewGuid().ToString("N")[..6]);

            await page.GetByTestId("nav-configuration").ClickAsync();
            await Assertions.Expect(page.GetByTestId("capability-coverage"))
                .ToBeVisibleAsync(new() { Timeout = 20_000 });

            var videoRow = page.GetByTestId("coverage-video");
            await Assertions.Expect(videoRow).ToBeVisibleAsync(new() { Timeout = 20_000 });

            var addKey = page.GetByTestId("coverage-add-key-video");
            var replaceKey = page.GetByTestId("coverage-change-key-video");
            if (await addKey.CountAsync() > 0)
                await addKey.ClickAsync();
            else if (await replaceKey.CountAsync() > 0)
                await replaceKey.ClickAsync();
            else
                await page.GetByTestId("coverage-add-provider-video").ClickAsync();

            await Assertions.Expect(page.GetByTestId("coverage-key-panel-video"))
                .ToBeVisibleAsync(new() { Timeout = 10_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
