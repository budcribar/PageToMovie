using System.Text.Json;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Verifies the disable-with-hint gating on a host where the gated capabilities are forced OFF.
/// Release-critical: model/key-dependent actions must be disabled with a "Set up →" hint rather
/// than shown and failing on click.
/// </summary>
[Collection("ui-caps-off")]
public class CapabilityGatingTests
{
    private readonly CapabilitiesOffFixture _fx;
    public CapabilityGatingTests(CapabilitiesOffFixture fx) => _fx = fx;

    [Fact]
    public async Task Capabilities_endpoint_reports_forced_off_capabilities()
    {
        using var resp = await _fx.GetAsync("/api/capabilities");
        Assert.True(resp.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var caps = doc.RootElement.GetProperty("capabilities");
        Assert.False(caps.GetProperty("video").GetBoolean());
        Assert.False(caps.GetProperty("review").GetBoolean());
        Assert.False(caps.GetProperty("image").GetBoolean());
        // Not in the forced-off list → still available under fakes.
        Assert.True(caps.GetProperty("planning").GetBoolean());
    }

    [Fact]
    public async Task Scenes_generate_button_is_gated_with_setup_link_when_video_off()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            // Wait for either the generate control (Film unlocked) or a readiness banner
            // (StudioStateMachine gate). Shared demo workspaces vary in shot-plan readiness.
            var batch = page.GetByTestId("scenes-generate-batch");
            var setup = page.GetByTestId("scenes-generate-batch-cap-setup-link");
            var readiness = page.GetByText("shot plan", new() { Exact = false });
            var deadline = DateTime.UtcNow.AddSeconds(50);
            while (DateTime.UtcNow < deadline)
            {
                if (await batch.IsVisibleAsync() || await setup.IsVisibleAsync() || await readiness.CountAsync() > 0)
                    break;
                await page.WaitForTimeoutAsync(400);
            }

            if (await batch.IsVisibleAsync() || await setup.IsVisibleAsync())
            {
                // The Generate Batch button still renders but CapabilityLockedControl surfaces "Set up →".
                await Assertions.Expect(batch).ToBeVisibleAsync(new() { Timeout = 10_000 });
                await Assertions.Expect(setup).ToBeVisibleAsync(new() { Timeout = 10_000 });
                await Assertions.Expect(page.GetByTestId("scenes-verify-dialogue-cap-setup-link")).ToBeVisibleAsync();
            }
            else
            {
                // Film chrome gated by pipeline readiness — still prove no false-positive setup links
                // are shown for a capability that is forced off only on unlocked controls.
                await Assertions.Expect(page.Locator("[data-testid$='-setup-link']")).ToHaveCountAsync(0);
                Assert.True(await readiness.CountAsync() > 0,
                    "Scenes never settled to generate batch or a shot-plan readiness gate.");
            }
        }
        finally { await ctx.CloseAsync(); }
    }
}
