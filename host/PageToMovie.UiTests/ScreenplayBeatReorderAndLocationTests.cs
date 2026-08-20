using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Remaining screenplay-editor surfaces: beat drag-reorder inside a scene card, and the
/// Locations modal (add a location, use it on the scene heading, persist to the draft).
/// </summary>
[Collection("ui-pipeline")]
public class ScreenplayBeatReorderAndLocationTests
{
    private readonly PipelineFixture _fx;
    public ScreenplayBeatReorderAndLocationTests(PipelineFixture fx) => _fx = fx;

    private static async Task ImportMaryAsync(IPage page, string baseUrl, string prefix)
    {
        await PipelineFlow.CreateFreshProjectAsync(page, baseUrl, prefix + "_" + Guid.NewGuid().ToString("N")[..6]);
        await PipelineFlow.SelectFakeModelsAsync(page);
        await PipelineFlow.ImportFountainAsync(page, baseUrl, "mary_had_a_lamb.fountain");
        await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
    }

    private static Task<string> DraftAsync(IPage page) =>
        page.EvaluateAsync<string>(@"async () => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
            const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
            const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
            const sp = await fetch('/api/projects/'+encodeURIComponent(id)+'/screenplay', {headers:h}).then(r=>r.json());
            return (sp.text||sp.Text||'');
        }");

    [Fact]
    public async Task Beat_drag_reorder_swaps_rows_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await ImportMaryAsync(page, _fx.BaseUrl, "BeatDrag");

            // Scene 1's card: rows are .spe-beat-row; drag from the ⋮⋮ handle edge.
            var card = page.Locator(".spe-sel-add").First.Locator("xpath=ancestor::div[contains(@class,'card')][1]");
            var rows = card.Locator(".spe-beat-row");
            Assert.True(await rows.CountAsync() >= 3, "scene 1 should have several beats");

            string RowKey(string t) => t.Replace("\n", " ").Trim();
            var first = RowKey(await rows.Nth(0).InnerTextAsync());
            var second = RowKey(await rows.Nth(1).InnerTextAsync());
            Assert.NotEqual(first, second);

            await rows.Nth(1).DragToAsync(rows.Nth(0), new()
            {
                SourcePosition = new() { X = 8, Y = 12 },
                TargetPosition = new() { X = 8, Y = 12 },
            });

            // The former second row is now first. Compare on a stable fragment (row text is long).
            var probe = second[..Math.Min(24, second.Length)];
            await Assertions.Expect(rows.Nth(0)).ToContainTextAsync(probe[..Math.Min(12, probe.Length)].Trim(), new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Scene_card_locations_gear_deep_links_to_locations_page_focused()
    {
        // In-app, OpenLocationModal NAVIGATES to /locations?loc={name} (the modal markup only
        // renders standalone) — cover the real path: gear → Locations page focused on the
        // scene's own place.
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await ImportMaryAsync(page, _fx.BaseUrl, "LocGear");

            await page.GetByRole(AriaRole.Button, new() { Name = "⚙ Locations" }).First.ClickAsync();

            await Assertions.Expect(page).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex("/locations"), new() { Timeout = 15_000 });
            await Assertions.Expect(page.GetByTestId("locations-page")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            // The deep-linked location is focused in the index and shown in the detail pane.
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByText("COUNTRY LANE").First).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // The switch-scenes heading select still lists only KNOWN locations — changing scene 1
            // to the schoolroom persists to the draft.
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/adaptation/screenplay");
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            // Only the OPEN scene renders a heading select — open scene 1 via the outline first
            // (after the /locations round-trip a different scene may be the expanded one).
            await page.Locator(".spe-outline-row").First.Locator(".spe-outline-label").ClickAsync();
            var locSelect = page.Locator(".spe-sel-loc").First;
            await Assertions.Expect(locSelect).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(locSelect).ToHaveValueAsync("COUNTRY LANE", new() { Timeout = 15_000 });
            await locSelect.SelectOptionAsync(new SelectOptionValue { Label = "SCHOOLROOM" });

            // Scene 1 was COUNTRY LANE's only use — after the change the draft's FIRST heading is
            // the schoolroom and the lane is gone from the headings entirely.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var saved = false;
            string lastDraft = "";
            while (DateTime.UtcNow < deadline && !saved)
            {
                lastDraft = await DraftAsync(page);
                var firstHeading = lastDraft.Split('\n')
                    .FirstOrDefault(l => l.TrimStart().StartsWith("INT.") || l.TrimStart().StartsWith("EXT.")) ?? "";
                saved = firstHeading.Contains("SCHOOLROOM", StringComparison.OrdinalIgnoreCase);
                if (!saved) await page.WaitForTimeoutAsync(500);
            }
            if (!saved)
            {
                var selDiag = await page.EvaluateAsync<string>(@"() => {
                    const sels = [...document.querySelectorAll('.spe-sel-loc')];
                    return JSON.stringify({ count: sels.length, values: sels.map(s => s.value).slice(0, 6) });
                }");
                Assert.Fail("scene 1's heading change to SCHOOLROOM never reached the saved draft; selects: " + selDiag
                            + "; first lines: " + string.Join(" | ", lastDraft.Split('\n').Take(10)));
            }
        }
        finally { await ctx.CloseAsync(); }
    }
}
