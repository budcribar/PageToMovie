using System.Text.RegularExpressions;
using Microsoft.Playwright;

using PageToMovie.Core.Utils;
namespace PageToMovie.UiTests;

/// <summary>
/// E2E coverage for the Title:/Author: quick-insert chips in the screenplay editor's "Advanced…"
/// panel (AdaptationScreenplay.razor.cs InsertTitleFieldAsync -> fountain-editor.js
/// insertTitleField). Unlike every other Advanced helper (which inserts at the cursor), these always
/// target the document start (line 0) — Title:/Author: only parse as Fountain title-page metadata
/// near the top of the file — and jump to an existing line instead of duplicating it.
/// </summary>
[Collection("ui-pipeline")]
public class ScreenplayTitleAuthorChipTests
{
    private readonly PipelineFixture _fx;
    public ScreenplayTitleAuthorChipTests(PipelineFixture fx) => _fx = fx;

    private const string EditorId = "screenplay-main";

    /// <summary>Open the "Advanced…" popover. Its own JS auto-closes it on any button click inside,
    /// so callers must re-open before each chip click that follows another.</summary>
    private static async Task OpenAdvancedPanelAsync(IPage page)
    {
        await page.Locator("summary", new() { HasText = "Advanced" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("screenplay-insert-title")).ToBeVisibleAsync(new() { Timeout = 10_000 });
    }

    private static Task<string> GetEditorValueAsync(IPage page) =>
        page.EvaluateAsync<string>($"() => window.fountainEditor.getValue('{EditorId}')");

    private static async Task WaitForEditorReadyAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            $"() => window.fountainEditor && window.fountainEditor.getValue('{EditorId}').length > 0",
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static int CountLinesStartingWith(string text, string prefix)
    {
        var re = new Regex($"^\\s*{Regex.Escape(prefix)}", RegexOptions.IgnoreCase | RegexOptions.Multiline, CommonRegex.Timeout);
        return re.Matches(text).Count;
    }

    // Author anchors after an existing Title: line (insertTitleField's afterKey) instead of always
    // targeting line 0, so clicking Title then Author lands in standard Fountain title-page order.
    [Fact]
    public async Task Fresh_project_title_then_author_insert_at_top_in_order_and_persist()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "TitleChip_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "no_title_author.fountain");
            await WaitForEditorReadyAsync(page);

            var initial = await GetEditorValueAsync(page);
            Assert.DoesNotContain("Title:", initial, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Author:", initial, StringComparison.OrdinalIgnoreCase);

            await OpenAdvancedPanelAsync(page);
            var titleBtn = page.GetByTestId("screenplay-insert-title");
            await Assertions.Expect(titleBtn).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await titleBtn.ClickAsync();
            await page.WaitForFunctionAsync(
                $"() => window.fountainEditor.getValue('{EditorId}').toLowerCase().startsWith('title:')",
                new PageWaitForFunctionOptions { Timeout = 10_000 });
            await page.Keyboard.TypeAsync("Batman Begins Again");

            var afterTitle = await GetEditorValueAsync(page);
            Assert.StartsWith("Title: Batman Begins Again", afterTitle);
            Assert.Equal(1, CountLinesStartingWith(afterTitle, "Title:"));

            // The panel auto-closed after the Title click; reopen for Author.
            var authorBtn = page.GetByTestId("screenplay-insert-author");
            if (!await authorBtn.IsVisibleAsync())
                await OpenAdvancedPanelAsync(page);
            await Assertions.Expect(authorBtn).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await authorBtn.ClickAsync();
            // Anchors after the existing Title: line (not line 0), so the document's SECOND line —
            // not its start — is where the cursor now lands.
            await page.WaitForFunctionAsync(
                $"() => (window.fountainEditor.getValue('{EditorId}').split(/\\r?\\n/)[1] || '').toLowerCase().startsWith('author:')",
                new PageWaitForFunctionOptions { Timeout = 10_000 });
            await page.Keyboard.TypeAsync("Jane Test Author");

            var final = await GetEditorValueAsync(page);
            var lines = final.Replace("\r\n", "\n").Split('\n');

            // Author anchors after the existing Title: line, so standard order is preserved.
            Assert.StartsWith("Title: Batman Begins Again", lines[0]);
            Assert.StartsWith("Author: Jane Test Author", lines[1]);
            // Neither insert duplicated — exactly one of each line in the whole document.
            Assert.Equal(1, CountLinesStartingWith(final, "Title:"));
            Assert.Equal(1, CountLinesStartingWith(final, "Author:"));

            // Persists: sign off (screenplay text, including both new lines, is saved first), then
            // reload the screenplay page fresh and confirm both lines survived the round trip.
            await PipelineFlow.SignOffScreenplayAsync(page);
            await PipelineFlow.WaitForSignOffLandingAsync(page);

            // Not Ui.GotoAppAsync: its readiness marker (a[href='/scenes']) only renders as a real
            // link once a shot plan (Stage2) exists — this test never builds one, so that nav item
            // stays a disabled span and the helper would time out waiting for it.
            await page.GotoAsync($"{_fx.BaseUrl}/adaptation/screenplay?admin=1");
            await WaitForEditorReadyAsync(page);
            var reloaded = await GetEditorValueAsync(page);
            Assert.Contains("Title: Batman Begins Again", reloaded);
            Assert.Contains("Author: Jane Test Author", reloaded);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Chips_jump_to_existing_lines_without_duplicating()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "TitleChipExisting_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            // tell_tale_heart.fountain already has Title:/Author: (plus Credit:/Source:/Draft date:)
            // lines within the first 30 lines — the same window the chips and ProjectStore scan.
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "tell_tale_heart.fountain");
            await WaitForEditorReadyAsync(page);

            var before = await GetEditorValueAsync(page);
            var beforeLineCount = before.Replace("\r\n", "\n").Split('\n').Length;
            Assert.Equal(1, CountLinesStartingWith(before, "Title:"));
            Assert.Equal(1, CountLinesStartingWith(before, "Author:"));

            await OpenAdvancedPanelAsync(page);
            var titleBtn = page.GetByTestId("screenplay-insert-title");
            await Assertions.Expect(titleBtn).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await titleBtn.ClickAsync();
            // Jump-only path — nothing textual to poll for, just give the JSInterop round trip a beat.
            await page.WaitForTimeoutAsync(300);

            var afterTitleClick = await GetEditorValueAsync(page);
            Assert.Equal(before, afterTitleClick);
            Assert.Equal(1, CountLinesStartingWith(afterTitleClick, "Title:"));

            var authorBtn = page.GetByTestId("screenplay-insert-author");
            if (!await authorBtn.IsVisibleAsync())
                await OpenAdvancedPanelAsync(page);
            await Assertions.Expect(authorBtn).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await authorBtn.ClickAsync();
            await page.WaitForTimeoutAsync(300);

            var afterAuthorClick = await GetEditorValueAsync(page);
            Assert.Equal(before, afterAuthorClick);
            Assert.Equal(1, CountLinesStartingWith(afterAuthorClick, "Author:"));

            var afterLineCount = afterAuthorClick.Replace("\r\n", "\n").Split('\n').Length;
            Assert.Equal(beforeLineCount, afterLineCount);
        }
        finally { await ctx.CloseAsync(); }
    }
}
