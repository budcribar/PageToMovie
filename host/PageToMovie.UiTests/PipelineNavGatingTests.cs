using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PageToMovie.Core.Utils;

namespace PageToMovie.UiTests;

/// <summary>
/// Full pipeline happy path on a fresh project, driven through the REAL page controls wherever a
/// button exists (create → import book → sign off screenplay → cast → build shot plan → scenes →
/// review), asserting at every step that the left-nav gates open in order (blocked steps render as
/// disabled with their blocked reason) and that each step page renders its expected fake results.
/// The other pipeline classes go deep on one page each; this one pins the connective tissue —
/// nav gating + step-to-step links — which is where a readiness-refresh regression shows up first.
/// </summary>
[Collection("ui-pipeline")]
public class PipelineNavGatingTests
{
    private readonly PipelineFixture _fx;
    public PipelineNavGatingTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Nav_gates_open_step_by_step_and_each_step_page_renders()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            // ── 0. Fresh project → Import page. Everything downstream is gated. ──
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "Gate_" + Guid.NewGuid().ToString("N")[..6]);
            await Assertions.Expect(page.GetByTestId("nav-adaptation")).ToBeVisibleAsync(new() { Timeout = 20_000 });
            await AssertNavBlockedAsync(page, "nav-characters", "Approve the screenplay first");
            await AssertNavBlockedAsync(page, "nav-scenes");
            await AssertNavBlockedAsync(page, "nav-review");
            await Assertions.Expect(page.GetByTestId("import-status")).ToHaveAttributeAsync("data-can-continue", "false", new() { Timeout = 20_000 });
            await Assertions.Expect(page.GetByTestId("import-file-input")).ToBeAttachedAsync();

            // ── 1. Import a Fountain book (fake planning) → auto-navigates to Screenplay. ──
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "tell_tale_heart.fountain");
            var spStatus = page.GetByTestId("screenplay-status");
            await Assertions.Expect(spStatus).ToHaveAttributeAsync("data-draft", "true", new() { Timeout = 60_000 });
            var scenes = int.Parse(await spStatus.GetAttributeAsync("data-scenes") ?? "0");
            Assert.True(scenes >= 1, $"screenplay draft should have >=1 scene, got {scenes}");
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            // Import page now offers "continue" (book is in): go back and check, then return.
            await page.GotoAsync($"{_fx.BaseUrl}/adaptation/import?admin=1");
            await Assertions.Expect(page.GetByTestId("import-status")).ToHaveAttributeAsync("data-can-continue", "true", new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("import-continue-screenplay")).ToBeVisibleAsync();
            await page.GetByTestId("import-continue-screenplay").ClickAsync();
            await page.WaitForURLAsync(new Regex("adaptation/screenplay", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 30_000 });

            // ── 2. Sign off → server extracts cast → app lands on Estimate (DecisionCard); Cast gate opens. ──
            await PipelineFlow.SignOffScreenplayAsync(page);
            await PipelineFlow.WaitForSignOffLandingAsync(page);
            await Assertions.Expect(page.GetByTestId("cost-page")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("nav-characters")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("nav-characters-disabled")).ToHaveCountAsync(0);
            await page.GetByTestId("nav-characters").ClickAsync();
            await page.WaitForURLAsync(new Regex("/characters", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 30_000 });
            // The sign-off extracted the cast (fake planning): the roster is populated.
            await Assertions.Expect(page.GetByTestId("cast-index")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("char-list-item").First).ToBeVisibleAsync(new() { Timeout = 90_000 });
            Assert.True(await page.GetByTestId("char-list-item").CountAsync() >= 1, "expected at least one cast member");
            // Film/Review still gated (no shot plan yet).
            await AssertNavBlockedAsync(page, "nav-scenes");
            await AssertNavBlockedAsync(page, "nav-review");

            // ── 3. Shot plan via the real "Build" button on the Shots page (SignalR job flow).
            //      Building the plan doesn't need locked looks (that gate is video generation). ──
            await page.GotoAsync($"{_fx.BaseUrl}/adaptation/shots?admin=1");
            var shotsStatus = page.GetByTestId("shots-status");
            await Assertions.Expect(shotsStatus).ToHaveAttributeAsync("data-ready", "false", new() { Timeout = 30_000 });
            var build = page.GetByTestId("shots-build");
            await Assertions.Expect(build).ToBeEnabledAsync(new() { Timeout = 30_000 });
            await build.ClickAsync();
            await Assertions.Expect(shotsStatus).ToHaveAttributeAsync("data-ready", "true", new() { Timeout = 180_000 });
            var planScenes = int.Parse(await shotsStatus.GetAttributeAsync("data-scenes") ?? "0");
            var planClips = int.Parse(await shotsStatus.GetAttributeAsync("data-clips") ?? "0");
            Assert.True(planScenes >= 1 && planClips >= 1, $"shot plan should have scenes+clips, got {planScenes}/{planClips}");
            await Assertions.Expect(page.GetByTestId("shots-to-scenes")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            // The job's completion refreshed readiness: Film and Review gates are open now.
            await Assertions.Expect(page.GetByTestId("nav-scenes")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("nav-review")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("nav-scenes-disabled")).ToHaveCountAsync(0);

            // ── 4. Scenes page shows the plan (via the step link, not a deep link). ──
            await page.GetByTestId("shots-to-scenes").ClickAsync();
            await page.WaitForURLAsync(new Regex("/scenes", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 30_000 });
            var scStatus = page.GetByTestId("scenes-status");
            try { await scStatus.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }); }
            catch (TimeoutException)
            {
                var main = await page.Locator("main").InnerTextAsync();
                Assert.Fail("Scenes page never rendered its status marker after following the Shots → Scenes link. Page text:\n" + main);
            }
            var sceneCount = int.Parse(await scStatus.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.True(sceneCount >= 1, $"expected >=1 scene on the Scenes page, got {sceneCount}");
            await Assertions.Expect(page.GetByTestId("scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("scenes-generate-batch")).ToBeVisibleAsync();

            // ── 5. Review page lists the same scenes in its checklist. ──
            await page.GetByTestId("nav-review").ClickAsync();
            await page.WaitForURLAsync(new Regex("/review", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 30_000 });
            var checklist = page.GetByTestId("review-checklist");
            await checklist.WaitForAsync(new() { Timeout = 60_000 });
            var reviewScenes = int.Parse(await checklist.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.True(reviewScenes >= 1, $"expected >=1 scene in the review checklist, got {reviewScenes}");
            await Assertions.Expect(page.GetByTestId("review-scene-row").First).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // ── 6. Home reflects the project: Film is now the "next" step on the process strip. ──
            await Ui.GotoHomePickerAsync(page);
            await Assertions.Expect(page.GetByTestId("studio-process-nav")).ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>A gated nav item renders as a disabled span carrying the blocked reason as its title,
    /// and the enabled link variant is absent.</summary>
    private static async Task AssertNavBlockedAsync(IPage page, string testId, string? expectedReason = null)
    {
        var disabled = page.GetByTestId(testId + "-disabled");
        await Assertions.Expect(disabled).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Assertions.Expect(page.GetByTestId(testId)).ToHaveCountAsync(0);
        var title = await disabled.GetAttributeAsync("title") ?? "";
        Assert.False(string.IsNullOrWhiteSpace(title), $"{testId} is gated but shows no blocked reason");
        if (expectedReason is not null)
            Assert.Equal(expectedReason, title);
    }
}
