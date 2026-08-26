using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Clip CRUD and field editor depth: adding a clip via "+ Add clip…", validating fields
/// (prompt required, duration bounds, dialogue/speaker rules), modifying prompts in structured
/// vs raw mode (Bug #20), and deleting clips via the ⋯ menu with immediate count decrements.
/// </summary>
[Collection("ui-pipeline")]
public class ScenesClipCrudTests
{
    private readonly PipelineFixture _fx;
    public ScenesClipCrudTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Add_clip_validates_required_fields_and_increments_clip_count()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "ClipAdd_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Open scene 1
            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            var status = page.GetByTestId("scenes-status");
            var clipCountBefore = int.Parse(await status.GetAttributeAsync("data-clip-count") ?? "0");

            // Click "+ Add clip…"
            var addBtn = page.GetByTestId("clip-add");
            await Assertions.Expect(addBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await addBtn.ClickAsync();

            var modal = page.GetByTestId("clip-editor-modal");
            await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(page.GetByTestId("clip-editor-title")).ToContainTextAsync("Add clip");

            // Try saving empty (Visual prompt required)
            var saveBtn = page.GetByTestId("clip-editor-save");
            await saveBtn.ClickAsync();
            await Assertions.Expect(modal.GetByText("Visual prompt is required")).ToBeVisibleAsync(new() { Timeout = 10_000 });

            // Switch to raw prompt mode and enter visual prompt
            var rawToggle = page.GetByTestId("clip-prompt-raw-toggle");
            await rawToggle.ClickAsync();
            var rawInput = page.GetByTestId("clip-prompt-raw");
            await Assertions.Expect(rawInput).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await rawInput.FillAsync("CLOSE UP — Old man's pale blue vulture eye staring in horror.");

            // Enter dialogue without speaker -> validation error
            var dlgInput = page.GetByTestId("clip-editor-dialogue");
            await dlgInput.FillAsync("Who is there?");
            await saveBtn.ClickAsync();
            await Assertions.Expect(modal.GetByText("Dialogue needs a speaker")).ToBeVisibleAsync(new() { Timeout = 10_000 });

            // Clear dialogue so it's a valid silent clip
            await dlgInput.FillAsync("");

            // Save valid clip
            await saveBtn.ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync(new() { Timeout = 20_000 });

            // Clip count must increment immediately without full page reload
            await Assertions.Expect(status).ToHaveAttributeAsync(
                "data-clip-count", (clipCountBefore + 1).ToString(), new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Delete_selected_clips_removes_rows_and_decrements_count()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "ClipDel_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            var status = page.GetByTestId("scenes-status");
            var clipCountBefore = int.Parse(await status.GetAttributeAsync("data-clip-count") ?? "0");

            // Select clip 1 in scene 1
            var clip1Checkbox = page.GetByTestId("clip-select-1");
            await Assertions.Expect(clip1Checkbox).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await clip1Checkbox.CheckAsync();

            // Open scene ⋯ menu
            await page.GetByTestId("scene-menu").ClickAsync();
            var deleteSelectedBtn = page.GetByTestId("scene-delete-selected-clips");
            await Assertions.Expect(deleteSelectedBtn).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(deleteSelectedBtn).ToBeEnabledAsync();
            await deleteSelectedBtn.ClickAsync();

            // Confirm delete modal
            var confirmBtn = page.Locator("[data-testid='clip-delete-confirm'], [data-testid='clips-delete-confirm']");
            await Assertions.Expect(confirmBtn).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await confirmBtn.ClickAsync();
            await Assertions.Expect(confirmBtn).ToBeHiddenAsync(new() { Timeout = 15_000 });

            // Clip count decrements immediately
            await Assertions.Expect(status).ToHaveAttributeAsync(
                "data-clip-count", (clipCountBefore - 1).ToString(), new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Clip_field_editor_retains_changes_across_raw_toggle()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "PromptToggle_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            var addBtn = page.GetByTestId("clip-add");
            await Assertions.Expect(addBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await addBtn.ClickAsync();

            var rawToggle = page.GetByTestId("clip-prompt-raw-toggle");
            await rawToggle.ClickAsync();

            var rawInput = page.GetByTestId("clip-prompt-raw");
            await Assertions.Expect(rawInput).ToBeVisibleAsync(new() { Timeout = 10_000 });
            const string testPrompt = "WIDE SHOT — Midnight in the lantern-lit bedroom.";
            await rawInput.FillAsync(testPrompt);

            // Toggle back to structured fields and then back to raw
            await rawToggle.ClickAsync();
            await rawToggle.ClickAsync();

            // Value remains preserved
            await Assertions.Expect(rawInput).ToHaveValueAsync(testPrompt);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Edit_clip_script_updates_prompt_and_persists_across_reload()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "ClipEdit_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Open scene 1
            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            // Click C01 expand button to open clip inspector
            var expandC01Btn = page.Locator("button", new() { HasText = "C01" }).First;
            await Assertions.Expect(expandC01Btn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await expandC01Btn.ClickAsync();

            // Click Edit Clip Script
            var editBtn = page.GetByTestId("clip-edit-line");
            await Assertions.Expect(editBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await editBtn.ClickAsync();

            var modal = page.GetByTestId("clip-editor-modal");
            await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Switch to raw prompt mode
            var rawToggle = page.GetByTestId("clip-prompt-raw-toggle");
            await rawToggle.ClickAsync();
            var rawInput = page.GetByTestId("clip-prompt-raw");
            await Assertions.Expect(rawInput).ToBeVisibleAsync(new() { Timeout = 10_000 });

            const string modifiedPrompt = "EXTREME CLOSE-UP — Single flickering candle flame reflects in wide paranoid eyes.";
            await rawInput.FillAsync(modifiedPrompt);

            // Save edits
            var saveBtn = page.GetByTestId("clip-editor-save");
            await saveBtn.ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync(new() { Timeout = 20_000 });

            // Reload page to verify persistence in shot plan
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            expandC01Btn = page.Locator("button", new() { HasText = "C01" }).First;
            await Assertions.Expect(expandC01Btn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await expandC01Btn.ClickAsync();

            editBtn = page.GetByTestId("clip-edit-line");
            await Assertions.Expect(editBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await editBtn.ClickAsync();

            modal = page.GetByTestId("clip-editor-modal");
            await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 15_000 });
            rawToggle = page.GetByTestId("clip-prompt-raw-toggle");
            await rawToggle.ClickAsync();
            rawInput = page.GetByTestId("clip-prompt-raw");
            await Assertions.Expect(rawInput).ToHaveValueAsync(modifiedPrompt, new() { Timeout = 10_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
