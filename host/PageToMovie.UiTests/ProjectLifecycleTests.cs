using System.Text.Json;
using Microsoft.Playwright;
using PageToMovie.Core.Utils;

namespace PageToMovie.UiTests;

/// <summary>
/// Project lifecycle on a generated (Mary-shaped) project: export → import round-trip that
/// re-hydrates clips from sidecars, and fork → edit both sides → "Sync origin" 3-way merge.
/// </summary>
[Collection("ui-pipeline")]
public class ProjectLifecycleTests
{
    private readonly PipelineFixture _fx;
    public ProjectLifecycleTests(PipelineFixture fx) => _fx = fx;

    private static string Uniq(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>Authed browser fetch → JSON string (the app's own session headers).</summary>
    private static Task<string> ApiAsync(IPage page, string method, string path, string? body = null) =>
        page.EvaluateAsync<string>(@"async ([method, path, body]) => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||''), 'Content-Type':'application/json'};
            const r = await fetch(path, {method, headers:h, body: body || undefined});
            return await r.text();
        }", new object?[] { method, path, body });

    [Fact]
    public async Task Generated_project_zip_roundtrip_rehydrates_clips()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        var zipPath = Path.Combine(Path.GetTempPath(), "ptm-mary-export-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            var src = Uniq("MaryExp");
            await PipelineFlow.RunToGeneratedClipsAsync(page, _fx.BaseUrl, src, "mary_had_a_lamb.fountain");

            // Baseline: how much the source project has on the Film page.
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            var status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 30_000 });
            var srcScenes = int.Parse(await status.GetAttributeAsync("data-scene-count") ?? "0");
            var srcClips = int.Parse(await status.GetAttributeAsync("data-clip-count") ?? "0");
            var srcOnDisk = int.Parse(await status.GetAttributeAsync("data-clips-on-disk") ?? "0");
            Assert.True(srcScenes >= 5 && srcOnDisk >= srcClips - 1 && srcClips > 0,
                $"source project incomplete: scenes={srcScenes} clips={srcClips} onDisk={srcOnDisk}");

            await Ui.GotoHomePickerAsync(page);
            var srcId = await Ui.SelectedPickerValueAsync(page);
            await Ui.ApiDownloadAsync(page, ProjectIdRouting.ProjectApi(srcId) + "/export", zipPath);
            Assert.True(new FileInfo(zipPath).Length > 1024, "export zip looks empty");

            // Import back under a new name through the real Home panel.
            var imported = Uniq("MaryImp");
            await page.GetByTestId("home-import-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToBeVisibleAsync();
            await page.GetByTestId("home-import-file").SetInputFilesAsync(zipPath);
            var nameBox = page.GetByTestId("home-import-name");
            await Assertions.Expect(nameBox).Not.ToHaveValueAsync("", new() { Timeout = 10_000 });
            await nameBox.FillAsync(imported);
            await page.GetByTestId("home-import-confirm").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToHaveCountAsync(0, new() { Timeout = 60_000 });

            // Switch to the imported project and prove the Film page re-hydrates the clips: the
            // sidecars in the zip are the provider pointers, so every clip must count as present
            // WITHOUT this browser ever holding the imported project's media files.
            await Ui.GotoHomePickerAsync(page);
            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Label = imported });
            await Ui.AssertSelectedProjectLabelAsync(page, imported);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            status = page.GetByTestId("scenes-status");
            await status.WaitForAsync(new() { Timeout = 30_000 });
            Assert.Equal(srcScenes.ToString(), await status.GetAttributeAsync("data-scene-count"));
            var impClips = int.Parse(await status.GetAttributeAsync("data-clip-count") ?? "0");
            var impOnDisk = int.Parse(await status.GetAttributeAsync("data-clips-on-disk") ?? "0");
            Assert.Equal(srcClips, impClips);
            Assert.True(impOnDisk >= srcOnDisk,
                $"imported project lost clips: source onDisk={srcOnDisk}, imported onDisk={impOnDisk} — sidecar re-hydration broke");
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* ignore */ }
            await ctx.CloseAsync();
        }
    }

    [Fact]
    public async Task Fork_edit_both_sides_then_sync_origin_merges_cleanly()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var origin = Uniq("MaryOrig");
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, origin, "mary_had_a_lamb.fountain");

            await Ui.GotoHomePickerAsync(page);
            var originId = await Ui.SelectedPickerValueAsync(page);

            // Make it forkable through the real visibility select, then fork it.
            await page.GetByTestId("home-manage-projects").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-visibility-select")).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await page.GetByTestId("home-visibility-select").SelectOptionAsync("Open");
            // B2 fixed (2026-08-20): Open is a real mode now — forkable, distinct from the
            // read-only Public option, and it round-trips instead of snapping back to Public.
            await Assertions.Expect(page.GetByTestId("home-visibility-badge"))
                .ToHaveAttributeAsync("data-visibility", "Open", new() { Timeout = 15_000 });

            var forkJson = await ApiAsync(page, "POST", ProjectIdRouting.ProjectApi(originId) + "/fork");
            using var forkDoc = JsonDocument.Parse(forkJson);
            Assert.True(forkDoc.RootElement.GetProperty("ok").GetBoolean(), "fork failed: " + forkJson);
            var forkId = forkDoc.RootElement.GetProperty("id").GetString()!;

            // ORIGIN edit: scene 1 clip 1's line, via the real Edit Clip Script modal.
            const string originLine = "Mary had a little lamb, its fleece was white as SNOWDRIFT.";
            await EditClipDialogueAsync(page, _fx.BaseUrl, sceneNumber: 1, clipNumber: 1, originLine);
            // The edit is committed by the debounced auto-git service (~4.2s) — the sync merges
            // COMMITTED state only, so give the commit time to land.
            await page.WaitForTimeoutAsync(6_500);

            // Switch the studio to the FORK and edit a DIFFERENT clip there.
            await Ui.GotoHomePickerAsync(page);
            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Value = forkId });
            const string forkLine = "Everywhere that Mary went, the FORK was sure to go.";
            await EditClipDialogueAsync(page, _fx.BaseUrl, sceneNumber: 1, clipNumber: 2, forkLine);
            await page.WaitForTimeoutAsync(6_500); // fork's own auto-commit (see above)

            // Sync origin from the Home manage panel — a real 3-way merge, no conflicts expected
            // (different clips edited on each side).
            await Ui.GotoHomePickerAsync(page);
            await page.GetByTestId("home-manage-projects").ClickAsync();
            var sync = page.GetByTestId("home-sync-origin");
            await Assertions.Expect(sync).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await sync.ClickAsync();
            try
            {
                await Assertions.Expect(page.GetByTestId("home-message"))
                    .ToContainTextAsync("synced", new() { Timeout = 60_000, IgnoreCase = true });
            }
            catch (PlaywrightException)
            {
                var diag = await page.EvaluateAsync<string>(@"() => JSON.stringify({
                    msg: document.querySelector('[data-testid=home-message]')?.innerText || null,
                    errs: Array.from(document.querySelectorAll('.alert-danger, .alert-warning')).map(e => e.innerText.trim()),
                }).slice(0, 1200)");
                // Name the conflicting files — scan the fork's working tree for conflict markers.
                var forkDir = Path.Combine(_fx.WorkspaceRootPath, "projects",
                    forkId.Replace('/', Path.DirectorySeparatorChar));
                var conflicted = Directory.Exists(forkDir)
                    ? Directory.EnumerateFiles(forkDir, "*", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(".git") && new FileInfo(f).Length < 5_000_000)
                        .Where(f => { try { return File.ReadAllText(f).Contains("<<<<<<<"); } catch { return false; } })
                        .Select(f => Path.GetRelativePath(forkDir, f)).ToList()
                    : new List<string> { "(fork dir not found: " + forkDir + ")" };
                Assert.Fail("Sync origin did not report success. Page: " + diag +
                            " | conflict-marked files: " + string.Join(", ", conflicted));
            }

            // The fork now carries BOTH edits: origin's clip-1 line and its own clip-2 line.
            var detail = await ApiAsync(page, "GET", ProjectIdRouting.ProjectApi(forkId) + "/scenes/1");
            if (!detail.Contains("SNOWDRIFT") || !detail.Contains("the FORK was sure to go"))
            {
                var forkDir = Path.Combine(_fx.WorkspaceRootPath, "projects", forkId.Replace('/', Path.DirectorySeparatorChar));
                var originDir = Path.Combine(_fx.WorkspaceRootPath, "projects", originId.Replace('/', Path.DirectorySeparatorChar));
                string Bp(string dir) { var p = Path.Combine(dir, "blueprint.clips.grok.json"); return File.Exists(p) ? File.ReadAllText(p) : "(missing)"; }
                var forkBp = Bp(forkDir);
                var originBp = Bp(originDir);
                var rootJsons = Directory.Exists(forkDir)
                    ? string.Join(",", Directory.EnumerateFiles(forkDir, "*.json").Select(Path.GetFileName))
                    : "(no dir)";
                var cfgPath = Path.Combine(forkDir, "pipeline_config.json");
                var cfg = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "(none)";
                var dialogues = System.Text.RegularExpressions.Regex.Matches(detail, "\"dialogue\":\"(.*?)\"")
                    .Select(m => m.Groups[1].Value).ToList();
                Assert.Fail("Merged fork detail missing an edit. " +
                    $"forkBp has SNOWDRIFT={forkBp.Contains("SNOWDRIFT")}, FORK-line={forkBp.Contains("the FORK was sure to go")}; " +
                    $"originBp has SNOWDRIFT={originBp.Contains("SNOWDRIFT")}. Root jsons: {rootJsons}. Config: {cfg[..Math.Min(300, cfg.Length)]}. " +
                    $"Detail dialogues: [{string.Join(" | ", dialogues)}]");
            }
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Open scene N on the Film page, expand clip C, edit its line through the real
    /// "Edit Clip Script" modal, save, and confirm the row shows the new text.</summary>
    private static async Task EditClipDialogueAsync(IPage page, string baseUrl, int sceneNumber, int clipNumber, string newLine)
    {
        await Ui.GotoAppAsync(page, baseUrl, "/scenes");
        var sceneRow = page.Locator($"[data-testid='scene-row'][data-scene-number='{sceneNumber}']");
        await sceneRow.Locator("span.badge").First.ClickAsync();
        var expander = page.GetByTestId($"clip-expander-{clipNumber}");
        await Assertions.Expect(expander).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await expander.ClickAsync();
        var editBtn = page.GetByTestId("clip-edit-line");
        await Assertions.Expect(editBtn).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await editBtn.ClickAsync();
        var dlg = page.GetByTestId("clip-editor-dialogue");
        await Assertions.Expect(dlg).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await dlg.FillAsync(newLine);
        await page.GetByTestId("clip-editor-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("clip-editor-modal")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }

    [Fact]
    public async Task Studio_process_strip_navigates_smoothly_across_all_workflow_steps()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var project = Uniq("NavStep");
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, project, "mary_had_a_lamb.fountain");

            // Every step is addressed by its own testid and asserted to exist. A step the project
            // has not unlocked renders "is-disabled" and does not navigate; the rest must. The
            // visited count at the end is what stops a bad selector from passing silently.
            var steps = new[] { "screenplay", "cast", "locations", "film" };
            var visited = 0;
            foreach (var step in steps)
            {
                var strip = page.GetByTestId("studio-process-nav").First;
                await Assertions.Expect(strip).ToBeVisibleAsync(new() { Timeout = 30_000 });

                var link = strip.GetByTestId($"studio-step-{step}");
                await Assertions.Expect(link).ToBeVisibleAsync(new() { Timeout = 15_000 });

                var href = await link.GetAttributeAsync("href") ?? "";
                Assert.False(string.IsNullOrWhiteSpace(href), $"step {step} has no href");
                if ((await link.GetAttributeAsync("class") ?? "").Contains("is-disabled"))
                    continue;

                await link.ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(
                    new System.Text.RegularExpressions.Regex(
                        System.Text.RegularExpressions.Regex.Escape(href.TrimStart('/'))),
                    new() { Timeout = 15_000 });
                visited++;
            }

            // Every step was found and carries an href — the failure this guards against is a
            // selector that matches nothing, which used to skip the whole test silently. How many
            // are clickable depends on what the project has unlocked, so require at least one.
            Assert.True(visited >= 1, "no studio step in the strip was enabled from the Film page");
        }
        finally { await ctx.CloseAsync(); }
    }
}
