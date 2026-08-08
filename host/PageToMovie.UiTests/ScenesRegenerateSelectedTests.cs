using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PageToMovie.Core.Options;
using PageToMovie.Engine;

namespace PageToMovie.UiTests;

/// <summary>
/// E2E coverage for "Regenerate Selected Scenes" (renamed from "Rebuild Shot Plan" —
/// ScenesPage.RebuildShotPlan). Scenes.razor.cs's RebuildShotPlanAsync now scopes
/// Engine.StartStage2Async's Scenes param to the page's checkbox selection (_selected) instead of
/// always sending "all", wiring up Stage2PlannerService.PlanAsync's existing scoped-merge path
/// (MergePlannedScenes keeps every scene not in scope byte-for-byte — same object — from the existing
/// blueprint) versus the full-rebuild path when nothing is checked.
/// </summary>
[Collection("ui-pipeline")]
public class ScenesRegenerateSelectedTests
{
    private readonly PipelineFixture _fx;
    public ScenesRegenerateSelectedTests(PipelineFixture fx) => _fx = fx;

    private ProjectStore Store() =>
        new(Options.Create(new PageToMovieOptions { WorkspaceRoot = _fx.WorkspaceRootPath, EnableReadCaches = false }));

    private string BlueprintPath(string projectId) =>
        Path.Combine(Store().GetProjectDir(projectId), "blueprint.clips.grok.json");

    private static JsonNode? SceneNode(JsonObject root, int sceneNumber) =>
        root["scenes"]!.AsArray().FirstOrDefault(s => (int?)s!["scene_number"] == sceneNumber);

    [Fact]
    public async Task Checking_one_scene_scopes_regen_to_it_and_leaves_others_untouched()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var name = "Regen_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, name, "tell_tale_heart.fountain");

            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var sceneCountBefore = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            // tell_tale_heart.fountain has 4 real scene headings + 1 auto-inserted credits scene —
            // need at least scenes 1 and 2 as real, distinct scenes to prove scoping.
            Assert.True(sceneCountBefore >= 3, $"need >=3 scenes to prove scoping, got {sceneCountBefore}");

            var activeId = Ui.ActiveProjectId(_fx.WorkspaceRootPath);
            Assert.False(string.IsNullOrWhiteSpace(activeId));

            var beforeRoot = JsonNode.Parse(await File.ReadAllTextAsync(BlueprintPath(activeId!)))!.AsObject();
            var scene1Before = SceneNode(beforeRoot, 1);
            var scene2Before = SceneNode(beforeRoot, 2);
            Assert.NotNull(scene1Before);
            Assert.NotNull(scene2Before);

            // Snapshot in-flight jobs so we can find the NEW stage2 job this test triggers — the
            // earlier full-plan build (RunToScenesAsync -> BuildShotPlanAsync) already queued one.
            var beforeJobIds = await GetJobIdsAsync(page, activeId!);

            // Check only scene 1's row, then click the real "Regenerate Selected Scenes" button.
            var scene1Row = page.Locator("[data-testid='scene-row'][data-scene-number='1']");
            await Assertions.Expect(scene1Row).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await scene1Row.Locator("input[type=checkbox]").CheckAsync();

