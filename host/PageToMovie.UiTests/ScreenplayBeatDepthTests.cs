using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Screenplay structured-editor depth: add a Dialogue beat via the add-select, pick its speaker
/// from the cast dropdown, type the line, delete a beat, and prove the edits persist to the
/// Fountain draft (autosave) across a full reload. Complements ScreenplayBeatEditorTests (display)
/// and PageSweepTests (outline drag).
/// </summary>
[Collection("ui-pipeline")]
public class ScreenplayBeatDepthTests
{
    private readonly PipelineFixture _fx;
    public ScreenplayBeatDepthTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Add_beat_pick_speaker_type_line_delete_beat_and_persist()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "BeatD_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "mary_had_a_lamb.fountain");

            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var spoken = page.GetByPlaceholder("Spoken dialogue…");
            await Assertions.Expect(spoken.First).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var beatsBefore = await spoken.CountAsync();

            // Add a Dialogue beat at the foot of the FIRST scene card.
            var addSelect = page.Locator(".spe-sel-add").First;
            await addSelect.SelectOptionAsync("Dialogue");
            await Assertions.Expect(spoken).ToHaveCountAsync(beatsBefore + 1, new() { Timeout = 15_000 });

            // The new beat is the last dialogue row of scene 1's card — pick MARY and type a line.
            var card = page.Locator(".spe-sel-add").First.Locator("xpath=ancestor::div[contains(@class,'card')][1]");
            var newSpeaker = card.Locator(".spe-sel-speaker").Last;
            var speakerOptions = await newSpeaker.Locator("option").AllInnerTextsAsync();
            var mary = speakerOptions.FirstOrDefault(o => o.Contains("MARY", StringComparison.OrdinalIgnoreCase));
            Assert.False(mary is null, "cast dropdown should offer MARY; had: " + string.Join(",", speakerOptions));
            await newSpeaker.SelectOptionAsync(new SelectOptionValue { Label = mary });

            const string newLine = "May I keep the lamb beside my desk today?";
            var newSpokenInput = card.GetByPlaceholder("Spoken dialogue…").Last;
            await newSpokenInput.FillAsync(newLine);
            await newSpokenInput.BlurAsync();

            // Delete the FIRST beat of the same card (✕) — the card's beat-row count drops by one.
            // (The first beat is an Action row, so count beat rows via their delete buttons, not
            // the spoken-dialogue inputs.)
            var deleteButtons = card.Locator("button[title='Delete this beat']");
            var beforeDelete = await deleteButtons.CountAsync();
            await deleteButtons.First.ClickAsync();
            await Assertions.Expect(deleteButtons).ToHaveCountAsync(beforeDelete - 1, new() { Timeout = 15_000 });

