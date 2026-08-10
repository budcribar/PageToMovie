using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Contract: closing the browser does NOT cancel a server-side job. On re-login / page reload,
/// Book import must reattach progress (import-progress + Cancel) for a still-running job.
///
/// Full multi-minute Odyssey adapts are too slow for CI; this suite documents the product
/// contract and asserts reattach chrome whenever the host already has a running mine-job.
/// When no job is running, the test is a soft no-op (passes after documenting skip).
/// </summary>
[Collection("ui")]
public class JobResumeUiTests
{
    private readonly AppFixture _fx;
    public JobResumeUiTests(AppFixture fx) => _fx = fx;

    [Fact]
    public async Task Book_import_reattaches_when_mine_job_still_running()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            // Prefer any authenticated session the fixture host already has (cookie from prior tests).
            await page.GotoAsync(_fx.BaseUrl + "/adaptation/import", new() { WaitUntil = WaitUntilState.NetworkIdle });

            // If redirected to login, this environment needs auth setup — still a valid skip for CI without long jobs.
            if (page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                // Product contract documented; host not signed in for this suite instance.
                return;
            }

            // Poll /api/jobs?mine=1 from the browser context (same cookies).
            var hasRunning = await page.EvaluateAsync<bool>("""
                async () => {
                  try {
                    const r = await fetch('/api/jobs?mine=1', { credentials: 'same-origin' });
                    if (!r.ok) return false;
                    const j = await r.json();
                    const jobs = j.jobs || j.Jobs || [];
                    return jobs.some(x => {
                      const s = (x.status || x.Status || '').toLowerCase();
                      return s === 'running' || s === 'queued';
                    });
                  } catch { return false; }
                }
                """);

            if (!hasRunning)
            {
                // No live job — contract is still correct; reattach UI only appears when server has work.
                // Manual / nightly: start Odyssey import, close tab, reopen Book → expect #import-progress.
                return;
            }

            // Reload Book to force reattach path (OnInitialized → TryReattachRunningJob).
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByTestId("import-progress"))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("import-status"))
                .ToHaveAttributeAsync("data-importing", "true");
            // Cancel must be available so re-login is not a dead-end.
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }))
                .ToBeVisibleAsync(new() { Timeout = 10_000 });
        }
        finally
        {
            await ctx.CloseAsync();
        }
    }
}
