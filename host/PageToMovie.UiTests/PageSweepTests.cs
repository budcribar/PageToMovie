using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Depth sweep for pages the suite barely touched: the Locations page (index, plate/variants,
/// unused-filter), the Dialogue-Timing admin page (scene pick → analyze), and the Screenplay
/// editor's outline drag-reorder. Mary fixture throughout.
/// </summary>
[Collection("ui-pipeline")]
public class PageSweepTests
{
    private readonly PipelineFixture _fx;
    public PageSweepTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Locations_and_dialogue_timing_pages_work_on_a_generated_project()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl,
                "Sweep_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            // ---- Locations: index lists the Mary locations; selecting one shows its plate state ----
            var console = new List<string>();
            page.Console += (_, msg) => { if (msg.Type is "error" or "warning") console.Add($"{msg.Type}: {msg.Text}"); };
            page.PageError += (_, err) => console.Add("pageerror: " + err);
            var net = new List<string>();
            page.Request += (_, r) => { if (r.Url.Contains("locations")) net.Add("REQ " + r.Method + " " + r.Url); };
            page.Response += (_, r) => { if (r.Url.Contains("locations")) net.Add("RSP " + r.Status + " " + r.Url); };
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/locations");
            await Assertions.Expect(page.GetByTestId("locations-page")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            var items = page.GetByTestId("loc-list-item");
            try
            {
                await Assertions.Expect(items.First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            }
            catch (PlaywrightException)
            {
                var diag = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                    const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                    const t0 = Date.now();
                    let locs = null, status = 0;
                    try {
                        const r = await Promise.race([
                            fetch('/api/projects/'+encodeURIComponent(id)+'/locations', {headers:h}),
                            new Promise((_, rej)=>setTimeout(()=>rej(new Error('endpoint>15s')), 15000)),
                        ]);
                        status = r.status; locs = (await r.text()).slice(0, 400);
                    } catch (e) { locs = ''+e; }
                    return JSON.stringify({
                        endpointMs: Date.now()-t0, status, locs,
                        err: document.querySelector('[data-testid=locations-error]')?.innerText || null,
                        text: (document.querySelector('[data-testid=locations-page]')?.innerText || '').slice(0, 300),
                    });
                }");
                Assert.Fail("Locations index rendered no items. Page: " + diag +
                            " | console: " + string.Join(" || ", console.TakeLast(10)) +
                            " | net: " + string.Join(" || ", net.TakeLast(10)));
            }
            Assert.True(await items.CountAsync() >= 2, "Mary has at least the lane and the schoolroom");

            await items.First.ClickAsync();
            // Pipeline locks plates for shots: locked plate, or (if this location is unlocked)
            // the empty-state + generate control must be present — never neither.
            var lockedPlate = page.GetByTestId("loc-locked-plate");
            var noPlate = page.GetByTestId("loc-no-plate-yet");
            await Assertions.Expect(lockedPlate.Or(noPlate).First).ToBeVisibleAsync(new() { Timeout = 15_000 });
            if (await noPlate.CountAsync() > 0)
                await Assertions.Expect(page.GetByTestId("loc-generate-looks")).ToBeVisibleAsync();

            // The unused-filter toggle flips without error.
            var toggle = page.GetByTestId("loc-toggle-unused");
            if (await toggle.CountAsync() > 0)
            {
                await toggle.ClickAsync();
                await Assertions.Expect(page.GetByTestId("locations-error")).ToHaveCountAsync(0);
                await toggle.ClickAsync();
            }

            // ---- Dialogue timing (admin): pick scene 1, analyze, no error surface ----
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/dialogue-timing");
            var sceneSel = page.GetByTestId("dt-scene");
            await Assertions.Expect(sceneSel).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await sceneSel.SelectOptionAsync("1");
            var analyze = page.GetByTestId("dt-analyze");
            await Assertions.Expect(analyze).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await analyze.ClickAsync();
            await Assertions.Expect(analyze).ToBeEnabledAsync(new() { Timeout = 120_000 }); // busy → done
            await Assertions.Expect(page.GetByTestId("dt-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Screenplay_outline_drag_with_a_shot_plan_routes_through_the_renumber_engine()
    {
        // B8: once a shot plan exists, a text-only reorder would desync blueprint + clip files —
        // the outline drag must go through the same renumber engine as the Film page's drag.
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl,
                "SpDragPlan_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/adaptation/screenplay");
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var rows = page.Locator(".spe-outline-row");
            await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 30_000 });

            await rows.Nth(1).DragToAsync(rows.Nth(0));

            // The BLUEPRINT (not just the text) now has the schoolroom as scene 1 — proof the
            // renumber engine ran rather than a local model move.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var swapped = false;
            string last = "";
            while (DateTime.UtcNow < deadline && !swapped)
            {
                last = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                    const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                    const list = await fetch('/api/projects/'+encodeURIComponent(id)+'/scenes', {headers:h}).then(r=>r.json());
                    const scenes = (list.scenes||list.Scenes||[]);
                    return scenes.map(x => (x.sceneNumber ?? x.SceneNumber) + ':' + (x.setting ?? x.Setting ?? '')).join(' | ');
                }");
                swapped = last.StartsWith("1:INT. SCHOOLROOM", StringComparison.OrdinalIgnoreCase);
                if (!swapped) await page.WaitForTimeoutAsync(750);
            }
            Assert.True(swapped, "blueprint scene 1 never became the schoolroom after the outline drag; scenes: " + last);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Screenplay_outline_drag_reorders_scenes_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "SpDrag_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "mary_had_a_lamb.fountain");

            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var rows = page.Locator(".spe-outline-row");
            await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            Assert.True(await rows.CountAsync() >= 4, "Mary outline should list its scenes");

            var first = (await rows.Nth(0).InnerTextAsync()).Trim();
            var second = (await rows.Nth(1).InnerTextAsync()).Trim();
            Assert.NotEqual(first, second);

            // Record drag events so a silent non-drag is diagnosable.
            await page.EvaluateAsync(@"() => {
                window.__dragLog = [];
                for (const ev of ['dragstart','dragover','drop','dragend'])
                    document.addEventListener(ev, e => window.__dragLog.push(ev + ':' + (e.target.className || e.target.tagName).toString().slice(0, 40)), true);
            }");
            await rows.Nth(1).DragToAsync(rows.Nth(0));
            var dragLog = await page.EvaluateAsync<string[]>("() => window.__dragLog.slice(0, 12)");

            // The former second scene is now first (labels renumber, so compare the heading text).
            string HeadingOf(string rowText) => rowText.Split('\n')[0].TrimStart('1', '2', '3', '4', '5', ' ').Trim();
            try
            {
                await Assertions.Expect(rows.Nth(0)).ToContainTextAsync(HeadingOf(second)[..Math.Min(12, HeadingOf(second).Length)], new() { Timeout = 15_000 });
            }
            catch (PlaywrightException)
            {
                Assert.Fail("Outline drag did not reorder. Drag events seen: [" + string.Join(", ", dragLog) + "]");
            }

            // Wait for the debounced autosave (900ms) to persist the reorder server-side before
            // reloading — poll the draft itself rather than sleeping.
            var saveDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < saveDeadline)
            {
                var draft = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                    const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                    const sp = await fetch('/api/projects/'+encodeURIComponent(id)+'/screenplay', {headers:h}).then(r=>r.json());
                    return (sp.text||sp.Text||'');
                }");
                var a = draft.IndexOf("INT. SCHOOLROOM", StringComparison.Ordinal);
                var b = draft.IndexOf("EXT. COUNTRY LANE", StringComparison.Ordinal);
                if (a >= 0 && b >= 0 && a < b) break;
                await page.WaitForTimeoutAsync(500);
            }

            // Persisted: a full reload shows the same order.
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await Assertions.Expect(page.Locator(".spe-outline-row").Nth(0))
                .ToContainTextAsync(HeadingOf(second)[..Math.Min(12, HeadingOf(second).Length)], new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
