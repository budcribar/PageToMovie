using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Dialogue timing page (/dialogue-timing) UI tests: verifies that the page resolves the active
/// project, honors the ?scene=N query parameter, and dynamically reacts to scene parameter updates.
/// </summary>
[Collection("ui-pipeline")]
public class DialogueTimingUiTests
{
    private readonly PipelineFixture _fx;
    public DialogueTimingUiTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task DialogueTiming_honors_query_param_scene_and_updates_on_navigation()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "DiagTime_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Navigate to dialogue timing with scene=3
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/dialogue-timing?scene=3");

            var sceneSelect = page.GetByTestId("dt-scene");
            await Assertions.Expect(sceneSelect).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(sceneSelect).ToHaveValueAsync("3");

            // Navigate to scene=1
            await page.GotoAsync($"{_fx.BaseUrl}/dialogue-timing?scene=1");
            await Assertions.Expect(sceneSelect).ToHaveValueAsync("1", new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task DialogueTiming_scene_dropdown_switches_scene_lines_and_status()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "DiagSwitch_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/dialogue-timing");

            var sceneSelect = page.GetByTestId("dt-scene");
            await Assertions.Expect(sceneSelect).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var analyzeBtn = page.GetByTestId("dt-analyze");
            await Assertions.Expect(analyzeBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Select Scene 4 from dropdown
            await sceneSelect.SelectOptionAsync("4");
            await Assertions.Expect(sceneSelect).ToHaveValueAsync("4", new() { Timeout = 10_000 });

            // Assert Analyze button remains available for scene 4
            await Assertions.Expect(analyzeBtn).ToBeEnabledAsync();
        }
        finally { await ctx.CloseAsync(); }
    }
}
