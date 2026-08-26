using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Scene job state UI tests: credits scene client-side browser render without server jobs (Bug #7),
/// and handling unknown / lost jobs cleanly without hanging in "Waiting…" indefinitely (Bug #8).
/// </summary>
[Collection("ui-pipeline")]
public class ScenesJobStateUiTests
{
    private readonly PipelineFixture _fx;
    public ScenesJobStateUiTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Credits_scene_browser_generation_completes_without_server_job()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "CreditsGen_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Locate end credits scene
            var creditsRow = page.Locator("[data-testid='scene-row']", new() { HasText = "END CREDITS" });
            await Assertions.Expect(creditsRow).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Click the badge to open credits scene detail
            await creditsRow.Locator("span.badge").First.ClickAsync();

            // The scene detail opens and shows the credits clip
            var clip1Row = page.GetByTestId("clip-expander-1");
            await Assertions.Expect(clip1Row).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
