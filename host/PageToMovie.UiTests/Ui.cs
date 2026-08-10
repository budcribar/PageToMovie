using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>Shared page-driving helpers for the UI suite.</summary>
public static class Ui
{
    /// <summary>
    /// Shell-ready marker that works even when Film/Cast/Review nav links are replaced by
    /// disabled spans (StudioStateMachine gates hide <c>a[href='/scenes']</c> until shot plan + cast).
    /// Prefer Home / Settings / Estimate — they render for any signed-in session.
    /// </summary>
    public static ILocator ShellReady(IPage page) =>
        page.Locator(
            "[data-testid='nav-studio'], [data-testid='nav-configuration'], [data-testid='nav-cost'], " +
            "[data-testid='nav-scenes'], [data-testid='nav-scenes-disabled']").First;

    /// <summary>Navigate to a route with the admin login-bypass, wait for the WASM shell, dismiss the terms gate.</summary>
    public static async Task GotoAppAsync(IPage page, string baseUrl, string route = "/")
    {
        var sep = route.Contains('?') ? "&" : "?";
        await page.GotoAsync($"{baseUrl}{route}{sep}admin=1");
        await ShellReady(page)
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await DismissTermsAsync(page);
    }

    /// <summary>Like <see cref="GotoAppAsync"/> but establishes the logged-in session first — some
    /// pages (e.g. Configuration) redirect to /login when hit as a cold deep link. Loads home
    /// (?admin=1) to sign in, then navigates to the target; the session persists in the context.</summary>
    public static async Task GotoAppLoggedInAsync(IPage page, string baseUrl, string route)
    {
        await GotoAppAsync(page, baseUrl, "/");
        await page.GotoAsync($"{baseUrl}{route}");
        await ShellReady(page)
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await DismissTermsAsync(page);
    }

    public static async Task DismissTermsAsync(IPage page)
    {
        var agree = page.GetByRole(AriaRole.Button, new() { Name = "Agree & continue" });
        if (await agree.IsVisibleAsync())
        {
            var check = page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Checkbox).First;
            if (await check.IsVisibleAsync()) await check.CheckAsync();
            await agree.ClickAsync();
            await agree.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        }
    }

    /// <summary>Real-admin only: open the account menu and switch to "view as regular user".
    /// State lives in the client Session singleton, so navigate via in-app links (not a full
    /// reload) afterwards to stay in user mode.</summary>
    public static async Task EnterUserModeAsync(IPage page)
    {
        await page.Locator("[data-testid='nav-user-menu']").ClickAsync();
        var toggle = page.Locator("[data-testid='nav-view-as-user']");
        await toggle.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await toggle.ClickAsync();
        // Confirm the switch took effect via its observable result: admin-only nav is gone.
        await Assertions.Expect(page.Locator("a[href='/admin']")).ToHaveCountAsync(0);
        // Close the dropdown (if still open) so its backdrop doesn't intercept later clicks.
        var backdrop = page.GetByRole(AriaRole.Button, new() { Name = "Close account menu" });
        if (await backdrop.IsVisibleAsync()) await backdrop.ClickAsync();
    }

    public static ConsoleErrors CollectConsoleErrors(IPage page) => new(page);

    /// <summary>The active project id from the workspace pointer (local file, no auth) — lets tests
    /// drive the same project through the real Engine that the host is showing in the browser.</summary>
    public static string? ActiveProjectId(string repo)
    {
        var wsPath = Path.Combine(repo, "projects", "workspace.json");
        if (!File.Exists(wsPath)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(wsPath));
        foreach (var name in new[] { "ActiveProject", "activeProject" })
            if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        return null;
    }

    /// <summary>True when the left-nav item is the enabled link (not the disabled span).</summary>
    public static async Task ExpectNavOpenAsync(IPage page, string testId)
    {
        var open = page.GetByTestId(testId);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await open.CountAsync() > 0 && await open.IsVisibleAsync())
                return;
            await page.WaitForTimeoutAsync(250);
        }
        Assert.Fail($"Expected nav {testId} enabled (link), but only disabled or missing.");
    }

    /// <summary>True when the left-nav item is the disabled span with a blocked-reason title.</summary>
    public static async Task ExpectNavGatedAsync(IPage page, string testId, string reasonContains)
    {
        var gated = page.GetByTestId(testId + "-disabled");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string title = "";
        while (DateTime.UtcNow < deadline)
        {
            if (await gated.CountAsync() > 0 && await gated.IsVisibleAsync())
            {
                title = await gated.GetAttributeAsync("title") ?? "";
                if (title.Contains(reasonContains, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            await page.WaitForTimeoutAsync(250);
        }
        Assert.Fail($"Expected nav {testId} gated with title containing '{reasonContains}'. title='{title}'");
    }
}

/// <summary>Collects console errors, filtering the known pre-existing baseline noise.</summary>
public sealed class ConsoleErrors
{
    private readonly List<string> _errors = new();

    public ConsoleErrors(IPage page)
    {
        page.Console += (_, msg) => { if (msg.Type == "error") _errors.Add(msg.Text); };
        page.PageError += (_, err) => _errors.Add(err);
    }

    public IReadOnlyList<string> All => _errors;

    /// <summary>Errors excluding the documented baseline (the /cost 400 and its aborted fetch).</summary>
    public IReadOnlyList<string> Unexpected => _errors
        .Where(e => !Regex.IsMatch(e, "status of 400", RegexOptions.IgnoreCase)
                    && !e.Contains("ERR_ABORTED")
                    && !e.Contains("Failed to load resource"))
        .ToList();
}