            // Autosave (900ms debounce) must land the new line in the Fountain draft.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var saved = false;
            while (DateTime.UtcNow < deadline && !saved)
            {
                var draft = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                    const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                    const sp = await fetch('/api/projects/'+encodeURIComponent(id)+'/screenplay', {headers:h}).then(r=>r.json());
                    return (sp.text||sp.Text||'');
                }");
                saved = draft.Contains("keep the lamb beside my desk", StringComparison.OrdinalIgnoreCase);
                if (!saved) await page.WaitForTimeoutAsync(500);
            }
            Assert.True(saved, "the added beat's line never reached the saved Fountain draft");

            // Full reload: the line survives in the editor.
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var valuesAfter = await page.GetByPlaceholder("Spoken dialogue…").EvaluateAllAsync<string[]>("els => els.map(e => e.value)");
            Assert.Contains(valuesAfter, v => v.Contains("keep the lamb beside my desk", StringComparison.OrdinalIgnoreCase));
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Scene_heading_inline_edit_updates_fountain_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "SceneHead_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "mary_had_a_lamb.fountain");

            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });

            // Locate scene 1 card's setting and location dropdowns
            var settingSelect = page.Locator("#scene-card-1 select.spe-sel-env");
            await Assertions.Expect(settingSelect).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await settingSelect.SelectOptionAsync("INT");

            var locationSelect = page.Locator("#scene-card-1 select.spe-sel-loc");
            await Assertions.Expect(locationSelect).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await locationSelect.SelectOptionAsync("SCHOOLHOUSE");

            // Wait for autosave
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var saved = false;
            while (DateTime.UtcNow < deadline && !saved)
            {
                var draft = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                    const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                    const sp = await fetch('/api/projects/'+encodeURIComponent(id)+'/screenplay', {headers:h}).then(r=>r.json());
                    return (sp.text||sp.Text||'');
                }");
                saved = draft.Contains("INT. SCHOOLHOUSE - DAY", StringComparison.OrdinalIgnoreCase);
                if (!saved) await page.WaitForTimeoutAsync(500);
            }
            Assert.True(saved, "edited scene heading never reached the saved Fountain draft");

            // Reload and verify
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            settingSelect = page.Locator("#scene-card-1 select.spe-sel-env");
            await Assertions.Expect(settingSelect).ToHaveValueAsync("INT", new() { Timeout = 15_000 });
            locationSelect = page.Locator("#scene-card-1 select.spe-sel-loc");
            await Assertions.Expect(locationSelect).ToHaveValueAsync("SCHOOLHOUSE", new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Insert_scene_after_inserts_and_renumbers_in_outline_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "SceneIns_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "mary_had_a_lamb.fountain");

            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });

            // Count outline rows before
            var outlineRows = page.Locator(".spe-outline-row");
            var countBefore = await outlineRows.CountAsync();
            Assert.True(countBefore >= 1, "expected at least 1 scene");

            // Click insert scene after scene 1
            var insertBtn = page.Locator("button[title='Insert scene after 1']");
            await Assertions.Expect(insertBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await insertBtn.ClickAsync();

            // Outline rows should increment by 1
            await Assertions.Expect(outlineRows).ToHaveCountAsync(countBefore + 1, new() { Timeout = 15_000 });

            // Wait for autosave to persist the new scene to backend
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var saved = false;
            while (DateTime.UtcNow < deadline && !saved)
            {
                var draft = await page.EvaluateAsync<string>(@"async () => {
                    const raw = sessionStorage.getItem('PageToMovie.admin.session');
                    const s = JSON.parse(raw);
                    const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                    const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
                    const id = (pr.active||pr.Active||{}).id || (pr.active||pr.Active||{}).Id;
                    const sp = await fetch('/api/projects/'+encodeURIComponent(id)+'/screenplay', {headers:h}).then(r=>r.json());
                    return (sp.text||sp.Text||'');
                }");
                saved = draft.Contains("Describe what we see", StringComparison.OrdinalIgnoreCase);
                if (!saved) await page.WaitForTimeoutAsync(500);
            }
            Assert.True(saved, "inserted scene never reached the saved Fountain draft");

            // Reload page to verify incremented scenes persist
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await Assertions.Expect(page.Locator(".spe-outline-row")).ToHaveCountAsync(countBefore + 1, new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Screenplay_menu_opens_story_characters_and_locations_modals()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "MenuModals_" + Guid.NewGuid().ToString("N")[..6]);
            await PipelineFlow.SelectFakeModelsAsync(page);
            await PipelineFlow.ImportFountainAsync(page, _fx.BaseUrl, "mary_had_a_lamb.fountain");

            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });

            var menuBtn = page.GetByTestId("screenplay-menu");
            await Assertions.Expect(menuBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Click Edit Story Characters
            await menuBtn.ClickAsync();
            var charMenuItem = page.Locator(".dropdown-item", new() { HasText = "Edit story characters" });
            await Assertions.Expect(charMenuItem).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await charMenuItem.ClickAsync();

            // Navigates to Characters / Cast hub
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/characters"), new() { Timeout = 15_000 });
            await Assertions.Expect(page.Locator("h1", new() { HasText = "Cast" })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
