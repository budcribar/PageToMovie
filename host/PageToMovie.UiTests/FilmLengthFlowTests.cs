using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Film length (runtime target) end to end on a real book-text import (fakes Stage 1). Covers the
/// 2026-08-15 bug where a 2-minute nursery rhyme was priced as a 180-minute film: a target from
/// another project must never leak into a fresh one, and "Use estimate" restores the natural
/// length. Book text is required — a Fountain import has no natural length, so the card is hidden.
/// </summary>
[Collection("ui-pipeline")]
public class FilmLengthFlowTests
{
    private readonly PipelineFixture _fx;
    public FilmLengthFlowTests(PipelineFixture fx) => _fx = fx;

    private static readonly Regex UseEstimateMinutes = new(@"\((\d+)\s*min\)", RegexOptions.IgnoreCase);

    [Fact]
    public async Task Target_is_per_project_and_use_estimate_restores_natural_length()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            // Project A: import the nursery rhyme, get its natural length, shorten to 1 min.
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "Len_A_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            var naturalA = await ImportBookTextAndReadNaturalAsync(page, _fx.BaseUrl);
            Assert.True(naturalA >= 1, $"natural length should be ≥ 1 min, was {naturalA}");

            var input = page.GetByTestId("film-length-input");
            await Assertions.Expect(input).ToHaveValueAsync(naturalA.ToString(), new() { Timeout = 15_000 });

            // Shorten (autosaves) — the number must persist across a full reload of the page.
            var shorter = Math.Max(1, naturalA - 1);
            if (shorter != naturalA)
            {
                await input.FillAsync(shorter.ToString());
                await Assertions.Expect(page.GetByTestId("film-length-saving")).ToBeHiddenAsync(new() { Timeout = 30_000 });
                await page.ReloadAsync();
                await Assertions.Expect(page.GetByTestId("film-length-input")).ToHaveValueAsync(shorter.ToString(), new() { Timeout = 60_000 });

                // "Use estimate (N min)" puts it back to the natural length.
                await page.GetByTestId("film-length-estimate").ClickAsync();
                await Assertions.Expect(page.GetByTestId("film-length-input")).ToHaveValueAsync(naturalA.ToString(), new() { Timeout = 30_000 });
                await Assertions.Expect(page.GetByTestId("film-length-error")).ToHaveCountAsync(0);

                // Shorten again so a leak into project B would be observable.
                await page.GetByTestId("film-length-input").FillAsync(shorter.ToString());
                await Assertions.Expect(page.GetByTestId("film-length-saving")).ToBeHiddenAsync(new() { Timeout = 30_000 });
            }

            // Project B: same book. Its target must be its own natural length — nothing remembered
            // from project A (neither the shortened target nor anything else).
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "Len_B_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            var naturalB = await ImportBookTextAndReadNaturalAsync(page, _fx.BaseUrl);
            Assert.Equal(naturalA, naturalB);
            await Assertions.Expect(page.GetByTestId("film-length-input")).ToHaveValueAsync(naturalB.ToString(), new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Upload the Mary Had a Little Lamb text on the import page, wait for the book to be
    /// prepared (the Film length card only exists once a natural length is known) and return the
    /// natural minutes shown on the "Use estimate (N min)" button.</summary>
    private static async Task<int> ImportBookTextAndReadNaturalAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/adaptation/import?admin=1");
        var book = Path.Combine(AppFixture.FindRepoRoot(), "books", "MaryHadALittleLamb.txt");
        Assert.True(File.Exists(book), $"fixture missing: {book}");
        var fileInput = page.GetByTestId("import-file-input");
        await fileInput.WaitForAsync(new() { Timeout = 30_000 });
        await fileInput.SetInputFilesAsync(book);

        // Book import is a job (prepare + book → Fountain on the fake chat). The card appears on the
        // import page once book text is prepared; if the app moved on to the screenplay, come back.
        var card = page.GetByTestId("film-length-card");
        try
        {
            await card.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 120_000 });
        }
        catch (TimeoutException)
        {
            await page.GotoAsync($"{baseUrl}/adaptation/import?admin=1");
            await card.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        }
        var label = await page.GetByTestId("film-length-estimate").TextContentAsync() ?? "";
        var m = UseEstimateMinutes.Match(label);
        Assert.True(m.Success, $"Use-estimate button should show the natural minutes, was '{label}'");
        return int.Parse(m.Groups[1].Value);
    }
}
