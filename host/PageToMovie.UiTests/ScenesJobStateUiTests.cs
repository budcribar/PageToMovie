using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// End-credits scene state on the Film page (Bug #7): the shot plan appends a credits scene on
/// its own, and that scene opens and lists its clip like any other — the credits card is rendered
/// in the browser, so it must be present without any server job having run for it.
/// </summary>
[Collection("ui-pipeline")]
public class ScenesJobStateUiTests
{
    private readonly PipelineFixture _fx;
    public ScenesJobStateUiTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Credits_scene_is_auto_added_and_opens_with_its_clip()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "CreditsGen_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Planning alone must produce the credits scene — no generate step ran here.
            var creditsRow = page.Locator("[data-testid='scene-row']", new() { HasText = "END CREDITS" });
            await Assertions.Expect(creditsRow).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // It is the last scene, not one dropped in the middle of the story.
            var lastSceneNumber = int.Parse(
                await page.GetByTestId("scene-row").Last.GetAttributeAsync("data-scene-number") ?? "0");
            var creditsNumber = int.Parse(await creditsRow.GetAttributeAsync("data-scene-number") ?? "0");
            Assert.Equal(lastSceneNumber, creditsNumber);

            await creditsRow.Locator("span.badge").First.ClickAsync();

            // The scene detail opens and lists the credits clip.
            await Assertions.Expect(page.GetByTestId("clip-expander-1"))
                .ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
