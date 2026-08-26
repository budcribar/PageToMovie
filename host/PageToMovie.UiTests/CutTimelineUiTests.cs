using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Cut timeline interactions on the Review page's Finish tab: Backspace inside the text inspector
/// edits that field instead of deleting the selected block (Bug #1), and a ruler range drag
/// followed by the range Delete button shortens the movie (Bug #15).
///
/// Reach the page by clicking the Review nav link, never by loading /review directly. The editor
/// takes its clips from the browser's media root, which lives in page JS state — a full page load
/// drops it and leaves an empty timeline with "Save cut" disabled.
/// </summary>
[Collection("ui-pipeline")]
public class CutTimelineUiTests
{
    private readonly PipelineFixture _fx;
    public CutTimelineUiTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Backspace_in_the_text_inspector_edits_the_field_instead_of_deleting()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await OpenFinishTabWithClipsAsync(page, "CutBack");

            var clips = page.GetByTestId("cut-tl-clip");
            var clipsBefore = await clips.CountAsync();

            // Adding a title selects it and focuses its text field — exactly the state where an
            // unguarded Backspace shortcut would wipe the block being typed into.
            await page.GetByTestId("cut-tl-text-add").ClickAsync();
            var textBlocks = page.GetByTestId("cut-tl-text-clip");
            await Assertions.Expect(textBlocks).ToHaveCountAsync(1, new() { Timeout = 15_000 });

            var content = page.GetByTestId("cut-tl-text-content");
            await Assertions.Expect(content).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await content.FillAsync("FADE IN");
            await content.PressAsync("Backspace");

            // The key edited one character; the title and every clip are still on the timeline.
            await Assertions.Expect(content).ToHaveValueAsync("FADE I");
            await Assertions.Expect(textBlocks).ToHaveCountAsync(1);
            await Assertions.Expect(clips).ToHaveCountAsync(clipsBefore);
            await Assertions.Expect(page.Locator(".cut-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Range_delete_shortens_the_movie()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await OpenFinishTabWithClipsAsync(page, "CutRange");

            var clock = page.Locator(".cut-tl-clock");
            var totalBefore = TotalOf(await clock.InnerTextAsync());

            // Dragging on the ruler is the only way to mark a range for deletion.
            var ruler = page.GetByTestId("cut-tl-ruler");
            var box = await ruler.BoundingBoxAsync()
                      ?? throw new InvalidOperationException("Timeline ruler has no box.");
            var y = box.Y + (box.Height / 2);
            await page.Mouse.MoveAsync(box.X + (box.Width * 0.15f), y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(box.X + (box.Width * 0.35f), y, new() { Steps = 8 });
            await page.Mouse.MoveAsync(box.X + (box.Width * 0.55f), y, new() { Steps = 8 });
            await page.Mouse.UpAsync();

            var deleteRange = page.Locator("button.cut-tl-del");
            await Assertions.Expect(deleteRange).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await deleteRange.ClickAsync();

            // Range cleared, no error, and the movie is genuinely shorter than it was.
            await Assertions.Expect(deleteRange).ToHaveCountAsync(0, new() { Timeout = 15_000 });
            await Assertions.Expect(page.Locator(".cut-error")).ToHaveCountAsync(0);
            var totalAfter = TotalOf(await clock.InnerTextAsync());
            Assert.True(
                totalAfter < totalBefore,
                $"expected the timeline to shrink after a range delete, got {totalBefore} → {totalAfter}");
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Playhead_scrub_moves_the_clock_without_changing_the_total()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await OpenFinishTabWithClipsAsync(page, "CutPlay");

            var clock = page.Locator(".cut-tl-clock");
            var (headBefore, totalBefore) = ClockParts(await clock.InnerTextAsync());
            Assert.Equal(0, headBefore);

            // Drag the playhead itself to roughly the middle of the timeline. Pressing on the ruler
            // instead would start a range selection, not a seek.
            var playhead = page.GetByTestId("cut-tl-playhead");
            await Assertions.Expect(playhead).ToBeVisibleAsync(new() { Timeout = 15_000 });
            var head = await playhead.BoundingBoxAsync()
                       ?? throw new InvalidOperationException("Playhead has no box.");
            var ruler = await page.GetByTestId("cut-tl-ruler").BoundingBoxAsync()
                        ?? throw new InvalidOperationException("Timeline ruler has no box.");
            await page.Mouse.MoveAsync(head.X + (head.Width / 2), head.Y + (head.Height / 2));
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(ruler.X + (ruler.Width * 0.3f), head.Y + (head.Height / 2), new() { Steps = 8 });
            await page.Mouse.MoveAsync(ruler.X + (ruler.Width * 0.5f), head.Y + (head.Height / 2), new() { Steps = 8 });
            await page.Mouse.UpAsync();

            // Scrubbing seeks; it must not edit the cut.
            var (headAfter, totalAfter) = ClockParts(await clock.InnerTextAsync());
            Assert.True(headAfter > headBefore, $"playhead did not move: {headBefore} → {headAfter}");
            Assert.Equal(totalBefore, totalAfter);
            await Assertions.Expect(page.Locator(".cut-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Run the pipeline, then reach Finish through the nav so the media root survives.</summary>
    private async Task OpenFinishTabWithClipsAsync(IPage page, string prefix)
    {
        await PipelineFlow.RunToGeneratedClipsAsync(
            page, _fx.BaseUrl, prefix + "_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

        await page.GetByTestId("nav-review").ClickAsync();
        await Assertions.Expect(page.Locator(".cut-editor")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByTestId("cut-tl-clip").First)
            .ToBeVisibleAsync(new() { Timeout = 60_000 });

        // Zoom to fit: the timeline lives in a horizontal scroller, so at the default zoom the
        // ruler's box can run past the viewport and pointer coordinates land nowhere.
        await page.GetByTestId("cut-tl-fit").ClickAsync();
    }

    /// <summary>Playhead and total seconds from the "playhead / total" clock (invariant m:ss.ff).</summary>
    private static (double Head, double Total) ClockParts(string clockText)
    {
        var halves = clockText.Split('/');
        Assert.Equal(2, halves.Length);
        return (Seconds(halves[0]), Seconds(halves[1]));
    }

    private static double TotalOf(string clockText) => ClockParts(clockText).Total;

    private static double Seconds(string clock)
    {
        var parts = clock.Trim().Split(':');
        Assert.Equal(2, parts.Length);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return (int.Parse(parts[0], inv) * 60) + double.Parse(parts[1], inv);
    }
}
