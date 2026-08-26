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
            var kindSelect = joinRows.First.GetByTestId("scene-join-kind");
            await kindSelect.SelectOptionAsync("dissolve");

            // Type a card note
            var cardInput = joinRows.First.GetByTestId("scene-join-card");
            await cardInput.FillAsync("ONE YEAR LATER");
            await cardInput.BlurAsync();

            // Navigate to screenplay and verify the transition + card note exist in the draft
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/adaptation/screenplay");
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });

            var draftText = await page.EvaluateAsync<string>(@"async () => {
                const raw = sessionStorage.getItem('PageToMovie.admin.session');
                const s = JSON.parse(raw);
                const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                const sp = await fetch('/api/projects/'+encodeURIComponent(id)+'/screenplay', {headers:h}).then(r=>r.json());
                return (sp.text||sp.Text||'');
            }");

            Assert.Contains("DISSOLVE TO:", draftText);
            Assert.Contains("[[CARD: ONE YEAR LATER]]", draftText);
        }
        finally { await ctx.CloseAsync(); }
    }
}
