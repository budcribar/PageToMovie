using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Screenplay structured editor: a Fountain dialogue block that spans lines (verse, or the
/// Tell-Tale Heart fixture's "…and am." / "But why will you say that I am mad?") is a single
/// speech. The beat row is a single-line input and browsers strip newlines from its value, so
/// it used to read "am.But why" (Mary11: "playto see a lamb"). It must show a space, and the
/// stored Fountain must keep its lines.
/// </summary>
[Collection("ui-pipeline")]
public class ScreenplayBeatEditorTests
{
    private readonly PipelineFixture _fx;
    public ScreenplayBeatEditorTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Multi_line_dialogue_shows_as_one_spaced_line_in_the_beat_editor()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "Beat_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "tell_tale_heart.fountain");

            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var spoken = page.GetByPlaceholder("Spoken dialogue…");
            await Assertions.Expect(spoken.First).ToBeVisibleAsync(new() { Timeout = 60_000 });

            var values = await spoken.EvaluateAllAsync<string[]>("els => els.map(e => e.value)");
            var joined = values.FirstOrDefault(v => v.Contains("dreadfully nervous", StringComparison.Ordinal));
            Assert.False(joined is null, "expected the NARRATOR's opening speech among the beat inputs; got: " + string.Join(" || ", values));
            Assert.Contains("and am. But why", joined);
            Assert.DoesNotContain("am.But", joined);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Adding_action_beat_via_dropdown_inserts_new_beat_input_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "AddBeat_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "tell_tale_heart.fountain");

            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var addSelect = page.Locator(".spe-sel-add").First;
            await Assertions.Expect(addSelect).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Count rows before
            var rows = page.Locator(".spe-beat-row");
            var countBefore = await rows.CountAsync();

            // Select Action beat
            await addSelect.SelectOptionAsync(new SelectOptionValue { Value = "Action" });

            // Rows should increment by 1
            await Assertions.Expect(rows).ToHaveCountAsync(countBefore + 1, new() { Timeout = 15_000 });

            // Type in the new action beat input
            var lastBeatInput = page.Locator(".spe-beat-row input[placeholder*='What we see']").Last;
            await lastBeatInput.FillAsync("A raven taps loudly against the dark window pane.");
            await lastBeatInput.BlurAsync();

            // Wait for autosave debounce (900ms)
            await page.WaitForTimeoutAsync(1500);

            // Reload page to verify persistence
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var lastActionInput = page.Locator(".spe-beat-row input[placeholder*='What we see']").Last;
            await Assertions.Expect(lastActionInput).ToHaveValueAsync("A raven taps loudly against the dark window pane.", new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
