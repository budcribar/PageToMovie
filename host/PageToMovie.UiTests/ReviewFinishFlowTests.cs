using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Review page Finish tab: the embedded Cut editor mounts inside the studio flow (Bug #16).
///
/// Scope note — this is a mount smoke test, not timeline coverage. The hosted editor gets its
/// clips from the browser's own media folder, which only exists after the operator grants a
/// directory handle through the File System Access picker. A Playwright context starts with no
/// such grant and the picker is a native dialog, so the timeline (trims, titles, joins, range
/// delete) cannot be reached from this suite at all. Those paths are covered where they can be:
/// PageToMovie.Cut.Tests — CutTextTrackTests for the Backspace-in-a-focused-field guard,
/// CutRangeDeleteTests for range deletion.
/// </summary>
[Collection("ui-pipeline")]
public class ReviewFinishFlowTests
{
    private readonly PipelineFixture _fx;
    public ReviewFinishFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Finish_tab_mounts_the_cut_editor()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToGeneratedClipsAsync(
                page, _fx.BaseUrl, "FinishTab_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/review");

            // Finish is the default view: the editor renders in place, with its save action, and
            // without surfacing an error at the operator.
            var cutEditor = page.Locator(".cut-editor");
            await Assertions.Expect(cutEditor).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.Locator("button.cut-btn", new() { HasText = "Save cut" }))
                .ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(page.Locator(".cut-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }
}
