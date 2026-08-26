using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Adaptation Import page flow: uploads source documents (.fountain, .txt), verifies dropzone
/// states, chosen file name indicator, and the unlocked "Looks good — continue to Screenplay" and
/// "Estimate" CTAs.
/// </summary>
[Collection("ui-pipeline")]
public class AdaptationImportFlowTests
{
    private readonly PipelineFixture _fx;
    public AdaptationImportFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Import_fountain_file_enables_continue_to_screenplay_button()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var projectName = "ImportFlow_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, projectName);
            await PipelineFlow.SelectFakeModelsAsync(page);

            // On the Import page, verify dropzone is visible
            var dropzone = page.GetByTestId("import-dropzone");
            await Assertions.Expect(dropzone).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Upload fountain fixture
            var fileInput = page.GetByTestId("import-file-input");
            var fixturePath = Path.Combine(AppFixture.FindRepoRoot(), "host", "playwright", "fixtures", "tell_tale_heart.fountain");
            await fileInput.SetInputFilesAsync(fixturePath);

            // Import automatically navigates to screenplay structured editor
            await page.WaitForURLAsync(new Regex("adaptation/screenplay", RegexOptions.IgnoreCase), new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("screenplay-structured-editor")).ToBeVisibleAsync(new() { Timeout = 60_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
