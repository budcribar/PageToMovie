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
            // The Generate Batch button still renders (data-testid preserved) but is wrapped in
            // CapabilityLockedControl, which — with video off — surfaces the "Set up →" deep link.
            await Assertions.Expect(page.GetByTestId("scenes-generate-batch")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("scenes-generate-batch-cap-setup-link")).ToBeVisibleAsync();
            // Verify Scene (video review) is gated the same way (review forced off).
            await Assertions.Expect(page.GetByTestId("scenes-verify-dialogue-cap-setup-link")).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }
}
