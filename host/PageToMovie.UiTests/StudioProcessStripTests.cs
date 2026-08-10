using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Residual 6.2 (studio-state-machine migration): studio process strip + left-nav gates
/// (Cast / Estimate / Film / Review) must track <see cref="PageToMovie.Core.Models.StudioStateMachine"/>
/// after the migration. Uses the isolated pipeline host + fountain fixture so readiness is real.
///
/// Strip is inspected on <c>/cost</c> (Active=estimate). The <b>active</b> step never gets
/// <c>is-disabled</c> (by design); gated active steps still carry the blocked reason in <c>title</c>.
/// Left nav uses enabled link vs <c>{testid}-disabled</c> span (see <c>NavMenu.NavItem</c>).
/// </summary>
[Collection("ui-pipeline")]
public class StudioProcessStripTests
{
    private readonly PipelineFixture _fx;
    public StudioProcessStripTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Strip_and_nav_gates_from_unsigned_draft_through_cast_ready()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var name = "Strip_" + Guid.NewGuid().ToString("N")[..6];

            // ── B: import fountain, unsigned draft ─────────────────────────
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, name);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "tell_tale_heart.fountain");
            await page.GetByTestId("screenplay-status")
                .WaitForAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("screenplay-status"))
                .ToHaveAttributeAsync("data-draft", "true", new() { Timeout = 60_000 });

            await OpenStripHostAsync(page);
            await ExpectStepGatedAsync(page, "studio-step-cast", "screenplay");
            await ExpectStepGatedAsync(page, "studio-step-estimate", "screenplay"); // active on /cost
            await ExpectStepGatedAsync(page, "studio-step-film", "screenplay");
            await ExpectStepGatedAsync(page, "studio-step-review", "shot plan");
            // Film blocked reason is screenplay until signed; Review still says shot plan.
            await Ui.ExpectNavGatedAsync(page, "nav-characters", "screenplay");
            await Ui.ExpectNavGatedAsync(page, "nav-scenes", "screenplay");
            await Ui.ExpectNavGatedAsync(page, "nav-review", "shot plan");

            // ── D: sign off → Cast + Estimate unlock; Film/Review still blocked ──
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/adaptation/screenplay");
            await PipelineFlow.SignOffScreenplayAsync(page);
            await page.WaitForURLAsync(new Regex("characters", RegexOptions.IgnoreCase), new() { Timeout = 90_000 });

            await OpenStripHostAsync(page);
            await ExpectStepOpenAsync(page, "studio-step-cast");
            await ExpectStepOpenAsync(page, "studio-step-estimate");
            await ExpectStepGatedAsync(page, "studio-step-film", "shot plan");
            await ExpectStepGatedAsync(page, "studio-step-review", "shot plan");
            await Ui.ExpectNavOpenAsync(page, "nav-characters");
            await Ui.ExpectNavGatedAsync(page, "nav-scenes", "shot plan");
            await Ui.ExpectNavGatedAsync(page, "nav-review", "shot plan");

            // ── E: shot plan ready, cast incomplete → Film off, Review on ───
            await PipelineFlow.BuildShotPlanAsync(page);
            await OpenStripHostAsync(page);

            await ExpectStepOpenAsync(page, "studio-step-cast");
            await ExpectStepOpenAsync(page, "studio-step-estimate");
            await ExpectStepGatedAsync(page, "studio-step-film", "voice");
            await ExpectStepOpenAsync(page, "studio-step-review");
            await Ui.ExpectNavOpenAsync(page, "nav-characters");
            await Ui.ExpectNavGatedAsync(page, "nav-scenes", "voice");
            await Ui.ExpectNavOpenAsync(page, "nav-review");

            // ── G: cast ready → Film unlocks ────────────────────────────────
            await PipelineFlow.MakeCastReadyForShotsAsync(page);
            await OpenStripHostAsync(page);

            await ExpectStepOpenAsync(page, "studio-step-film");
            await ExpectStepOpenAsync(page, "studio-step-review");
            await ExpectStepOpenAsync(page, "studio-step-cast");
            await ExpectStepOpenAsync(page, "studio-step-estimate");
            await Ui.ExpectNavOpenAsync(page, "nav-scenes");
            await Ui.ExpectNavOpenAsync(page, "nav-review");
            await Ui.ExpectNavOpenAsync(page, "nav-characters");
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Strip_cast_stays_disabled_with_unsigned_draft_after_import()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "StripDraft_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "solo.fountain");
            await OpenStripHostAsync(page);

            await ExpectStepGatedAsync(page, "studio-step-cast", "screenplay");
            await ExpectStepGatedAsync(page, "studio-step-estimate", "screenplay");
            await Ui.ExpectNavGatedAsync(page, "nav-characters", "screenplay");
            await Ui.ExpectNavGatedAsync(page, "nav-scenes", "screenplay");
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>
    /// After sign-off the app navigates to Characters; left-nav Cast must be a real link and Film
    /// must remain the disabled span until the shot plan exists.
    /// </summary>
    [Fact]
    public async Task Nav_cast_unlocks_after_screenplay_signoff_film_stays_gated()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl,
                "NavSignoff_" + Guid.NewGuid().ToString("N")[..6], "solo.fountain");

            await Ui.ExpectNavOpenAsync(page, "nav-characters");
            await Ui.ExpectNavGatedAsync(page, "nav-scenes", "shot plan");
            await Ui.ExpectNavGatedAsync(page, "nav-review", "shot plan");

            // Film deep link must show readiness gate, not capability "Set up →".
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await Assertions.Expect(page.GetByText("Finish the shot plan", new() { Exact = false }).First)
                .ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("scenes-generate-batch-cap-setup-link"))
                .ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Estimate page mounts the full strip with Active=estimate.</summary>
    private async Task OpenStripHostAsync(IPage page)
    {
        await Ui.GotoAppAsync(page, _fx.BaseUrl, "/cost");
        await page.GetByTestId("studio-step-cast").WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 45_000,
        });
        // Allow ActiveProject readiness refresh after navigation.
        await page.WaitForTimeoutAsync(800);
    }

    /// <summary>
    /// Step is not navigable for pipeline progress: either <c>is-disabled</c>, or (active step)
    /// the title still carries the blocked reason.
    /// </summary>
    private static async Task ExpectStepGatedAsync(IPage page, string testId, string reasonContains)
    {
        var step = page.GetByTestId(testId);
        await step.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 });

        var deadline = DateTime.UtcNow.AddSeconds(25);
        string title = "";
        string cls = "";
        while (DateTime.UtcNow < deadline)
        {
            title = await step.GetAttributeAsync("title") ?? "";
            cls = await step.GetAttributeAsync("class") ?? "";
            var titleOk = title.Contains(reasonContains, StringComparison.OrdinalIgnoreCase);
            var disabled = cls.Contains("is-disabled", StringComparison.Ordinal);
            var active = cls.Contains("is-active", StringComparison.Ordinal);
            // Non-active gated steps must be disabled; active gated steps only require the title.
            if (titleOk && (disabled || active))
                return;
            await page.WaitForTimeoutAsync(300);
        }
        Assert.Fail(
            $"Expected {testId} gated with title containing '{reasonContains}'. title='{title}' class='{cls}'");
    }

    /// <summary>Step is open: not is-disabled, and title is the happy-path affordance (no block reason).</summary>
    private static async Task ExpectStepOpenAsync(IPage page, string testId)
    {
        var step = page.GetByTestId(testId);
        await step.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        var deadline = DateTime.UtcNow.AddSeconds(45);
        string? cls = null;
        string? title = null;
        while (DateTime.UtcNow < deadline)
        {
            cls = await step.GetAttributeAsync("class") ?? "";
            title = await step.GetAttributeAsync("title") ?? "";
            if (!cls.Contains("is-disabled", StringComparison.Ordinal)
                && !title.Contains("first", StringComparison.OrdinalIgnoreCase)
                && !title.Contains("Approve every", StringComparison.OrdinalIgnoreCase))
                return;
            await page.WaitForTimeoutAsync(400);
        }
        Assert.Fail($"Expected {testId} open; class='{cls}' title='{title}'");
    }
}
