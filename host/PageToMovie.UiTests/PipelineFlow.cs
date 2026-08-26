using System.Text.RegularExpressions;
using Microsoft.Playwright;

using PageToMovie.Core.Utils;
namespace PageToMovie.UiTests;

/// <summary>
/// Reusable fresh-project pipeline steps (create → pick fake models → import → sign off) shared by
/// the end-to-end and cast-display-variation tests. Everything runs against the fully-faked host with
/// the fake test-vendor catalog, so a fresh project can be driven from nothing to an extracted cast.
/// </summary>
public static class PipelineFlow
{
    /// <summary>Create a fresh project and return on the import page. Works whether the shared
    /// workspace is empty (home shows the easy-start landing, New is behind "Open full studio") or
    /// already has projects (home remembers full-studio mode, New is directly available) — the
    /// pipeline fixture reuses one workspace across tests, so both states occur.</summary>
    public static async Task CreateFreshProjectAsync(IPage page, string baseUrl, string name)
    {
        await page.GotoAsync($"{baseUrl}/?admin=1");
        // "Drop pages" is the home heading in every state — a state-independent readiness marker.
        await page.GetByRole(AriaRole.Heading, new() { Name = "Drop pages" })
                  .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        // A fresh context shows the terms gate; its backdrop covers the home buttons. Dismiss first.
        var agree = page.GetByRole(AriaRole.Button, new() { Name = "Agree & continue" });
        try { await agree.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 }); }
        catch (TimeoutException) { /* already accepted in this context */ }
        await Ui.DismissTermsAsync(page);
        // Reveal full studio only when on the easy-start landing; a non-empty home is already there.
        var openStudio = page.GetByTestId("home-open-full-studio");
        try
        {
            await openStudio.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            await openStudio.ClickAsync();
        }
        catch (TimeoutException) { /* already in full studio */ }
        await page.GetByTestId("home-new-project").First.ClickAsync(new() { Timeout = 30_000 });
        await page.GetByTestId("home-new-project-name").FillAsync(name);
        var createBtn = page.GetByTestId("home-create-project");
        await createBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await createBtn.ClickAsync();
        await page.WaitForURLAsync(new Regex("adaptation/import", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 60_000, WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.EvaluateAsync($"() => sessionStorage.setItem('ptm.test.currentProject', '{name}')");
    }

    /// <summary>Point every studio job at the key-free fake test vendor for the active project, via
    /// the app's own config API (authed with the browser session) — a stable "arrange" step; the
    /// Settings model-picker UI is covered separately and is slow/flaky to drive per fixture.</summary>
    public static async Task SelectFakeModelsAsync(IPage page)
    {
        var result = await page.EvaluateAsync<string>(@"async () => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return JSON.stringify({err:'no session'});
            const s = JSON.parse(raw);
            const tok = s.Token || s.token; const uid = s.UserId || s.userId || '';
            const h = {'Authorization':'Bearer '+tok, 'X-User-Id':uid, 'Content-Type':'application/json'};
            const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
            const list = pr.projects || pr.Projects || [];
            const targetName = sessionStorage.getItem('ptm.test.currentProject') || '';
            let a = targetName ? list.find(x => (x.id||'').includes(targetName) || (x.label||'').includes(targetName)) : null;
            if (!a) a = pr.active || pr.Active || list[0] || {};
            const id = a.id || a.Id || '';
            if (!id) return JSON.stringify({err:'no active project', keys:Object.keys(pr)});
            const body = { version:2,
                video_provider:'fake', image_provider:'fake', character_design_provider:'fake',
                planning_provider:'fake', vision_provider:'fake', quality_provider:'fake',
                model_name:'fake-video', image_model_name:'fake-image', planning_model_name:'fake-script',
                vision_model_name:'fake-vision', quality_model_name:'fake-vision',
                audio_model_name:'fake-music', voice_model_name:'fake-voice',
                model_selections:{video:'fake-video',image:'fake-image',chat:'fake-script',vision:'fake-vision',audio:'fake-music','video-edit':'fake-video-edit'} };
            // The import page (where CreateFreshProjectAsync leaves us) may still be finishing its own
            // config write; a PUT that lands before it gets overwritten. Verify and retry a few times.
            let status = 0, seen = '';
            for (let attempt = 0; attempt < 4; attempt++) {
                const res = await fetch('/api/projects/'+encodeURIComponent(id)+'/config',
                    {method:'PUT', headers:h, body:JSON.stringify(body)});
                status = res.status;
                if (status !== 200) break;
                await new Promise(r => setTimeout(r, 750));
                const chk = await fetch('/api/projects/'+encodeURIComponent(id)+'/config', {headers:h}).then(r=>r.json());
                seen = ((chk.config||{}).planning_model_name) || '';
                if (seen === 'fake-script') break;
            }
            return JSON.stringify({id, status, seen});
        }");
        Assert.DoesNotContain("\"err\"", result);
        Assert.Contains("\"status\":200", result);
        Assert.Contains("\"seen\":\"fake-script\"", result);
    }

    /// <summary>Import a Fountain fixture; waits for the app's own auto-navigation to the screenplay
    /// page (which only happens on a successful import).</summary>
    public static async Task ImportFountainAsync(IPage page, string baseUrl, string fixtureFile)
    {
        await page.GotoAsync($"{baseUrl}/adaptation/import?admin=1");
        var fountain = Path.Combine(AppFixture.FindRepoRoot(), "host", "playwright", "fixtures", fixtureFile);
        Assert.True(File.Exists(fountain), $"fixture missing: {fountain}");
        var input = page.GetByTestId("import-file-input");
        await input.WaitForAsync(new() { Timeout = 30_000 });
        await input.SetInputFilesAsync(fountain);
        try
        {
            await page.WaitForURLAsync(new Regex("adaptation/screenplay", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            var continueBtn = page.GetByTestId("import-continue-screenplay");
            if (await continueBtn.CountAsync() > 0 && await continueBtn.IsVisibleAsync())
            {
                await continueBtn.ClickAsync();
                await page.WaitForURLAsync(new Regex("adaptation/screenplay", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 30_000 });
                return;
            }
            var gate = page.GetByTestId("import-setup-required");
            var blocked = await gate.CountAsync() > 0 ? await gate.TextContentAsync() : "(not gated)";
            Assert.Fail($"Fountain import did not navigate to screenplay. setup gate: {blocked?.Trim()}");
        }
    }

    /// <summary>Approve the screenplay draft; the server extracts cast then the app navigates to
    /// the Estimate / DecisionCard page (<c>/cost</c>, since dba5b330 "approve lands on Estimate").
    /// Caller waits for that navigation (<see cref="WaitForSignOffLandingAsync"/>).</summary>
    public static async Task SignOffScreenplayAsync(IPage page)
    {
        var status = page.GetByTestId("screenplay-status");
        await status.WaitForAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(status).ToHaveAttributeAsync("data-draft", "true", new() { Timeout = 60_000 });

        var signoff = page.GetByTestId("screenplay-signoff");
        await signoff.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 45_000 });
        await Assertions.Expect(signoff).ToBeEnabledAsync(new() { Timeout = 60_000 });
        await signoff.ClickAsync();
        await page.WaitForURLAsync(new Regex("(/cost|/characters)", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 60_000 });
    }

    /// <summary>After sign-off the app lands on Estimate (<c>/cost</c>). This is the one place that
    /// knows the landing route, so a product change there is a one-line test update.</summary>
    public static Task WaitForSignOffLandingAsync(IPage page) =>
        page.WaitForURLAsync(new Regex("/cost", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 90_000 });

    /// <summary>Run the whole flow for a fixture and land on the Characters page (via the nav link,
    /// which the sign-off unlocked — cast is extracted server-side during approve).</summary>
    public static async Task RunToCharactersAsync(IPage page, string baseUrl, string projectName, string fixtureFile)
    {
        await CreateFreshProjectAsync(page, baseUrl, projectName);
        await SelectFakeModelsAsync(page);
        await ImportFountainAsync(page, baseUrl, fixtureFile);
        await SignOffScreenplayAsync(page);
        await WaitForSignOffLandingAsync(page);
        await page.GetByTestId("nav-characters").ClickAsync(new() { Timeout = 30_000 });
        await page.WaitForURLAsync(new Regex("/characters", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 30_000 });
    }

    /// <summary>Build the shot plan (Stage 2) for the active project and wait for the job to finish.
    /// Runs via the app's job API (authed browser fetch) — building the plan does not require locked
    /// portraits (that gate is only for video generation), so this reaches the Scenes display from an
    /// extracted cast. The ~15 Stage-2 classifiers resolve to the fake chat client.</summary>
    public static async Task BuildShotPlanAsync(IPage page)
    {
        var result = await page.EvaluateAsync<string>(@"async () => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return JSON.stringify({err:'no session'});
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||''), 'Content-Type':'application/json'};
            const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
            const list = pr.projects || pr.Projects || [];
            const targetName = sessionStorage.getItem('ptm.test.currentProject') || '';
            let p = targetName ? list.find(x => (x.id||'').includes(targetName) || (x.label||'').includes(targetName)) : null;
            if (!p) p = pr.active || pr.Active || list[0] || {};
            const id = p.id || p.Id;
            if (!id) return JSON.stringify({err:'no active project'});
            const start = await fetch('/api/jobs/stage2', {method:'POST', headers:h,
                body: JSON.stringify({projectId:id, scenes:'all'})}).then(r=>r.json());
            const j = start.job || {};
            const jobId = j.jobId || j.JobId || j.id;
            if (!jobId) return JSON.stringify({err:'no jobId', start});
            const deadline = Date.now() + 90000;
            let last = '';
            while (Date.now() < deadline) {
                await new Promise(r => setTimeout(r, 1500));
                const st = await fetch('/api/jobs/'+encodeURIComponent(jobId), {headers:h}).then(r=>r.json());
                last = (st.job||{}).status || (st.job||{}).Status;
                if (last === 'done') return JSON.stringify({ok:true});
                if (last === 'error' || last === 'cancelled') return JSON.stringify({err:'job '+last, job:st.job, activeId:id, projects:(pr.projects||[]).map(p=>p.id), clientProject:(sessionStorage.getItem('PageToMovie.activeProject')||localStorage.getItem('PageToMovie.activeProject')||''), url:location.href});
            }
            return JSON.stringify({err:'timeout', last});
        }");
        Assert.True(!result.Contains("\"err\"") && result.Contains("\"ok\":true"),
            "Stage 2 (shot plan) did not finish: " + result + Environment.NewLine + "API log (sign-off/projects lines): " + AppFixture.TailApiLog(AppFixture.CurrentApiLogPath, "[sign-off]") + Environment.NewLine + AppFixture.TailApiLog(AppFixture.CurrentApiLogPath, "[projects]"));
    }

    /// <summary>Full flow through to the Scenes page with a built shot plan.</summary>
    public static async Task RunToScenesAsync(IPage page, string baseUrl, string projectName, string fixtureFile)
    {
        await RunToCharactersAsync(page, baseUrl, projectName, fixtureFile);
        await BuildShotPlanAsync(page);
        await Ui.GotoAppAsync(page, baseUrl, "/scenes");
    }

    /// <summary>Make every character ready for video: generate a portrait variant (fake image), lock
    /// it (passes the fake style gate), and set a voice. Done via the app's own APIs (authed browser
    /// fetch) — this is the "ready for shots" arrange that the video-generation spend gate requires.
    /// Verified live: without a locked+voiced cast, gen-batch throws "Cast not ready for video".</summary>
    public static async Task MakeCastReadyForShotsAsync(IPage page)
    {
        var result = await page.EvaluateAsync<string>(@"async () => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return JSON.stringify({err:'no session'});
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||''), 'Content-Type':'application/json'};
            const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
            const list = pr.projects || pr.Projects || [];
            const targetName = sessionStorage.getItem('ptm.test.currentProject') || '';
            let p = targetName ? list.find(x => (x.id||'').includes(targetName) || (x.label||'').includes(targetName)) : null;
            if (!p) p = pr.active || pr.Active || list[0] || {};
            const id = p.id || p.Id;
            if (!id) return JSON.stringify({err:'no active project'});
            const E = encodeURIComponent(id);
            const poll = async (jobId) => {
                if (!jobId) return 'no jobId';
                const dl = Date.now()+60000; let last='';
                while (Date.now()<dl) { await new Promise(r=>setTimeout(r,1000));
                    const st = await fetch('/api/jobs/'+encodeURIComponent(jobId), {headers:h}).then(r=>r.json()).catch(()=>({}));
                    last = (st.job||{}).status; if (last==='done') return 'done';
                    if (last==='error'||last==='cancelled') return 'ERR:'+JSON.stringify(st.job).slice(0,150); }
                return 'timeout'; };
            // Cast extraction runs server-side on approve; on a busy host it can land a beat after the
            // page moved on. Poll rather than read once.
            let chars = [];
            const castDl = Date.now() + 60000;
            while (Date.now() < castDl) {
                chars = (await fetch('/api/projects/'+E+'/characters', {headers:h}).then(r=>r.json()).catch(()=>({}))).characters || [];
                if (chars.length) break;
                await new Promise(r=>setTimeout(r,1500));
            }
            if (!chars.length) {
                // Diagnostic for the intermittent case: was the chat model configured on this project
                // when sign-off ran? (sign-off skips extraction when the chat client is unconfigured).
                const cfg = await fetch('/api/projects/'+E+'/config', {headers:h}).then(r=>r.json()).catch(e=>({fetchErr:String(e)}));
                const c = cfg.config||cfg.Config||cfg;
                return JSON.stringify({err:'no cast extracted', activeId:id,
                    chat_model:c.chat_model_name||c.ChatModelName, planning_model:c.planning_model_name||c.PlanningModelName,
                    chat_provider:c.chat_provider||c.ChatProvider});
            }
            for (const c of chars) {
                const key = c.key||c.Key; const K = encodeURIComponent(key);
                let v = {};
                for (let attempt = 0; attempt < 10; attempt++) {
                    v = await fetch('/api/jobs/character-variants', {method:'POST', headers:h,
                        body: JSON.stringify({projectId:id, charKey:key, count:1, seedMode:'auto'})}).then(r=>r.json()).catch(()=>({}));
                    if (v.ok && ((v.job||{}).jobId || (v.job||{}).id)) break;
                    await new Promise(r=>setTimeout(r,1500));
                }
                const vp = await poll((v.job||{}).jobId||(v.job||{}).id);
                if (vp !== 'done') return JSON.stringify({err:'variants '+vp, key, v});
                let lk = {};
                for (let attempt = 0; attempt < 10; attempt++) {
                    lk = await fetch('/api/projects/'+E+'/characters/'+K+'/lock-variant', {method:'POST', headers:h,
                        body: JSON.stringify({index:1})}).then(r=>r.json()).catch(()=>({}));
                    if (lk.ok) break;
                    await new Promise(r=>setTimeout(r,1500));
                }
                if (!lk.ok) return JSON.stringify({err:'lock failed', key, detail:(lk.error||'').slice(0,120)});
                const vo = await fetch('/api/projects/'+E+'/characters/'+K+'/voice', {method:'POST', headers:h,
                    body: JSON.stringify({voiceProfile:'Adult male, 40s, test voice', voiceLabel:key})}).then(r=>r.json()).catch(()=>({}));
                if (!vo.ok) return JSON.stringify({err:'voice failed', key});
            }
            return JSON.stringify({ok:true, count:chars.length});
        }");
        Assert.True(!result.Contains("\"err\"") && result.Contains("\"ok\":true"),
            "Stage 2 (shot plan) did not finish: " + result + Environment.NewLine + "API log (sign-off/projects lines): " + AppFixture.TailApiLog(AppFixture.CurrentApiLogPath, "[sign-off]") + Environment.NewLine + AppFixture.TailApiLog(AppFixture.CurrentApiLogPath, "[projects]"));
    }

    /// <summary>Generate clips for every scene (fake video copies MP4 fixtures) and wait for the job.
    /// Requires a built shot plan and a ready-for-shots cast.</summary>
    public static async Task GenerateClipsAsync(IPage page)
    {
        var result = await page.EvaluateAsync<string>(@"async () => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return JSON.stringify({err:'no session'});
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||''), 'Content-Type':'application/json'};
            const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json());
            const list = pr.projects || pr.Projects || [];
            const targetName = sessionStorage.getItem('ptm.test.currentProject') || '';
            let p = targetName ? list.find(x => (x.id||'').includes(targetName) || (x.label||'').includes(targetName)) : null;
            if (!p) p = pr.active || pr.Active || list[0] || {};
            const id = p.id || p.Id;
            if (!id) return JSON.stringify({err:'no active project'});
            const E = encodeURIComponent(id);
            const scenes = ((await fetch('/api/projects/'+E+'/scenes', {headers:h}).then(r=>r.json())).scenes || [])
                .map(x => x.sceneNumber || x.SceneNumber);
            if (!scenes.length) return JSON.stringify({err:'no scenes'});
            const gb = await fetch('/api/jobs/gen-batch', {method:'POST', headers:h,
                body: JSON.stringify({projectId:id, scenes, onlyMissing:false})}).then(r=>r.json());
            const jobId = (gb.job||{}).jobId || (gb.job||{}).id;
            if (!jobId) return JSON.stringify({err:'no jobId', gb});
            const dl = Date.now()+120000; let last='';
            while (Date.now()<dl) { await new Promise(r=>setTimeout(r,1500));
                const st = await fetch('/api/jobs/'+encodeURIComponent(jobId), {headers:h}).then(r=>r.json());
                const j = st.job||{}; last = j.status;
                // 'partial' is terminal too: the end-credits scene renders client-side, so a job-API
                // batch never reaches 'done' — the real Generate Batch click afterwards fills it.
                if (last==='done' || (last==='partial' && (j.isFinished ?? true))) return JSON.stringify({ok:true, last});
                if (last==='error'||last==='cancelled') return JSON.stringify({err:'gen '+last, job:st.job}); }
            return JSON.stringify({err:'timeout', last});
        }");
        Assert.True(!result.Contains("\"err\"") && result.Contains("\"ok\":true"), "Clip generation (job API) did not finish: " + result);
    }

    /// <summary>
    /// Finish generation via the real Scenes page "Generate Batch" button (select all -> confirm
    /// modal -> go), not another fetch() shortcut. <see cref="GenerateClipsAsync"/>'s direct batch
    /// call deliberately leaves the end-credits scene ungenerated — the server refuses to send it to
    /// the video model (a title card makes it hallucinate unrelated footage; see
    /// FilmJobService.RunBatchGenAsync's IsCreditsScene skip) — so the only thing that can still fill
    /// it in is the deterministic client-side card render (canvas -> ffmpeg.wasm) that Scenes.razor's
    /// own batch handler runs for credits scenes. Driving the real button click (rather than calling
    /// that JS directly) proves the production click path, the same one a user hits.
    /// </summary>
    public static async Task FinishViaGenerateBatchButtonAsync(IPage page, string baseUrl)
    {
        await Ui.GotoAppAsync(page, baseUrl, "/scenes");
        var status = page.GetByTestId("scenes-status");
        await status.WaitForAsync(new() { Timeout = 30_000 });

        await page.GetByTestId("scenes-select-all").CheckAsync(new() { Timeout = 15_000 });
        var batchBtn = page.GetByTestId("scenes-generate-batch");
        await Assertions.Expect(batchBtn).ToBeEnabledAsync(new() { Timeout = 30_000 });
        await batchBtn.ClickAsync(new() { Timeout = 15_000 });
        try
        {
            await page.GetByTestId("generate-confirm-go").ClickAsync(new() { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            // Say what the page was doing instead of the confirm modal — enabled/disabled button,
            // any error banner, cast/selection state — so a regression here is diagnosable.
            var diag = await page.EvaluateAsync<string>(@"() => {
                const q = s => document.querySelector(s);
                const btn = q('[data-testid=scenes-generate-batch]');
                const err = Array.from(document.querySelectorAll('.alert-danger, .alert-warning')).map(e => e.textContent.trim()).join(' | ');
                const st = q('[data-testid=scenes-status]');
                return JSON.stringify({ btnDisabled: btn ? btn.disabled : null, btnText: btn ? btn.textContent.trim() : null,
                    modal: !!q('[data-testid=generate-confirm-modal]'), errors: err,
                    status: st ? Object.assign({}, st.dataset) : null, url: location.href });
            }");
            Assert.Fail("Generate Batch did not open the confirm modal. Page: " + diag);
        }

        var clips = int.Parse(await status.GetAttributeAsync("data-clip-count") ?? "0");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var onDisk = int.Parse(await status.GetAttributeAsync("data-clips-on-disk") ?? "0");
            if (onDisk >= clips) return;
            await page.WaitForTimeoutAsync(500);
        }
        var finalOnDisk = await status.GetAttributeAsync("data-clips-on-disk");
        var jobState = await page.EvaluateAsync<string>(@"async () => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session'); if (!raw) return 'no session';
            const s = JSON.parse(raw); const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
            const j = await fetch('/api/jobs?mine=1', {headers:h}).then(r=>r.json()).catch(e=>({e:String(e)}));
            return JSON.stringify(j).slice(0, 1500); }");
        Assert.Fail($"Generate Batch via the Scenes page did not finish within 60s: clips={clips}, onDisk={finalOnDisk}. Jobs: {jobState}");
    }

    /// <summary>Full flow through to generated clips on the Scenes page — including the end-credits
    /// scene, which only the real "Generate Batch" button click (not the fetch shortcut) can fill in.</summary>
    public static async Task RunToGeneratedClipsAsync(IPage page, string baseUrl, string projectName, string fixtureFile)
    {
        await RunToCharactersAsync(page, baseUrl, projectName, fixtureFile);
        await MakeCastReadyForShotsAsync(page);
        await BuildShotPlanAsync(page);
        await GenerateClipsAsync(page);
        await FinishViaGenerateBatchButtonAsync(page, baseUrl);
    }
}
