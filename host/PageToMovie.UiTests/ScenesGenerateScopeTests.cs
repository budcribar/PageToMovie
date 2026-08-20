using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Combined film-level Generate confirm (#20): Missing clips only vs All clips as new takes.
/// </summary>
[Collection("ui-pipeline")]
public class ScenesGenerateScopeTests
{
    private readonly PipelineFixture _fx;
    public ScenesGenerateScopeTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Missing_only_scope_fills_holes_from_the_generate_confirm()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var name = "GenMiss_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl, name, "tell_tale_heart.fountain");
            await PipelineFlow.MakeCastReadyForShotsAsync(page);
            await PipelineFlow.BuildShotPlanAsync(page);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await OpenGenerateConfirmAsync(page);

            var missing = page.GetByTestId("generate-confirm-scope-missing");
            await Assertions.Expect(missing).ToBeEnabledAsync();
            await Assertions.Expect(missing).ToBeCheckedAsync();
            await Assertions.Expect(page.GetByTestId("generate-confirm-resolution")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("generate-confirm-cost")).ToBeVisibleAsync();

            await page.GetByTestId("generate-confirm-go").ClickAsync();
            await Assertions.Expect(page.GetByTestId("job-modal-status")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task All_takes_scope_force_renders_from_the_generate_confirm()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl, "GenAll_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");
            await OpenGenerateConfirmAsync(page);

            var missing = page.GetByTestId("generate-confirm-scope-missing");
            var allTakes = page.GetByTestId("generate-confirm-scope-all");
            await Assertions.Expect(missing).ToBeDisabledAsync();
            await Assertions.Expect(allTakes).ToBeCheckedAsync();
            await Assertions.Expect(page.GetByTestId("generate-confirm-resolution")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("generate-confirm-cost")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("All clips as new takes")).ToBeVisibleAsync();

            await page.GetByTestId("generate-confirm-go").ClickAsync();
            await Assertions.Expect(page.GetByTestId("job-modal-status")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    private static async Task OpenGenerateConfirmAsync(IPage page)
    {
        await page.GetByTestId("scenes-select-all").CheckAsync(new() { Timeout = 15_000 });
        await page.GetByTestId("scenes-generate-batch").ClickAsync(new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByTestId("generate-confirm-modal")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }
}
