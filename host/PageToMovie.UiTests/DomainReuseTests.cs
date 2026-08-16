using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PageToMovie.Core.Options;
using PageToMovie.Engine;

namespace PageToMovie.UiTests;

/// <summary>
/// Domain-reuse: instead of re-deriving expected values in the test, drive the *real* Engine
/// (ProjectStore/CostReportService, over the same workspace the host uses) to decide what the UI
/// should show, then assert the browser matches. This is the payoff of a C# UI suite.
/// </summary>
[Collection("ui")]
public class DomainReuseTests
{
    private readonly AppFixture _fx;
    public DomainReuseTests(AppFixture fx) => _fx = fx;

    private static ProjectStore Store(string repo) =>
        new(Options.Create(new PageToMovieOptions { WorkspaceRoot = repo, EnableReadCaches = false }));

    [Fact]
    public async Task Cost_page_estimate_presence_matches_CostReportService_for_active_project()
    {
        var repo = _fx.WorkspaceRootPath;
        var activeId = Ui.ActiveProjectId(repo);
        Assert.False(string.IsNullOrWhiteSpace(activeId));

        var costs = new CostReportService(Store(repo));

        // The real Engine decides whether an estimate is even possible (fail-fast with no model).
        double? engineDraftTotal = null;
        try
        {
            var report = await costs.GetReportAsync(activeId!, "480p", "720p");
            engineDraftTotal = report.Summary.FullFilmAllDraftUsd;
        }
        catch (InvalidOperationException) { /* no model set → no estimate */ }

        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/cost");
            var estimate = page.GetByTestId("cost-estimate");
            if (engineDraftTotal is null)
            {
                await Assertions.Expect(estimate).ToHaveCountAsync(0);
            }
            else
            {
                await Assertions.Expect(estimate).ToBeVisibleAsync();
                var shown = double.Parse((await estimate.InnerTextAsync()).Replace("$", "").Replace(",", "").Trim());
                Assert.True(shown > 0, $"expected a positive estimate, got '{shown}'");
            }
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Scenes_page_lists_the_shot_plan_scenes_from_ProjectStore()
    {
        var repo = _fx.WorkspaceRootPath;
        var activeId = Ui.ActiveProjectId(repo);
        Assert.False(string.IsNullOrWhiteSpace(activeId));

        // Reuse the real store to enumerate the active project's shot-plan scenes.
        var scenes = await Store(repo).ListScenesAsync(activeId!, probeDurations: false);
        Assert.NotEmpty(scenes);

        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            // The first screenful of scene badges (S01, S02, …) must match the Engine's scene list.
            foreach (var sc in scenes.Take(3))
                await Assertions.Expect(page.GetByText($"S{sc.SceneNumber:D2}", new() { Exact = true }).First)
                                .ToBeVisibleAsync();
        }
        finally { await ctx.CloseAsync(); }
    }
}
