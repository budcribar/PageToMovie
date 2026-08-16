using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PageToMovie.Core.Utils;

namespace PageToMovie.UiTests;

/// <summary>
/// A-5: depth coverage for the Home page's project-management actions reachable via the "Manage"
/// panel — rename (display name only, id unchanged) and checkpoints (named git-backed snapshots,
/// save + revert) — which had zero dedicated coverage before this (only incidentally touched by
/// <see cref="PipelineFlow.CreateFreshProjectAsync"/> for project creation). Both round-trip through
/// the server, which the upcoming component-extraction must preserve.
/// </summary>
[Collection("ui-pipeline")]
public class HomeFlowTests
{
    private readonly PipelineFixture _fx;
    public HomeFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Rename_project_updates_picker_label_and_persists()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var originalName = "Home_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, originalName);

            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = 20_000 });

            await page.GetByTestId("home-manage-projects").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Rename" }).ClickAsync();

            var newName = "Renamed_" + Guid.NewGuid().ToString("N")[..6];
            var renameInput = page.GetByTestId("home-rename-project-name");
            await renameInput.FillAsync(newName);
            await page.GetByTestId("home-rename-project-save").ClickAsync();

            await Ui.AssertSelectedProjectLabelAsync(page, newName, 15_000);

            // Navigate away and back — the rename round-tripped through the server, not just client state.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await page.GetByTestId("nav-studio").ClickAsync();
            await Ui.AssertSelectedProjectLabelAsync(page, newName, 20_000);
            await Assertions.Expect(page.GetByTestId("home-project-picker")).Not.ToContainTextAsync(originalName);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Delete_confirm_is_a_modal_that_names_the_selected_project()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var leftover = "Keep_" + Guid.NewGuid().ToString("N")[..6];
            var chosen = "Drop_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, leftover);

            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = 20_000 });
            await page.GetByTestId("home-new-project").ClickAsync();
            await page.GetByTestId("home-new-project-name").FillAsync(chosen);
            await page.GetByTestId("home-create-project").ClickAsync();
            await page.WaitForURLAsync(
                new Regex("adaptation/import", RegexOptions.IgnoreCase, CommonRegex.Timeout),
                new() { Timeout = 60_000 });

            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = 20_000 });

            // Active is the just-created project; switch the picker to the other one, then Delete.
            await page.GetByTestId("home-project-picker").SelectOptionAsync(new SelectOptionValue { Label = leftover });
            await Ui.AssertSelectedProjectLabelAsync(page, leftover, 15_000);

            await page.GetByTestId("home-manage-projects").ClickAsync();
            await page.GetByTestId("home-delete-project").ClickAsync();

            var modal = page.GetByTestId("home-delete-project-modal");
            // Must be painted (display/visibility/box), not merely present after a state flip.
            await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(modal).ToBeInViewportAsync();
            var box = await modal.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box!.Width > 0 && box.Height > 0,
                $"Delete confirm must paint on screen, but box was {box.Width}x{box.Height}.");
            await Assertions.Expect(page.GetByTestId("home-delete-project-label")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-delete-project-label")).ToHaveTextAsync(leftover);
            await Assertions.Expect(page.GetByTestId("home-delete-project-cancel")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-delete-project-confirm")).ToBeVisibleAsync();
            await Assertions.Expect(modal).Not.ToContainTextAsync(chosen);

            await page.GetByTestId("home-delete-project-cancel").ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync();

            await page.GetByTestId("home-delete-project").ClickAsync();
            await Assertions.Expect(modal).ToBeVisibleAsync();
            await page.GetByTestId("home-delete-project-confirm").ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync(new() { Timeout = 20_000 });
            await Assertions.Expect(page.GetByTestId("home-project-picker")).Not.ToContainTextAsync(leftover);
            await Ui.AssertSelectedProjectLabelAsync(page, chosen, 15_000);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Checkpoint_save_and_revert_round_trips_through_server()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl,
                "Checkpoint_" + Guid.NewGuid().ToString("N")[..6]);

            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = 20_000 });

            await page.GetByTestId("home-manage-projects").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-checkpoints")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(page.GetByTestId("home-checkpoint-name")).ToHaveCountAsync(0);

            await page.GetByTestId("home-checkpoints").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-checkpoints-panel")).ToBeVisibleAsync(new() { Timeout = 15_000 });

            var checkpointName = "Before test edit " + Guid.NewGuid().ToString("N")[..6];
            await page.GetByTestId("home-checkpoint-name").FillAsync(checkpointName);
            await page.GetByTestId("home-checkpoint-save").ClickAsync();

            var list = page.GetByTestId("home-checkpoint-list");
            var listItem = list.GetByText(checkpointName);
            await Assertions.Expect(listItem).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await AssertCheckpointListScrollsAsync(list);

            // Navigate away and back — the checkpoint is a real git commit on the server, not client state.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await page.GetByTestId("nav-studio").ClickAsync();
            await page.GetByTestId("home-manage-projects").ClickAsync();
            await page.GetByTestId("home-checkpoints").ClickAsync();
            listItem = page.GetByTestId("home-checkpoint-list").GetByText(checkpointName);
            await Assertions.Expect(listItem).ToBeVisibleAsync(new() { Timeout = 20_000 });

            // Roll back to it — the only way to exercise the revert endpoint end to end.
            var revertBtn = page.Locator("[data-testid^='home-checkpoint-revert-']").First;
            await revertBtn.ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = 20_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Named_checkpoints_start_collapsed_and_expand_from_the_header()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl,
                "CpFold_" + Guid.NewGuid().ToString("N")[..6]);

            await page.GetByTestId("nav-studio").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = 20_000 });

            await page.GetByTestId("home-manage-projects").ClickAsync();
            var header = page.GetByTestId("home-checkpoints");
            await Assertions.Expect(header).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(header).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(page.GetByTestId("home-checkpoints-panel")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("home-checkpoint-name")).ToHaveCountAsync(0);

            await header.ClickAsync();
            await Assertions.Expect(header).ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(page.GetByTestId("home-checkpoints-panel")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-checkpoint-name")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("home-checkpoint-save")).ToBeVisibleAsync();

            await header.ClickAsync();
            await Assertions.Expect(header).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(page.GetByTestId("home-checkpoints-panel")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("home-checkpoint-name")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    private static async Task AssertCheckpointListScrollsAsync(ILocator list)
    {
        var overflowY = await list.EvaluateAsync<string>("el => getComputedStyle(el).overflowY");
        Assert.True(overflowY is "auto" or "scroll",
            $"Expected the expanded checkpoint list to scroll, but overflow-y was '{overflowY}'.");
        var maxHeightPx = await list.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).maxHeight)");
        Assert.True(maxHeightPx > 0 && maxHeightPx < 10_000,
            $"Expected a bounded max-height on the checkpoint list, but was '{maxHeightPx}'.");
    }

}
