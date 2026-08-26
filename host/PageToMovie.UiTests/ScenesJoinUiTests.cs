using System.Text.Json;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Film scene joins and transition cards (Bug #19): between-scene join selector (ScenesJoinRow)
/// backed by Fountain transitions (cut, dissolve, dip, fadewhite, cuttoblack) and optional
/// [[CARD: ...]] notes that persist to the Fountain screenplay draft.
/// </summary>
[Collection("ui-pipeline")]
public class ScenesJoinUiTests
{
    private readonly PipelineFixture _fx;
    public ScenesJoinUiTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Scene_join_kind_and_card_note_persist_to_fountain()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(
                page, _fx.BaseUrl, "JoinCard_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Verify join row exists
            var joinRows = page.GetByTestId("scene-join-row");
            await Assertions.Expect(joinRows.First).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Select "dissolve" transition on the first join
            await joinRows.First.GetByTestId("scene-join-kind").SelectOptionAsync("dissolve");

            // Type a card note. Both controls save on "change", so the edit has to be committed by
            // moving focus off the field — filling alone only raises "input".
            var cardInput = joinRows.First.GetByTestId("scene-join-card");
            await cardInput.FillAsync("ONE YEAR LATER");
            await cardInput.PressAsync("Tab");

            // Each edit writes the screenplay draft in the background, so poll for the result
            // instead of reading once — a single read races the write and passes only by luck.
            var projectId = await Ui.ServerActiveProjectIdAsync(page);
            var draftText = await WaitForDraftAsync(
                page, projectId!, "DISSOLVE TO:", "[[CARD: ONE YEAR LATER]]");

            Assert.Contains("DISSOLVE TO:", draftText);
            Assert.Contains("[[CARD: ONE YEAR LATER]]", draftText);

            // The screenplay page still opens on that draft.
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/adaptation/screenplay");
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor"))
                .ToBeVisibleAsync(new() { Timeout = 60_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Read the screenplay draft until it carries every expected marker, or time out.</summary>
    private static async Task<string> WaitForDraftAsync(
        IPage page, string projectId, params string[] expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var text = "";
        while (true)
        {
            var raw = await Ui.ApiFetchAsync(page, $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay");
            using (var doc = JsonDocument.Parse(raw))
            {
                text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            }
            if (expected.All(text.Contains) || DateTime.UtcNow >= deadline)
                return text;
            await page.WaitForTimeoutAsync(500);
        }
    }
}
