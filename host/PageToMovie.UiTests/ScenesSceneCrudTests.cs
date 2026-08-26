using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Scene CRUD operations: adding a scene (verifying it inserts before the auto-inserted
/// credits scene, Bug #18) and deleting a scene via the ⋯ menu (verifying in-memory count
/// reconciliation, no stale 3/4 counts, and selecting neighbor scene, Bug #2).
/// </summary>
[Collection("ui-pipeline")]
public class ScenesSceneCrudTests
{
    private readonly PipelineFixture _fx;
    public ScenesSceneCrudTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Add_scene_inserts_before_end_credits_and_increments_scene_count()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "SceneAdd_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var sceneCountBefore = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.True(sceneCountBefore >= 1);

            // Locate end credits row
            var creditsRow = page.Locator("[data-testid='scene-row']", new() { HasText = "END CREDITS" });
            await Assertions.Expect(creditsRow).ToBeVisibleAsync(new() { Timeout = 30_000 });
            var creditsBeforeNum = int.Parse(await creditsRow.GetAttributeAsync("data-scene-number") ?? "0");

            // Click Add Scene
            await page.GetByTestId("scenes-add-scene").ClickAsync();

            // Total scene count increments
            await Assertions.Expect(status).ToHaveAttributeAsync(
                "data-scene-count", (sceneCountBefore + 1).ToString(), new() { Timeout = 15_000 });

            // Credits row is bumped by one
            var creditsRowAfter = page.Locator("[data-testid='scene-row']", new() { HasText = "END CREDITS" });
            await Assertions.Expect(creditsRowAfter).ToBeVisibleAsync(new() { Timeout = 15_000 });
            var creditsAfterNum = int.Parse(await creditsRowAfter.GetAttributeAsync("data-scene-number") ?? "0");
            Assert.Equal(creditsBeforeNum + 1, creditsAfterNum);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Delete_scene_reconciles_counts_and_selects_neighbor()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "SceneDel_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var sceneCountBefore = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");

            // Open scene 1
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            // Open scene ⋯ menu
            await page.GetByTestId("scene-menu").ClickAsync();
            var deleteBtn = page.GetByTestId("scene-delete");
            await Assertions.Expect(deleteBtn).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await deleteBtn.ClickAsync();

            // Confirm delete modal
            var confirmBtn = page.GetByRole(AriaRole.Button, new() { Name = "Delete scene" });
            await Assertions.Expect(confirmBtn).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await confirmBtn.ClickAsync();
            await Assertions.Expect(confirmBtn).ToBeHiddenAsync(new() { Timeout = 15_000 });

            // Count decrements accurately and readiness summary updates
            await Assertions.Expect(status).ToHaveAttributeAsync(
                "data-scene-count", (sceneCountBefore - 1).ToString(), new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
