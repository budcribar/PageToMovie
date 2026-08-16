using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PageToMovie.Core.Utils;

namespace PageToMovie.UiTests;

/// <summary>
/// End-to-end coverage of the Home page's project management against the fakes host, on its own
/// isolated workspace (<see cref="HomeFixture"/>). Every mutation asserts BOTH the picker's option
/// list AND its selected option, then re-checks after navigating away and back (server persistence).
///
/// Why this exists: commit 38eee596 fixed a delete that left the deleted project selected in the
/// picker (client selection outlived the server delete; the StudioCard child only received IsFixed
/// cascading values so it never re-rendered when Home did). Only one test caught it, and it was
/// failing unnoticed. These tests pin down every project mutation the Home page offers so that
/// class of cross-component state drift shows up immediately.
/// </summary>
[Collection("ui-home")]
public class HomeProjectManagementTests
{
    private readonly HomeFixture _fx;
    public HomeProjectManagementTests(HomeFixture fx) => _fx = fx;

    private static string Uniq(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..6];

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_via_new_modal_selects_the_new_project_and_lists_it()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var a = Uniq("CreateA");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, a);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, a);
            Assert.Contains(a, await Ui.PickerLabelsAsync(page));

            // "+ New" opens a centered modal; Cancel closes it without creating anything.
            await page.GetByTestId("home-new-project").ClickAsync();
            var modal = page.GetByTestId("home-new-project-modal");
            await Assertions.Expect(modal).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-create-project")).ToBeDisabledAsync(); // empty name
            await page.GetByTestId("home-new-project-cancel").ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync();
            await Ui.AssertSelectedProjectLabelAsync(page, a);

            // Create a second project by pressing Enter in the name box (keyboard path).
            var b = Uniq("CreateB");
            await page.GetByTestId("home-new-project").ClickAsync();
            await Assertions.Expect(modal).ToBeVisibleAsync();
            await page.GetByTestId("home-new-project-name").FillAsync(b);
            await Assertions.Expect(page.GetByTestId("home-create-project")).ToBeEnabledAsync();
            await page.GetByTestId("home-new-project-name").PressAsync("Enter");
            await page.WaitForURLAsync(new Regex("adaptation/import", RegexOptions.IgnoreCase, CommonRegex.Timeout), new() { Timeout = 60_000 });

            // Back on Home: the new project is selected AND listed; the older one is still listed.
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, b);
            var labels = await Ui.PickerLabelsAsync(page);
            Assert.Contains(a, labels);
            Assert.Contains(b, labels);
            // Server agrees on the active project (the picker isn't just client state).
            var selectedId = await Ui.SelectedPickerValueAsync(page);
            Assert.Equal(selectedId, await Ui.ServerActiveProjectIdAsync(page));

            // Full reload (fresh Home instance, hydrated from the server) shows the same selection.
            await Ui.ReloadHomeAsync(page, _fx.BaseUrl);
            await Ui.AssertSelectedProjectLabelAsync(page, b, 20_000);
        }
        finally { await ctx.CloseAsync(); }
    }

    // ── Pick ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Picker_switch_updates_selection_and_badge_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var a = Uniq("PickA");
            var b = Uniq("PickB");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, a);
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, b);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, b);

            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Label = a });
            await Ui.AssertSelectedProjectLabelAsync(page, a);
            var aId = await Ui.SelectedPickerValueAsync(page);
            Assert.Equal(aId, await Ui.ServerActiveProjectIdAsync(page));
            // New projects are private; the badge next to the picker reflects the selected project.
            await Assertions.Expect(page.GetByTestId("home-visibility-badge")).ToHaveAttributeAsync("data-visibility", "Private");
            // Selection must be visible from the process strip too (a sibling of the picker on the card).
            await Assertions.Expect(page.GetByTestId("studio-process-nav")).ToBeVisibleAsync();

            // In-app navigation away and back keeps the client selection.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, a);

            // Full reload hydrates from the server-side per-user pointer.
            await Ui.ReloadHomeAsync(page, _fx.BaseUrl);
            await Ui.AssertSelectedProjectLabelAsync(page, a, 20_000);
            Assert.Equal(aId, await Ui.SelectedPickerValueAsync(page));

            // Switching back works too (both directions round-trip).
            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Label = b });
            await Ui.AssertSelectedProjectLabelAsync(page, b);
            Assert.Equal(await Ui.SelectedPickerValueAsync(page), await Ui.ServerActiveProjectIdAsync(page));
        }
        finally { await ctx.CloseAsync(); }
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    /// <summary>The exact scenario from 38eee596: the delete confirm lives on Home, the picker lives
    /// in the StudioCard child (IsFixed cascades). After confirming, the child must drop the deleted
    /// project from its list AND move its selection — and Home's message banner must show.</summary>
    [Fact]
    public async Task Delete_selected_project_drops_it_from_picker_and_adopts_another()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var keep = Uniq("DelKeep");
            var drop = Uniq("DelDrop");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, keep);
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, drop);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, drop);
            var before = await Ui.PickerLabelsAsync(page);
            Assert.Contains(drop, before);
            Assert.Contains(keep, before);

            await page.GetByTestId("home-manage-projects").ClickAsync();
            await page.GetByTestId("home-delete-project").ClickAsync();
            var modal = page.GetByTestId("home-delete-project-modal");
            await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(page.GetByTestId("home-delete-project-label")).ToHaveTextAsync(drop);
            await page.GetByTestId("home-delete-project-confirm").ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync(new() { Timeout = 20_000 });

            // Home banner (rendered by Home) and picker (rendered by the StudioCard child) both updated.
            await Assertions.Expect(page.GetByTestId("home-message")).ToContainTextAsync(drop, new() { Timeout = 10_000 });
            await Ui.AssertPickerLabelsAsync(page, before.Where(l => l != drop));
            var selectedLabel = await page.EvalOnSelectorAsync<string>("[data-testid='home-project-picker']", "el => el.selectedOptions[0]?.textContent?.trim() ?? ''");
            Assert.NotEqual(drop, selectedLabel);
            Assert.NotEqual("", selectedLabel);
            var selectedId = await Ui.SelectedPickerValueAsync(page);
            Assert.Equal(selectedId, await Ui.ServerActiveProjectIdAsync(page));

            // Persisted: navigate away/back and reload — the deleted project never comes back.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await Ui.GotoHomePickerAsync(page);
            Assert.DoesNotContain(drop, await Ui.PickerLabelsAsync(page));
            await Ui.AssertSelectedProjectLabelAsync(page, selectedLabel);
            await Ui.ReloadHomeAsync(page, _fx.BaseUrl);
            await Ui.AssertSelectedProjectLabelAsync(page, selectedLabel, 20_000);
            Assert.DoesNotContain(drop, await Ui.PickerLabelsAsync(page));
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Delete a project that was picked (not the most recently created / server-active
    /// one at creation time), then delete the last remaining project → the card falls back to the
    /// empty "Choose a film" state with no picker, and stays there after navigation.</summary>
    [Fact]
    public async Task Delete_picked_then_last_project_reaches_empty_state()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var a = Uniq("LastA");
            var b = Uniq("LastB");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, a);
            await DeleteAllProjectsExceptAsync(page, a);   // isolate: only our projects remain
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, b);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, b);
            await Ui.AssertPickerLabelsAsync(page, new[] { a, b });

            // Pick A (so the client selection ≠ the project created last), then delete it.
            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Label = a });
            await Ui.AssertSelectedProjectLabelAsync(page, a);
            await DeleteSelectedViaManageAsync(page, a);
            await Ui.AssertPickerLabelsAsync(page, new[] { b });
            await Ui.AssertSelectedProjectLabelAsync(page, b);
            Assert.Equal(await Ui.SelectedPickerValueAsync(page), await Ui.ServerActiveProjectIdAsync(page));

            // Delete the last one → empty state.
            await DeleteSelectedViaManageAsync(page, b);
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
            await Assertions.Expect(page.GetByTestId("home-empty-state")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-new-project")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-message")).ToContainTextAsync(b);
            Assert.Null(await Ui.ServerActiveProjectIdAsync(page));

            // Navigate away and back: still empty (no ghost selection re-hydrated from anywhere).
            await page.GetByTestId("nav-configuration").ClickAsync();
            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-new-project").First).ToBeVisibleAsync(new() { Timeout = 20_000 });
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    // ── Rename ───────────────────────────────────────────────────────────────

    /// <summary>A rename that changes the slug MOVES the project to a new id (export → re-import →
    /// delete old). The picker must follow: new label selected, new id selected, old id gone — and
    /// the server's per-user active pointer must point at the new id so a reload agrees.</summary>
    [Fact]
    public async Task Rename_with_new_slug_moves_selection_to_the_new_id_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            // Two projects, so a wrong "fall back to first option" is observable.
            var other = Uniq("AaaOther"); // sorts first alphabetically on purpose
            var orig = Uniq("RenOrig");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, other);
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, orig);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, orig);
            var origId = await Ui.SelectedPickerValueAsync(page);

            await page.GetByTestId("home-manage-projects").ClickAsync();
            await page.GetByTestId("home-rename-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-rename-panel")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-rename-project-name")).ToHaveValueAsync(orig);

            var renamed = Uniq("RenNew");
            await page.GetByTestId("home-rename-project-name").FillAsync(renamed);
            await page.GetByTestId("home-rename-project-save").ClickAsync();

            await Ui.AssertSelectedProjectLabelAsync(page, renamed, 20_000);
            await Assertions.Expect(page.GetByTestId("home-rename-panel")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("home-message")).ToContainTextAsync(renamed, new() { Timeout = 10_000 });
            var newId = await Ui.SelectedPickerValueAsync(page);
            Assert.NotEqual(origId, newId);
            Assert.DoesNotContain(origId, await Ui.PickerValuesAsync(page));
            var labels = await Ui.PickerLabelsAsync(page);
            Assert.Contains(renamed, labels);
            Assert.DoesNotContain(orig, labels);
            Assert.Contains(other, labels);
            Assert.Equal(newId, await Ui.ServerActiveProjectIdAsync(page));

            // In-app round trip and a full reload both land on the renamed project.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, renamed);
            await Ui.ReloadHomeAsync(page, _fx.BaseUrl);
            await Ui.AssertSelectedProjectLabelAsync(page, renamed, 20_000);
            Assert.Equal(newId, await Ui.SelectedPickerValueAsync(page));
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Rename_to_an_existing_project_name_shows_error_and_keeps_selection()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var taken = Uniq("Taken");
            var mine = Uniq("Mine");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, taken);
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, mine);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, mine);
            var mineId = await Ui.SelectedPickerValueAsync(page);

            await page.GetByTestId("home-manage-projects").ClickAsync();
            await page.GetByTestId("home-rename-project").ClickAsync();
            await page.GetByTestId("home-rename-project-name").FillAsync(taken);
            await page.GetByTestId("home-rename-project-save").ClickAsync();

            // Server refuses (id collision) → error banner on Home; nothing changed in the picker.
            await Assertions.Expect(page.GetByTestId("home-error")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Ui.AssertSelectedProjectLabelAsync(page, mine);
            Assert.Equal(mineId, await Ui.SelectedPickerValueAsync(page));
            var labels = await Ui.PickerLabelsAsync(page);
            Assert.Contains(taken, labels);
            Assert.Contains(mine, labels);

            // Cancel closes the rename panel; the error stays until the next action.
            await page.GetByTestId("home-rename-project-cancel").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-rename-panel")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    // ── Visibility ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Visibility_change_updates_badge_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var name = Uniq("Vis");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, name);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, name);
            var badge = page.GetByTestId("home-visibility-badge");
            await Assertions.Expect(badge).ToHaveAttributeAsync("data-visibility", "Private");

            await page.GetByTestId("home-manage-projects").ClickAsync();
            var select = page.GetByTestId("home-visibility-select");
            await Assertions.Expect(select).ToBeVisibleAsync();
            await select.SelectOptionAsync("Public");
            await Assertions.Expect(badge).ToHaveAttributeAsync("data-visibility", "Public", new() { Timeout = 15_000 });
            await Assertions.Expect(badge).ToContainTextAsync("Public");
            await Assertions.Expect(select).ToHaveValueAsync("Public");
            await Ui.AssertSelectedProjectLabelAsync(page, name); // selection untouched by the change

            // Persisted server-side: reload, reopen Manage, both badge and select show Public.
            await Ui.ReloadHomeAsync(page, _fx.BaseUrl);
            await Ui.AssertSelectedProjectLabelAsync(page, name, 20_000);
            await Assertions.Expect(page.GetByTestId("home-visibility-badge")).ToHaveAttributeAsync("data-visibility", "Public");
            await page.GetByTestId("home-manage-projects").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-visibility-select")).ToHaveValueAsync("Public");

            // And back to Private.
            await page.GetByTestId("home-visibility-select").SelectOptionAsync("Private");
            await Assertions.Expect(page.GetByTestId("home-visibility-badge")).ToHaveAttributeAsync("data-visibility", "Private", new() { Timeout = 15_000 });
            await Assertions.Expect(page.GetByTestId("home-visibility-badge")).ToContainTextAsync("Private");
        }
        finally { await ctx.CloseAsync(); }
    }

    // ── Panels: Manage / Rename / Import / New ───────────────────────────────

    [Fact]
    public async Task Manage_import_and_rename_panels_toggle_and_close_each_other()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var name = Uniq("Panels");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, name);
            await Ui.GotoHomePickerAsync(page);

            var manageBtn = page.GetByTestId("home-manage-projects");
            var managePanel = page.GetByTestId("home-manage-panel");
            await Assertions.Expect(manageBtn).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(managePanel).ToHaveCountAsync(0);

            // Expand: panel + its sections appear (project actions, package backup, checkpoints header).
            await manageBtn.ClickAsync();
            await Assertions.Expect(manageBtn).ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(managePanel).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-delete-project")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-rename-project")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-visibility-select")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-backup-project")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-package-history")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-checkpoints")).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Collapse.
            await manageBtn.ClickAsync();
            await Assertions.Expect(manageBtn).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(managePanel).ToHaveCountAsync(0);

            // Import panel opens from the toolbar; expanding Manage closes it (one panel at a time).
            await page.GetByTestId("home-import-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-import-file")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-import-confirm")).ToHaveCountAsync(0); // no file yet
            await manageBtn.ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToHaveCountAsync(0);
            await Assertions.Expect(managePanel).ToBeVisibleAsync();

            // Rename opens its panel; Cancel closes it; re-opening Manage while renaming closes it too.
            await page.GetByTestId("home-rename-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-rename-panel")).ToBeVisibleAsync();
            await page.GetByTestId("home-rename-project-cancel").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-rename-panel")).ToHaveCountAsync(0);
            await page.GetByTestId("home-rename-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-rename-panel")).ToBeVisibleAsync();
            await manageBtn.ClickAsync(); // collapse
            await manageBtn.ClickAsync(); // expand again → rename dismissed
            await Assertions.Expect(page.GetByTestId("home-rename-panel")).ToHaveCountAsync(0);

            // Import toggle: Cancel inside the panel closes it.
            await page.GetByTestId("home-import-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToBeVisibleAsync();
            await page.GetByTestId("home-import-cancel").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToHaveCountAsync(0);

            // "+ New" modal: the header close (X) button dismisses it.
            await page.GetByTestId("home-new-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-new-project-modal")).ToBeVisibleAsync();
            await page.GetByTestId("home-new-project-modal").GetByRole(AriaRole.Button, new() { Name = "Cancel" }).First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-new-project-modal")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    // ── Cross-component: child handler → Home-owned banner ───────────────────

    /// <summary>The message/error banners are rendered by Home; the picker (whose change handler
    /// clears the message) is rendered by the StudioCard child. State changed by a child handler
    /// must reach the parent's markup.</summary>
    [Fact]
    public async Task Picking_another_project_clears_the_home_message_banner()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var a = Uniq("BannerA");
            var b = Uniq("BannerB");
            var c = Uniq("BannerC");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, a);
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, b);
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, c);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, c);

            // Delete C (Home-owned modal) → banner "Deleted C" appears.
            await DeleteSelectedViaManageAsync(page, c);
            await Assertions.Expect(page.GetByTestId("home-message")).ToContainTextAsync(c, new() { Timeout = 10_000 });
            var afterDelete = await page.EvalOnSelectorAsync<string>("[data-testid='home-project-picker']", "el => el.selectedOptions[0]?.textContent?.trim() ?? ''");
            var target = afterDelete == a ? b : a;

            // Pick another project (StudioCard-owned handler clears the message) → banner gone.
            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Label = target });
            await Ui.AssertSelectedProjectLabelAsync(page, target);
            await Assertions.Expect(page.GetByTestId("home-message")).ToHaveCountAsync(0, new() { Timeout = 10_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>The checkpoint count in the Manage header is rendered by the StudioCard; the Save
    /// button that changes it lives in the CheckpointsPanel grandchild. Saving must bump the header.</summary>
    [Fact]
    public async Task Saving_a_checkpoint_bumps_the_count_in_the_manage_header()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, Uniq("CpCount"));
            await Ui.GotoHomePickerAsync(page);
            await page.GetByTestId("home-manage-projects").ClickAsync();
            var count = page.GetByTestId("home-checkpoint-count");
            await Assertions.Expect(count).ToBeVisibleAsync(new() { Timeout = 15_000 });
            var before = int.Parse((await count.TextContentAsync() ?? "0").Trim());

            await page.GetByTestId("home-checkpoints").ClickAsync();
            await page.GetByTestId("home-checkpoint-name").FillAsync("Count " + Guid.NewGuid().ToString("N")[..4]);
            await page.GetByTestId("home-checkpoint-save").ClickAsync();
            await Assertions.Expect(count).ToHaveTextAsync((before + 1).ToString(), new() { Timeout = 20_000 });
            await Assertions.Expect(page.GetByTestId("home-checkpoint-list").Locator("li")).ToHaveCountAsync(before + 1);
        }
        finally { await ctx.CloseAsync(); }
    }

    // ── Import zip ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_project_zip_adds_it_to_the_picker_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        var zipPath = Path.Combine(Path.GetTempPath(), "ptm-import-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            var src = Uniq("ExportSrc");
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, src);
            await Ui.GotoHomePickerAsync(page);
            await Ui.AssertSelectedProjectLabelAsync(page, src);
            var srcId = await Ui.SelectedPickerValueAsync(page);

            // Export the project through the app's own API (browser session), then import it back
            // through the Home import panel under a new name.
            await Ui.ApiDownloadAsync(page, ProjectIdRouting.ProjectApi(srcId) + "/export", zipPath);
            Assert.True(new FileInfo(zipPath).Length > 0, "export zip is empty");

            var before = await Ui.PickerLabelsAsync(page);
            await page.GetByTestId("home-import-project").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToBeVisibleAsync();
            await page.GetByTestId("home-import-file").SetInputFilesAsync(zipPath);
            var nameBox = page.GetByTestId("home-import-name");
            await Assertions.Expect(nameBox).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(nameBox).Not.ToHaveValueAsync(""); // default name pre-filled from the file
            var imported = Uniq("Imported");
            await nameBox.FillAsync(imported);
            await page.GetByTestId("home-import-confirm").ClickAsync();

            await Assertions.Expect(page.GetByTestId("home-import-panel")).ToHaveCountAsync(0, new() { Timeout = 60_000 });
            await Assertions.Expect(page.GetByTestId("home-message")).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Ui.AssertPickerLabelsAsync(page, before.Append(imported), 20_000);
            // Import doesn't hijack the current selection.
            await Ui.AssertSelectedProjectLabelAsync(page, src);

            // Persisted: the imported project is still listed after a reload, and can be picked.
            await Ui.ReloadHomeAsync(page, _fx.BaseUrl);
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = 20_000 });
            Assert.Contains(imported, await Ui.PickerLabelsAsync(page));
            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Label = imported });
            await Ui.AssertSelectedProjectLabelAsync(page, imported);
            Assert.Equal(await Ui.SelectedPickerValueAsync(page), await Ui.ServerActiveProjectIdAsync(page));
        }
        finally
        {
            await ctx.CloseAsync();
            try { File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Open Manage (if collapsed) and delete the currently selected project via the modal,
    /// asserting the modal names <paramref name="expectedLabel"/>.</summary>
    private static async Task DeleteSelectedViaManageAsync(IPage page, string expectedLabel)
    {
        var manageBtn = page.GetByTestId("home-manage-projects");
        if (await manageBtn.GetAttributeAsync("aria-expanded") != "true")
            await manageBtn.ClickAsync();
        await page.GetByTestId("home-delete-project").ClickAsync();
        var modal = page.GetByTestId("home-delete-project-modal");
        await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Assertions.Expect(page.GetByTestId("home-delete-project-label")).ToHaveTextAsync(expectedLabel);
        await page.GetByTestId("home-delete-project-confirm").ClickAsync();
        await Assertions.Expect(modal).ToBeHiddenAsync(new() { Timeout = 20_000 });
    }

    /// <summary>Delete every project in the (isolated) workspace except <paramref name="keepLabel"/>,
    /// via the API — leftovers from earlier tests in this class would otherwise stop the
    /// "last project deleted → empty state" case from being reachable.</summary>
    private static async Task DeleteAllProjectsExceptAsync(IPage page, string keepLabel)
    {
        var text = await Ui.ApiFetchAsync(page, "/api/projects");
        using var doc = JsonDocument.Parse(text);
        foreach (var p in doc.RootElement.GetProperty("projects").EnumerateArray())
        {
            var id = p.GetProperty("id").GetString() ?? "";
            var label = (p.TryGetProperty("label", out var l) ? l.GetString() : null)
                        ?? (p.TryGetProperty("title", out var t) ? t.GetString() : null) ?? id;
            if (label == keepLabel || id.Length == 0) continue;
            await Ui.ApiFetchAsync(page, ProjectIdRouting.ProjectApi(id), "DELETE");
        }
    }
}
