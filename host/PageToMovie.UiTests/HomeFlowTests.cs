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

            await AssertSelectedProjectLabelAsync(page, newName, 15_000);

            // Navigate away and back — the rename round-tripped through the server, not just client state.
            await page.GetByTestId("nav-configuration").ClickAsync();
            await page.GetByTestId("nav-studio").ClickAsync();
            await AssertSelectedProjectLabelAsync(page, newName, 20_000);
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
            await AssertSelectedProjectLabelAsync(page, leftover, 15_000);

            await page.GetByTestId("home-manage-projects").ClickAsync();
            await page.GetByTestId("home-delete-project").ClickAsync();

            var modal = page.GetByTestId("home-delete-project-modal");
            await Assertions.Expect(modal).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Assertions.Expect(page.GetByTestId("home-delete-project-label")).ToHaveTextAsync(leftover);
            await Assertions.Expect(modal).Not.ToContainTextAsync(chosen);

            await page.GetByTestId("home-delete-project-cancel").ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync();

            await page.GetByTestId("home-delete-project").ClickAsync();
            await Assertions.Expect(modal).ToBeVisibleAsync();
            await page.GetByTestId("home-delete-project-confirm").ClickAsync();
            await Assertions.Expect(modal).ToBeHiddenAsync(new() { Timeout = 20_000 });
            await Assertions.Expect(page.GetByTestId("home-project-picker")).Not.ToContainTextAsync(leftover);
            await AssertSelectedProjectLabelAsync(page, chosen, 15_000);
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
            await page.GetByTestId("home-checkpoints").ClickAsync();
            await Assertions.Expect(page.GetByTestId("home-checkpoints-panel")).ToBeVisibleAsync(new() { Timeout = 15_000 });

            var checkpointName = "Before test edit " + Guid.NewGuid().ToString("N")[..6];
            await page.GetByTestId("home-checkpoint-name").FillAsync(checkpointName);
            await page.GetByTestId("home-checkpoint-save").ClickAsync();

            var listItem = page.GetByTestId("home-checkpoint-list").GetByText(checkpointName);
            await Assertions.Expect(listItem).ToBeVisibleAsync(new() { Timeout = 15_000 });

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

    /// <summary>Blazor sets the DOM `.selected` property on the option, not the HTML attribute, so a
    /// CSS `option[selected]` locator won't see it — poll the select's own `selectedOptions` instead.
    /// `EvalOnSelectorAsync` throws immediately (no auto-wait) if the selector isn't in the DOM yet, so
    /// the loop tolerates that during a page navigation rather than failing on the first iteration.</summary>
    private static async Task AssertSelectedProjectLabelAsync(IPage page, string expectedLabel, int timeoutMs)
    {
        await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = timeoutMs });

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = await page.EvalOnSelectorAsync<string>(
                    "[data-testid='home-project-picker']",
                    "el => el.selectedOptions[0]?.textContent ?? ''");
                if (last == expectedLabel) return;
            }
            catch (PlaywrightException) { /* element mid-navigation; retry */ }
            await Task.Delay(250);
        }
        Assert.Fail($"Expected project picker selected option to be '{expectedLabel}', but was '{last}'.");
    }
}
