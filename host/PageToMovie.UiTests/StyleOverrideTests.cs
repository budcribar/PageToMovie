using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// The portrait style gate is overridable: when the classifier says a look's medium doesn't match the
/// project, the creator can still lock it ("Use this look anyway") — a photoreal character in an
/// animated film, or vice versa. Runs on a host with the fake style gate forced to reject.
/// </summary>
[Collection("ui-style-reject")]
public class StyleOverrideTests
{
    private readonly StyleRejectFixture _fx;
    public StyleOverrideTests(StyleRejectFixture fx) => _fx = fx;

    [Fact]
    public async Task Style_rejected_look_can_be_locked_with_override()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl, "Override_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Select a character and generate looks (fake image) → the pick grid appears.
            await Assertions.Expect(page.GetByTestId("char-list-item").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.GetByTestId("char-list-item").First.ClickAsync();
            await page.GetByTestId("char-route-generate").ClickAsync(new() { Timeout = 30_000 });
            var desc = page.GetByTestId("char-look-panel").Locator("textarea").First;
            await desc.WaitForAsync(new() { Timeout = 30_000 });
            if (string.IsNullOrWhiteSpace(await desc.InputValueAsync()))
                await desc.FillAsync("A pale, thin adult with dark hair and a dark wool coat, photoreal.");
            await page.GetByTestId("char-generate-looks").ClickAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("char-pick-card").First).ToBeVisibleAsync(new() { Timeout = 90_000 });

            // Try to lock a look — the (forced-reject) style gate blocks it and the override panel
            // appears with the three reason choices (two-step: a reason is required to proceed).
            await page.GetByTestId("char-use-look").First.ClickAsync();
            var overridePanel = page.GetByTestId("char-style-override");
            await Assertions.Expect(overridePanel).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("char-override-ai-wrong")).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(page.GetByTestId("char-override-preference")).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(page.GetByTestId("char-override-other")).ToBeVisibleAsync(new() { Timeout = 10_000 });

            // Pick "my creative choice" → the look locks despite the mismatch; the panel clears.
            await page.GetByTestId("char-override-preference").ClickAsync();
            await Assertions.Expect(overridePanel).ToHaveCountAsync(0, new() { Timeout = 30_000 });

            // The override (with its reason) is recorded in the AI-call telemetry — the feedback loop.
            // The lock-variant response returns only once the override write has been awaited
            // server-side, so this is not a write-ordering race — but a *separate* subsequent HTTP
            // request re-reading the just-written file can still observe it a beat late (measured
            // ~800ms under test load), so poll briefly rather than asserting on the very first read.
            var found = false;
            string lastPayload = "";
            for (var i = 0; i < 8 && !found; i++)
            {
                await page.WaitForTimeoutAsync(500);
                lastPayload = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const resp = await fetch('/api/admin/ai-calls', {headers:h});
                    let an = null; try { an = await resp.json(); } catch {}
                    const d = (an||{}).data || {};
                    const op = (d.ops||[]).find(o => o.op === 'style_gate_override');
                    return JSON.stringify({found: !!op, calls: op ? op.calls : 0,
                        total: d.totalCalls, ops: (d.ops||[]).map(o=>o.op).slice(0,15)});
                }");
                found = lastPayload.Contains("\"found\":true");
            }
            Assert.True(found, "style_gate_override not in analytics after polling. last payload: " + lastPayload);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Dismissing_the_override_keeps_the_ai_verdict()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl, "Dismiss_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Assertions.Expect(page.GetByTestId("char-list-item").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.GetByTestId("char-list-item").First.ClickAsync();
            await page.GetByTestId("char-route-generate").ClickAsync(new() { Timeout = 30_000 });
            var desc = page.GetByTestId("char-look-panel").Locator("textarea").First;
            await desc.WaitForAsync(new() { Timeout = 30_000 });
            if (string.IsNullOrWhiteSpace(await desc.InputValueAsync()))
                await desc.FillAsync("A pale, thin adult with dark hair, photoreal.");
            await page.GetByTestId("char-generate-looks").ClickAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("char-pick-card").First).ToBeVisibleAsync(new() { Timeout = 90_000 });

            await page.GetByTestId("char-use-look").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("char-style-override")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Dismiss (the X) — the verdict stands: panel gone, nothing locked, no crash.
            await page.GetByTestId("char-style-override-dismiss").ClickAsync();
            await Assertions.Expect(page.GetByTestId("char-style-override")).ToHaveCountAsync(0, new() { Timeout = 10_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
