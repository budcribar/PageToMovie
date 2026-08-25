using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// A-2: depth coverage for the Review page's core "Review &amp; Approve" workflow (approve/reject a
/// clip, approve a scene, checklist count) plus reachability of the Play/Share tabs — the state the
/// upcoming component-extraction must preserve. Builds on the same generated-clips pipeline as
/// ClipGenerationTests (A-1b).
///
/// Found and fixed a real bug while writing these: the "Scenes &amp; clips" review table (and the
/// job-status block above it) had no closing brace for the Play tab's `else if` in Review.razor —
/// everything from the job-status block onward was silently nested inside `_activeTab == "play"`,
/// so the entire scene-approval table was invisible on the default "Review &amp; Approve" tab. See
/// the closing-brace fix right after the clip-player block in Review.razor.
/// </summary>
[Collection("ui-pipeline")]
public class ReviewFlowTests
{
    private readonly PipelineFixture _fx;
    public ReviewFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Approving_a_clip_and_a_scene_updates_the_checklist()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl,
                "Review_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");

            // No scene approved yet. Default landing is Finish; scene rows live on Review and Approve.
            await page.GetByTestId("review-tab-review").ClickAsync();
            var checklist = page.GetByTestId("review-checklist");
            await Assertions.Expect(checklist).ToBeVisibleAsync(new() { Timeout = 30_000 });
            Assert.Equal("0", await checklist.GetAttributeAsync("data-approved-count"));

            var firstRow = page.GetByTestId("review-scene-row").First;
            await Assertions.Expect(firstRow).ToBeVisibleAsync(new() { Timeout = 30_000 });
            var sceneNumber = await firstRow.GetAttributeAsync("data-scene-number");
            Assert.False(string.IsNullOrWhiteSpace(sceneNumber));

            // Open the clip review panel and Pass the first clip.
            await page.GetByTestId($"review-clips-{sceneNumber}").ClickAsync();
            var passBtn = page.GetByTestId($"review-pass-{sceneNumber}-1");
            await Assertions.Expect(passBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await passBtn.ClickAsync();
            // ReviewAsync round-trips through the server (SoftLoadAsync) — wait for the busy button
            // to re-enable rather than asserting on a fixed delay.
            await Assertions.Expect(passBtn).ToBeEnabledAsync(new() { Timeout = 15_000 });

            // Approve the scene — the checklist count must reflect it without a page reload.
            var approveBtn = page.GetByTestId($"review-approve-{sceneNumber}");
            // Background client saves (register → soft reload) flip the page busy for a beat; a click
            // that lands on that beat is dropped by Blazor. Click on an enabled button, and re-click
            // once it is enabled again if the approval has not landed.
            var approved = false;
            for (var attempt = 0; attempt < 3 && !approved; attempt++)
            {
                await Assertions.Expect(approveBtn).ToBeEnabledAsync(new() { Timeout = 30_000 });
                await approveBtn.ClickAsync();
                try
                {
                    await Assertions.Expect(page.GetByTestId("review-scene-row").First)
                        .ToHaveAttributeAsync("data-approved", "true", new() { Timeout = 8_000 });
                    approved = true;
                }
                catch (PlaywrightException) { /* retry */ }
            }
            if (!approved)
            {
                // Diagnostic: what did the page say (error/message alerts) when the approval did not land?
                var alerts = await page.EvaluateAsync<string>("() => [...document.querySelectorAll('.alert, [role=alert]')].map(e => e.innerText.trim()).filter(Boolean).join(' | ')");
                var btnState = await approveBtn.EvaluateAsync<string>("b => b.outerHTML");
                Assert.Fail($"Scene approval did not land. alerts: {alerts} ; approve button: {btnState}");
            }
            await Assertions.Expect(checklist).ToHaveAttributeAsync("data-approved-count", "1", new() { Timeout = 15_000 });

            // Approval is one-way (EditLogService.MarkSceneApprovedAsync always writes "approved" —
            // there is no unapprove endpoint). Clicking the now-"✓ Approved" button re-approves and
            // stays at 1; it is not a toggle despite the button rendering in a pressed-looking state.
            await approveBtn.ClickAsync();
            await Assertions.Expect(approveBtn).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(checklist).ToHaveAttributeAsync("data-approved-count", "1", new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Finish_review_and_share_tabs_are_reachable_once_clips_exist()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl,
                "ReviewTabs_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");
            await Assertions.Expect(page.GetByTestId("review-checklist")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var finishTab = page.GetByTestId("review-tab-finish");
            await Assertions.Expect(finishTab).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(finishTab).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("btn-warning"), new() { Timeout = 15_000 });

            var shareTab = page.GetByTestId("review-tab-share");
            await Assertions.Expect(shareTab).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await shareTab.ClickAsync();
            await Assertions.Expect(page.GetByTestId("review-share-card")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await page.GetByTestId("review-tab-review").ClickAsync();
            await Assertions.Expect(page.GetByTestId("review-scene-row").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
