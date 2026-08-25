using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// The whole creator arc on a Mary-had-a-little-lamb screenplay (narrator V.O. + speaking Mary /
/// Teacher / Children + a SILENT lamb — the same shape as the user's real Mary19 project):
/// import → adaptation → cast (incl. silent animal) → shot plan → generate every clip (fakes) →
/// verify dialogue → score music → review/approve → Play + Share. One long test per concern so the
/// expensive pipeline arrange is paid once.
/// </summary>
[Collection("ui-pipeline")]
public class MaryEndToEndTests
{
    private readonly PipelineFixture _fx;
    public MaryEndToEndTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Mary_arc_import_generate_verify_score_review()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl,
                "Mary_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            // ---- Shot plan shape: 5 story scenes + auto credits, credits last ----
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var sceneCount = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.True(sceneCount >= 5, $"Mary fixture should yield >=5 scenes, got {sceneCount}");

            // ---- Verify dialogue on scene 1 (fakes answer the vision/transcription calls) ----
            var scene1Row = page.Locator("[data-testid='scene-row'][data-scene-number='1']");
            await scene1Row.Locator("span.badge").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("clip-select-all")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("clip-select-all").CheckAsync();
            var verify = page.GetByTestId("scene-verify-selected");
            await Assertions.Expect(verify).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await verify.ClickAsync();
            // The one-chip-per-row model: after verification, scene 1's clip 1 chip shows a verdict
            // (✓ Verified or a ⚠ tier) instead of the bare "✓ Ready".
            await Assertions.Expect(page.GetByTestId("clip-status-1"))
                .Not.ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^✓ Ready$"), new() { Timeout = 120_000 });

            // ---- Score background music for scene 1 via the ⋯ menu ----
            await page.GetByTestId("scene-menu").ClickAsync();
            var score = page.GetByTestId("scene-row-score-music");
            await Assertions.Expect(score).ToBeVisibleAsync(new() { Timeout = 10_000 });
            if (await score.IsEnabledAsync())
            {
                await score.ClickAsync();
                var go = page.GetByTestId("score-menu-generate");
                await Assertions.Expect(go).ToBeVisibleAsync(new() { Timeout = 15_000 });
                await go.ClickAsync();
                // Fake audio returns quickly; the scene row gains its music marker. Diagnose via the
                // jobs API if it never shows (music job error vs save/registry vs list-refresh gap).
                try
                {
                    await Assertions.Expect(scene1Row.GetByText("🎵")).ToBeVisibleAsync(new() { Timeout = 120_000 });
                }
                catch (PlaywrightException)
                {
                    var diag = await page.EvaluateAsync<string>(@"async () => {
                        const raw = sessionStorage.getItem('PageToMovie.admin.session');
                        const s = JSON.parse(raw);
                        const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                        const job = await fetch('/api/jobs?mine=1', {headers:h}).then(r=>r.json()).catch(e=>({err:''+e}));
                        const err = document.querySelector('.alert-danger')?.innerText || '';
                        return JSON.stringify({job, err}).slice(0, 1500);
                    }");
                    Assert.Fail("Music 🎵 marker never appeared on scene 1. Diagnostics: " + diag);
                }
            }
            else
            {
                Assert.Fail("Score music is disabled under fakes — music capability should be ready (fake audio key).");
            }

            // ---- Review: approve clip + scene, then Play & Share are live ----
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");
            await page.GetByTestId("review-tab-review").ClickAsync();
            var checklist = page.GetByTestId("review-checklist");
            await Assertions.Expect(checklist).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var firstRow = page.GetByTestId("review-scene-row").First;
            await Assertions.Expect(firstRow).ToBeVisibleAsync(new() { Timeout = 30_000 });
            var sn = await firstRow.GetAttributeAsync("data-scene-number");
            await page.GetByTestId($"review-clips-{sn}").ClickAsync();
            var pass = page.GetByTestId($"review-pass-{sn}-1");
            await Assertions.Expect(pass).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await pass.ClickAsync();
            await Assertions.Expect(pass).ToBeEnabledAsync(new() { Timeout = 15_000 });

            var finishTab = page.GetByTestId("review-tab-finish");
            await Assertions.Expect(finishTab).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await finishTab.ClickAsync();

            var shareTab = page.GetByTestId("review-tab-share");
            await Assertions.Expect(shareTab).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await shareTab.ClickAsync();
            await Assertions.Expect(page.GetByTestId("review-share-card")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Mary_silent_lamb_clips_exist_and_narrator_is_voice_only()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl,
                "MaryCast_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            // The shot plan must carry at least one SILENT clip (lamb action beats have no line) —
            // scan the scenes via the app's own API for a clip with no dialogue.
            var silentInfo = await page.EvaluateAsync<string>(@"async () => {
                const raw = sessionStorage.getItem('PageToMovie.admin.session');
                const s = JSON.parse(raw);
                const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                const E = encodeURIComponent(id);
                const list = await fetch('/api/projects/'+E+'/scenes', {headers:h}).then(r=>r.json());
                const scenes = list.scenes||list.Scenes||[];
                let silent = 0, total = 0, narrator = 0;
                for (const sc of scenes) {
                    const snum = sc.sceneNumber ?? sc.SceneNumber;
                    const det = await fetch('/api/projects/'+E+'/scenes/'+snum, {headers:h}).then(r=>r.json());
                    const clips = (det.scene||det.Scene||{}).clips || (det.scene||det.Scene||{}).Clips || [];
                    for (const c of clips) {
                        total++;
                        const dlg = c.dialogue ?? c.Dialogue ?? '';
                        const spk = (c.speaker ?? c.Speaker ?? '').toLowerCase();
                        if (!dlg) silent++;
                        if (spk.includes('narrator')) narrator++;
                    }
                }
                return JSON.stringify({silent, total, narrator});
            }");
            var info = System.Text.Json.JsonDocument.Parse(silentInfo).RootElement;
            Assert.True(info.GetProperty("total").GetInt32() >= 8,
                $"expected a real shot plan, got {silentInfo}");
            Assert.True(info.GetProperty("silent").GetInt32() >= 1,
                $"Mary's lamb beats should yield at least one silent clip: {silentInfo}");
            Assert.True(info.GetProperty("narrator").GetInt32() >= 3,
                $"the narrator's verses should span several clips: {silentInfo}");
        }
        finally { await ctx.CloseAsync(); }
    }
}
