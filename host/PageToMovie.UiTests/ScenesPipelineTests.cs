using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Extends the fresh-project pipeline through Shot plan (Stage 2) to the Scenes page — the biggest
/// refactor target (Scenes.razor is ~5.2k lines). Proves the shot plan builds from an extracted cast
/// and the Scenes list renders, so the upcoming component extraction can be verified behaviour-preserving.
/// </summary>
[Collection("ui-pipeline")]
public class ScenesPipelineTests
{
    private readonly PipelineFixture _fx;
    public ScenesPipelineTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Fresh_project_builds_shot_plan_and_renders_scenes()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, "Scenes_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Shot plan produced scenes — the authoritative count marker must show a non-empty plan,
            // and the post-load "No shot plan yet" empty card must be gone (first-paint loading
            // uses "Loading scenes…", never that empty copy).
            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var sceneCount = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.True(sceneCount >= 1, $"expected >=1 scene in the shot plan, got {sceneCount}");

            // The scene table renders rows (virtualized — assert at least the first is present) and the
            // batch-generate control is on the page.
            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("scenes-generate-batch")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    // Varied casts through to a built shot plan. With maxSpeakersPerClip=1 (Grok/fake-video), every
    // speaker gets its own single-speaker clip, so the plan must contain at least one clip per
    // distinct speaker — this catches ensemble/animal/large-cast shot-plan regressions and proves the
    // single-speaker policy holds end-to-end (not just in the unit test).
    [Theory]
    [InlineData("solo.fountain", 1)]           // 1 speaker
    [InlineData("crowd_only.fountain", 3)]     // 3 ensemble speakers (Crowd/Children/Villagers)
    [InlineData("jungle_animals.fountain", 3)] // 3 talking (Wolf/Owl/Ranger) + 1 silent animal
    [InlineData("large_cast.fountain", 8)]     // 8 distinct speakers
    public async Task Varied_cast_builds_shot_plan_with_one_clip_per_speaker(string fixture, int minSpeakers)
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, "SV_" + Guid.NewGuid().ToString("N")[..6], fixture);

            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var scenes = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            var clips = int.Parse(await status.GetAttributeAsync("data-clip-count") ?? "0");

            Assert.True(scenes >= 1, $"{fixture}: expected >=1 scene, got {scenes}");
            // One clip per speaker minimum (single-speaker policy — no two-hander merging).
            Assert.True(clips >= minSpeakers, $"{fixture}: expected >={minSpeakers} clips (one per speaker), got {clips}");
            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    // Bug: "Add scene" appended the new blank scene AFTER the auto-inserted end-credits scene (it
    // just took max scene_number + 1, blind to credits) — a stray clip landed past "The End" and
    // credits had to be deleted/re-added to fix it. Fixed in ProjectStore.AddScene to insert before
    // an existing credits scene instead, bumping credits back up by one so it stays last.
    [Fact]
    public async Task Add_scene_inserts_before_the_auto_inserted_credits_scene()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, "AddScene_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var before = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.True(before >= 1, $"expected a built shot plan, got {before} scenes");

            // Stage 2 always auto-inserts an end-credits scene as the last scene of a built plan.
            var creditsRow = page.Locator("[data-testid='scene-row']", new() { HasText = "END CREDITS" });
            await Assertions.Expect(creditsRow).ToBeVisibleAsync(new() { Timeout = 30_000 });
            var creditsBefore = int.Parse(await creditsRow.GetAttributeAsync("data-scene-number") ?? "0");

            await page.GetByTestId("scenes-add-scene").ClickAsync();
            await Assertions.Expect(status).ToHaveAttributeAsync(
                "data-scene-count", (before + 1).ToString(), new() { Timeout = 15_000 });

            // Credits must have been bumped up by exactly one — the new scene took its old slot.
            var creditsRowAfter = page.Locator("[data-testid='scene-row']", new() { HasText = "END CREDITS" });
            await Assertions.Expect(creditsRowAfter).ToBeVisibleAsync(new() { Timeout = 15_000 });
            var creditsAfter = int.Parse(await creditsRowAfter.GetAttributeAsync("data-scene-number") ?? "0");
            Assert.Equal(creditsBefore + 1, creditsAfter);

            // And credits must still be the highest scene number in the plan — still last, never a
            // stray scene generated/rendered after it.
            var rendered = await page.Locator("[data-testid='scene-row']")
                .EvaluateAllAsync<int[]>("els => els.map(e => parseInt(e.getAttribute('data-scene-number')))");
            Assert.Equal(rendered.Max(), creditsAfter);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Opening_a_scene_shows_its_clip_select_bar()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, "SceneOpen_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
            // Open the first scene (click its S## badge) → the per-scene clip select bar appears.
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("clip-select-all")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    // The "AI Edit" button/prompt-modal flow (xAI /v1/videos/edits) end-to-end through the real
    // click path — a full generated project, open scene → open clip → AI Edit → submit prompt →
    // job completes → the clip shows a new take. Proves the UI wiring (button, modal, job-queue
    // start, live-progress completion handling), on top of the server-side job pipeline already
    // covered by VideoEditApiTests.cs.
    [Fact]
    public async Task AI_edit_button_opens_prompt_and_saves_a_new_take_on_completion()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(
                page, _fx.BaseUrl, "VideoEdit_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Open the first scene (the row's own click handler opens the clip detail table). Note:
            // "clip-select-bar" only renders when a clip is missing or multi-selected — with every
            // clip already generated, it never appears, so this doesn't wait on it.
            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("scene-row").First.Locator("span.badge").First.ClickAsync();

            // Scene 1's clip 1 in this fixture is ~10s (over the 8.7s edit cap) — clip 2 (~5s) is
            // the one guaranteed eligible, so this exercises the real button rather than guessing.
            // The inspector follows the clip-table row selection (not the Play button), so select
            // clip 2 by clicking its row (the checkbox itself is multi-select and stops propagation).
            var clip2Row = page.GetByTestId("clip-select-2").Locator("xpath=ancestor::tr[1]");
            await Assertions.Expect(clip2Row).ToBeVisibleAsync(new() { Timeout = 30_000 });
            // Click a plain cell (Duration) — the row centre lands on the Clip cell's own buttons
            // (Play / Takes / Delete), which open their modals instead of selecting the row.
            await clip2Row.Locator("td").Nth(2).ClickAsync();

            var editOpen = page.GetByTestId("clip-video-edit-open");
            if (await editOpen.CountAsync() == 0)
            {
                // Diagnostic: what did the detail render after the row click?
                await page.WaitForTimeoutAsync(3_000);
                var dump = await page.EvaluateAsync<string>(@"() => {
                    const ids = [...document.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid'));
                    const main = document.querySelector('main') || document.body;
                    const row = document.querySelector('[data-testid=clip-select-2]')?.closest('tr');
                    return JSON.stringify({rowClass: row?.className, testids: ids.slice(0, 80), text: main.innerText.slice(1200, 3000)});
                }");
                Assert.True(await editOpen.CountAsync() > 0, "clip-video-edit-open not rendered after selecting clip 2: " + dump);
            }
            await Assertions.Expect(editOpen).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(editOpen).ToBeEnabledAsync(new() { Timeout = 15_000 });

            await editOpen.ClickAsync();
            var prompt = page.GetByTestId("clip-video-edit-prompt");
            await Assertions.Expect(prompt).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await prompt.FillAsync("change her jacket to red");

            var goButton = page.GetByTestId("clip-video-edit-go");
            await Assertions.Expect(goButton).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await goButton.ClickAsync();

            // Job runs (job-queue + live progress, not a blocking request) and completes. Server-side
            // archiving/take-provenance is covered in depth by VideoEditApiTests.cs; this confirms
            // the real click path (button → modal → submit → live progress) drives it the same way.
            await Assertions.Expect(page.GetByText("edited — saved as a new take")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            // Not left stuck "busy" — the button is clickable again for a follow-up edit.
            await Assertions.Expect(editOpen).ToBeEnabledAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
