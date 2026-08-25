using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Final campaign items: picking a character voice through the real panel controls, the remaining
/// Configuration fields (theme + external editor), the simple-revoice page's main card, and the
/// Review Finish tab hosting the cut editor.
/// </summary>
[Collection("ui-pipeline")]
public class RemainingCoverageTests
{
    private readonly PipelineFixture _fx;
    public RemainingCoverageTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Characters_voice_profile_edit_via_panel_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl,
                "VoicePick_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            await Assertions.Expect(page.GetByTestId("char-list-item").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.EvaluateAsync(
                "() => [...document.querySelectorAll('[data-testid=char-list-item]')].find(b => /narrator/i.test(b.textContent))?.click()");
            // The narrator arrives with a seeded profile, so the Voice card is FOLDED — open it.
            var voiceCard = page.GetByTestId("char-voice-card");
            await Assertions.Expect(voiceCard).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await voiceCard.Locator(".char-panel-toggle").ClickAsync();
            var panel = page.GetByTestId("char-voice-panel");
            await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Type a pinned profile through the real textarea — it autosaves.
            const string profile = "Gravelly elderly male storyteller voice, slow and kind.";
            await panel.Locator("textarea").FillAsync(profile);
            await panel.Locator("textarea").BlurAsync();

            var deadline = DateTime.UtcNow.AddSeconds(15);
            var saved = false;
            while (DateTime.UtcNow < deadline && !saved)
            {
                var chars = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                    const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                    return JSON.stringify(await fetch('/api/projects/'+encodeURIComponent(id)+'/characters', {headers:h}).then(r=>r.json()));
                }");
                saved = chars.Contains("Gravelly elderly male storyteller", StringComparison.OrdinalIgnoreCase);
                if (!saved) await page.WaitForTimeoutAsync(500);
            }
            Assert.True(saved, "the voice profile typed into the panel never persisted to the characters API");
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Configuration_theme_and_external_editor_persist_across_reload()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "CfgApp_" + Guid.NewGuid().ToString("N")[..6]);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/configuration");
            await Ui.OpenConfigSectionAsync(page, "config-section-appearance");

            await page.GetByTestId("config-theme").SelectOptionAsync("light");
            var editorBox = page.Locator("input[list='video-editor-suggestions']");
            await editorBox.FillAsync("DaVinci Resolve");
            await editorBox.BlurAsync();
            await page.WaitForTimeoutAsync(2_500); // debounced autosave

            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Ui.OpenConfigSectionAsync(page, "config-section-appearance");
            await Assertions.Expect(page.GetByTestId("config-theme")).ToHaveValueAsync("light", new() { Timeout = 30_000 });
            await Assertions.Expect(page.Locator("input[list='video-editor-suggestions']"))
                .ToHaveValueAsync("DaVinci Resolve", new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Simple_revoice_page_shows_main_card_on_generated_project()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl,
                "Revoice_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/simple-revoice");
            try
            {
                await Assertions.Expect(page.GetByTestId("simple-revoice-card")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            }
            catch (PlaywrightException)
            {
                var diag = await page.EvaluateAsync<string>(@"() => JSON.stringify({
                    card: !!document.querySelector('[data-testid=simple-revoice-card]'),
                    url: location.pathname,
                    text: (document.body.innerText || '').replace(/\n+/g, ' | ').slice(0, 1200),
                })");
                Assert.Fail("simple-revoice card missing. " + diag);
            }
            await Assertions.Expect(page.GetByTestId("simple-revoice-error")).ToHaveCountAsync(0);
            // No clone sample was captured in this pipeline, so the card must show the truthful
            // "record your voice first" hand-off (the start button only exists once a clone exists).
            await Assertions.Expect(
                page.GetByTestId("simple-revoice-start")
                    .Or(page.GetByRole(AriaRole.Link, new() { Name = "← Record your voice" })).First)
                .ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Finish_tab_hosts_the_cut_editor()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl,
                "Finish_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");
            await PipelineFlow.MakeCastReadyForShotsAsync(page);
            await PipelineFlow.BuildShotPlanAsync(page);
            await PipelineFlow.GenerateClipsAsync(page);

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");
            var finishTab = page.GetByTestId("review-tab-finish");
            await Assertions.Expect(finishTab).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await finishTab.ClickAsync();
            await Assertions.Expect(page.GetByTestId("cut-timeline")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
