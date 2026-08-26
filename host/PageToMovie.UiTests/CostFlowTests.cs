using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Cost / Estimate page flow: after importing and signing off on a screenplay, lands on /cost
/// (Estimate / DecisionCard) with duration forecast, estimate basis, and breakdown cards.
/// </summary>
[Collection("ui-pipeline")]
public class CostFlowTests
{
    private readonly PipelineFixture _fx;
    public CostFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Cost_page_renders_forecast_metrics_and_basis_after_signoff()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var projectName = "CostFlow_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, projectName);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "tell_tale_heart.fountain");
            await PipelineFlow.SignOffScreenplayAsync(page);
            await PipelineFlow.WaitForSignOffLandingAsync(page);

            // Verify on /cost page
            await Assertions.Expect(page.GetByTestId("cost-page")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("decision-forecast-card")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Duration label and estimate are visible
            var duration = page.GetByTestId("cost-duration");
            await Assertions.Expect(duration).ToBeVisibleAsync(new() { Timeout = 15_000 });
            var durationText = await duration.InnerTextAsync();
            Assert.False(string.IsNullOrWhiteSpace(durationText));

            var estimate = page.GetByTestId("cost-estimate");
            await Assertions.Expect(estimate).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