            var regenBtn = page.GetByRole(AriaRole.Button, new() { Name = "Regenerate Selected Scenes" });
            await Assertions.Expect(regenBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            // Tooltip must reflect the scoped selection before we click (wired to _selected.Count).
            await Assertions.Expect(regenBtn).ToHaveAttributeAsync(
                "title", new Regex("only the 1 checked scene"), new() { Timeout = 10_000 });
            await regenBtn.ClickAsync();

            await WaitForNewStage2JobDoneAsync(page, activeId!, beforeJobIds);

            var afterRoot = JsonNode.Parse(await File.ReadAllTextAsync(BlueprintPath(activeId!)))!.AsObject();
            var scene2After = SceneNode(afterRoot, 2);
            Assert.NotNull(scene2After);

            // The proof: scene 2 (never checked) is structurally identical to what it was before this
            // scoped regen — the merge path left it alone rather than rebuilding the whole plan.
            Assert.True(JsonNode.DeepEquals(scene2Before, scene2After),
                "scene 2 (not checked) changed after a regen scoped to scene 1 only:\n" +
                $"before: {scene2Before!.ToJsonString()}\nafter: {scene2After!.ToJsonString()}");

            // stage2_meta.scene_filter records the scope Stage2PlannerService actually used — "1", not "all".
            Assert.Equal("1", (string?)afterRoot["stage2_meta"]?["scene_filter"]);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Nothing_checked_still_does_a_full_rebuild()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var name = "RegenAll_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, name, "tell_tale_heart.fountain");

            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 60_000 });
            var sceneCountBefore = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.True(sceneCountBefore >= 1, $"expected a built shot plan, got {sceneCountBefore} scenes");

            var activeId = Ui.ActiveProjectId(_fx.WorkspaceRootPath);
            Assert.False(string.IsNullOrWhiteSpace(activeId));
            var beforeJobIds = await GetJobIdsAsync(page, activeId!);

            // Nothing checked (fresh page, no prior selection) — button falls back to "all".
            var regenBtn = page.GetByRole(AriaRole.Button, new() { Name = "Regenerate Selected Scenes" });
            await Assertions.Expect(regenBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(regenBtn).ToHaveAttributeAsync(
                "title", new Regex("nothing checked, this rebuilds every scene"), new() { Timeout = 10_000 });
            await regenBtn.ClickAsync();

            await WaitForNewStage2JobDoneAsync(page, activeId!, beforeJobIds);

            var afterRoot = JsonNode.Parse(await File.ReadAllTextAsync(BlueprintPath(activeId!)))!.AsObject();
            Assert.Equal("all", (string?)afterRoot["stage2_meta"]?["scene_filter"]);

            // Existing "restore missing scenes" behavior: full rebuild, scene count unchanged/correct.
            await status.WaitForAsync(new() { Timeout = 30_000 });
            var sceneCountAfter = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            Assert.Equal(sceneCountBefore, sceneCountAfter);
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Job ids currently known for the project (before triggering a new one), via the app's
    /// own job-list API (authed browser fetch) — same "arrange"-style helper pattern as PipelineFlow.</summary>
    private static async Task<List<string>> GetJobIdsAsync(IPage page, string projectId)
    {
        var json = await page.EvaluateAsync<string>(@"async (projectId) => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return JSON.stringify([]);
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
            const r = await fetch('/api/jobs?projectId='+encodeURIComponent(projectId), {headers:h}).then(r=>r.json());
            const jobs = r.jobs || r.Jobs || [];
            return JSON.stringify(jobs.map(j => j.jobId || j.JobId || j.id || j.Id));
        }", projectId);
        return JsonSerializer.Deserialize<List<string>>(json) ?? new();
    }

    /// <summary>Poll until a stage2 job not present in <paramref name="beforeJobIds"/> reaches "done"
    /// (fails fast on error/cancelled) — the click starts the job async (POST returns once queued), so
    /// this is the same poll-via-fetch pattern PipelineFlow.BuildShotPlanAsync uses, applied to the job
    /// list (the button click doesn't hand the jobId back to the test the way a direct API call would).</summary>
    private static async Task WaitForNewStage2JobDoneAsync(IPage page, string projectId, List<string> beforeJobIds)
    {
        var result = await page.EvaluateAsync<string>(@"async ({projectId, beforeIds}) => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return JSON.stringify({err:'no session'});
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
            const before = new Set(beforeIds);
            const deadline = Date.now() + 60000;
            while (Date.now() < deadline) {
                const r = await fetch('/api/jobs?projectId='+encodeURIComponent(projectId), {headers:h}).then(r=>r.json());
                const jobs = r.jobs || r.Jobs || [];
                const mine = jobs.find(j => {
                    const id = j.jobId || j.JobId || j.id || j.Id;
                    const kind = j.kind || j.Kind || '';
                    return id && !before.has(id) && kind === 'stage2';
                });
                if (mine) {
                    const st = (mine.status || mine.Status || '').toLowerCase();
                    if (st === 'done') return JSON.stringify({ok:true});
                    if (st === 'error' || st === 'cancelled') return JSON.stringify({err:'job '+st, job:mine});
                }
                await new Promise(res => setTimeout(res, 1000));
            }
            return JSON.stringify({err:'timeout'});
        }", new { projectId, beforeIds = beforeJobIds });
        Assert.DoesNotContain("\"err\"", result);
        Assert.Contains("\"ok\":true", result);
    }
}
