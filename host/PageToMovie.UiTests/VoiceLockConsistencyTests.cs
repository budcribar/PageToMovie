using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Regression guard for a live-found bug (2026-08-19): after editing the narrator's voice profile,
/// the Characters page showed 🔒 while the Film page / scene index still said Cast Unlocked. The
/// Film page's cast gate must track voice-profile edits in BOTH directions: a sexless/ageless
/// profile on a speaking role unlocks the cast; restoring sex+age locks it again.
/// </summary>
[Collection("ui-pipeline")]
public class VoiceLockConsistencyTests
{
    private readonly PipelineFixture _fx;
    public VoiceLockConsistencyTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Film_page_cast_lock_tracks_narrator_voice_profile_edits()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl,
                "VLock_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");
            await PipelineFlow.MakeCastReadyForShotsAsync(page);

            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await Assertions.Expect(page.GetByText("Cast Locked").First).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Strip sex+age from the narrator's profile — a speaking role without a pinned voice.
            var res = await SetNarratorVoiceAsync(page, "A warm storytelling voice.");
            Assert.Contains("ok", res);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await Assertions.Expect(page.GetByText("Cast Unlocked").First).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Restore a pinned profile — Film page must flip back to locked, matching Characters.
            res = await SetNarratorVoiceAsync(page, "Warm adult male storytelling voice, deep and calm.");
            Assert.Contains("ok", res);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await Assertions.Expect(page.GetByText("Cast Locked").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    private static Task<string> SetNarratorVoiceAsync(IPage page, string profile) =>
        page.EvaluateAsync<string>(@"async (profile) => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||''), 'Content-Type':'application/json'};
            const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
            const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
            const E = encodeURIComponent(id);
            const chars = (await fetch('/api/projects/'+E+'/characters', {headers:h}).then(r=>r.json())).characters || [];
            const n = chars.find(c => /narrator/i.test(c.key||c.Key||'') || /narrator/i.test(c.name||c.Name||''));
            if (!n) return 'no narrator: ' + chars.map(c=>c.key||c.Key).join(',');
            const r = await fetch('/api/projects/'+E+'/characters/'+encodeURIComponent(n.key||n.Key)+'/voice',
                {method:'POST', headers:h, body: JSON.stringify({voiceProfile: profile, voiceLabel: 'Narrator voice'})}).then(r=>r.json());
            return r.ok ? 'ok' : JSON.stringify(r);
        }", profile);
}
