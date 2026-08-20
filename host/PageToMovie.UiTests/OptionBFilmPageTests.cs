using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// The Option B film page: clip rows expand in place (no separate inspector panel), one status
/// chip per row, an "+ Add clip" ghost row, rare actions behind the scene ⋯ menu, and
/// drag-and-drop reorder of clips and scenes (renumber-on-drop, server-side file renames).
/// Fresh pipeline project per test — reorder mutates project files, so no shared-state reuse.
/// </summary>
[Collection("ui-pipeline")]
public class OptionBFilmPageTests
{
    private readonly PipelineFixture _fx;
    public OptionBFilmPageTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Clip_row_expands_in_place_with_actions_add_clip_row_and_scene_menu()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, "OptB_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            // Expander chip toggles the in-place expansion (there is no separate inspector below).
            var expander = page.GetByTestId("clip-expander-1");
            await Assertions.Expect(expander).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await expander.ClickAsync();
            var expansion = page.GetByTestId("clip-expansion");
            await Assertions.Expect(expansion).ToBeVisibleAsync(new() { Timeout = 15_000 });
            // The single action bar: AI Edit Video / Takes / Edit Clip Script (no per-clip Regen —
            // single-clip regen is "check it + header Regen (1)").
            await Assertions.Expect(page.GetByTestId("clip-video-edit-open")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("clip-edit-line")).ToBeVisibleAsync();
            await Assertions.Expect(expansion.GetByText("Regen clip")).ToHaveCountAsync(0);
            await expander.ClickAsync();
            await Assertions.Expect(expansion).ToBeHiddenAsync(new() { Timeout = 15_000 });

            // "+ Add clip" ghost row opens the add-clip editor.
            var addClip = page.GetByTestId("clip-add");
            await Assertions.Expect(addClip).ToBeVisibleAsync();
            await addClip.ClickAsync();
            await Assertions.Expect(page.GetByText("Add clip", new() { Exact = false }).First).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).First.ClickAsync();

            // The ⋯ menu holds the rare scene actions, including both deletes.
            await page.GetByTestId("scene-menu").ClickAsync();
            await Assertions.Expect(page.GetByTestId("scene-delete")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("scene-delete-selected-clips")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("scene-delete-selected-clips")).ToBeDisabledAsync(); // nothing checked
            await Assertions.Expect(page.GetByTestId("toggle-fountain-drawer")).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Dragging_a_clip_row_reorders_and_renumbers_the_scene()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, "OptBDrag_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            var row1 = page.GetByTestId("clip-expander-1").Locator("xpath=ancestor::tr[1]");
            var row2 = page.GetByTestId("clip-expander-2").Locator("xpath=ancestor::tr[1]");
            await Assertions.Expect(row2).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var line1Before = await row1.Locator("td.clip-dialogue-cell").InnerTextAsync();
            var line2Before = await row2.Locator("td.clip-dialogue-cell").InnerTextAsync();
            Assert.NotEqual(line1Before, line2Before); // fixture scenes have distinct beats

            await row2.DragToAsync(row1);

            // After the renumber pass, C01 carries what used to be C02's beat (and vice versa).
            await Assertions.Expect(page.GetByTestId("clip-expander-1").Locator("xpath=ancestor::tr[1]")
                .Locator("td.clip-dialogue-cell")).ToHaveTextAsync(line2Before.Trim(), new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("clip-expander-2").Locator("xpath=ancestor::tr[1]")
                .Locator("td.clip-dialogue-cell")).ToHaveTextAsync(line1Before.Trim(), new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Dragging_a_scene_row_reorders_and_renumbers_the_film()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, "OptBScene_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            var scene1 = page.Locator("[data-testid='scene-row'][data-scene-number='1']");
            var scene2 = page.Locator("[data-testid='scene-row'][data-scene-number='2']");
            await Assertions.Expect(scene1).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(scene2).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var setting1Before = await scene1.Locator(".text-truncate").InnerTextAsync();
            var setting2Before = await scene2.Locator(".text-truncate").InnerTextAsync();
            Assert.NotEqual(setting1Before, setting2Before);

            await scene2.DragToAsync(scene1);

            // Renumber: the scene that WAS S02 is now S01 (screenplay chunks moved with it).
            await Assertions.Expect(page.Locator("[data-testid='scene-row'][data-scene-number='1']")
                .Locator(".text-truncate")).ToHaveTextAsync(setting2Before.Trim(), new() { Timeout = 30_000 });
            await Assertions.Expect(page.Locator("[data-testid='scene-row'][data-scene-number='2']")
                .Locator(".text-truncate")).ToHaveTextAsync(setting1Before.Trim(), new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
