using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

using PageToMovie.Core.Utils;
namespace PageToMovie.UiTests;

/// <summary>Shared page-driving helpers for the UI suite.</summary>
public static class Ui
{
    /// <summary>Navigate to a route with the admin login-bypass, wait for the WASM shell, dismiss the terms gate.</summary>
    public static async Task GotoAppAsync(IPage page, string baseUrl, string route = "/")
    {
        var sep = route.Contains('?') ? "&" : "?";
        await page.GotoAsync($"{baseUrl}{route}{sep}admin=1");
        // Shell-ready marker: the nav link to home (nav-studio) is present on every page in both
        // collapsed and expanded sidebar states.
        await page.Locator("a[data-testid='nav-studio'], a[href='/']").First
                  .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await DismissTermsAsync(page);
    }

    /// <summary>Like <see cref="GotoAppAsync"/> but establishes the logged-in session first — some
    /// pages (e.g. Configuration) redirect to /login when hit as a cold deep link. Loads home
    /// (?admin=1) to sign in, then navigates to the target; the session persists in the context.</summary>
    public static async Task GotoAppLoggedInAsync(IPage page, string baseUrl, string route)
    {
        await GotoAppAsync(page, baseUrl, "/");
        await page.GotoAsync($"{baseUrl}{route}");
        await page.Locator("a[data-testid='nav-studio'], a[href='/']").First
                  .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
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

    // ── Home project picker helpers ─────────────────────────────────────────────

    /// <summary>Full page load of Home (<c>/?admin=1</c>) — a fresh WASM app instance hydrated
    /// from the server, unlike in-app nav which keeps client state. Waits on the always-present
    /// Home nav link rather than the /scenes link (<see cref="GotoAppAsync"/>), which is a disabled
    /// <c>span</c> until the active project has a shot plan.</summary>
    public static async Task ReloadHomeAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/?admin=1");
        await page.GetByTestId("nav-studio")
                  .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await DismissTermsAsync(page);
    }

    /// <summary>Go to Home (full studio) via the in-app nav and wait for the picker.</summary>
    public static async Task GotoHomePickerAsync(IPage page, int timeoutMs = 20_000)
    {
        await page.GetByTestId("nav-studio").ClickAsync();
        await Assertions.Expect(page.GetByTestId("home-project-picker")).ToBeVisibleAsync(new() { Timeout = timeoutMs });
    }

    /// <summary>All option labels currently in the Home project picker, in DOM order.</summary>
    public static async Task<IReadOnlyList<string>> PickerLabelsAsync(IPage page)
    {
        var labels = await page.EvalOnSelectorAsync<string[]>(
            "[data-testid='home-project-picker']",
            "el => Array.from(el.options).map(o => o.textContent.trim())");
        return labels;
    }

    /// <summary>All option values (project ids) currently in the Home project picker, in DOM order.</summary>
    public static async Task<IReadOnlyList<string>> PickerValuesAsync(IPage page)
    {
        var values = await page.EvalOnSelectorAsync<string[]>(
            "[data-testid='home-project-picker']",
            "el => Array.from(el.options).map(o => o.value)");
        return values;
    }

    /// <summary>The picker's currently selected project id (option value), or "" when none.</summary>
    public static Task<string> SelectedPickerValueAsync(IPage page) =>
        page.EvalOnSelectorAsync<string>(
            "[data-testid='home-project-picker']",
            "el => el.selectedOptions[0]?.value ?? ''");

    /// <summary>Blazor sets the DOM `.selected` property on the option, not the HTML attribute, so a
    /// CSS `option[selected]` locator won't see it — poll the select's own `selectedOptions` instead.
    /// `EvalOnSelectorAsync` throws immediately (no auto-wait) if the selector isn't in the DOM yet, so
    /// the loop tolerates that during a page navigation rather than failing on the first iteration.</summary>
    public static async Task AssertSelectedProjectLabelAsync(IPage page, string expectedLabel, int timeoutMs = 15_000)
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
                    "el => el.selectedOptions[0]?.textContent?.trim() ?? ''");
                if (last == expectedLabel) return;
            }
            catch (PlaywrightException) { /* element mid-navigation; retry */ }
            await Task.Delay(250);
        }
        Assert.Fail($"Expected project picker selected option to be '{expectedLabel}', but was '{last}'.");
    }

    /// <summary>Assert the picker option labels are exactly <paramref name="expected"/> (order-insensitive),
    /// polling briefly because the list refreshes after each server round-trip.</summary>
    public static async Task AssertPickerLabelsAsync(IPage page, IEnumerable<string> expected, int timeoutMs = 15_000)
    {
        var want = expected.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string[] got = Array.Empty<string>();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                got = (await PickerLabelsAsync(page)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (got.SequenceEqual(want)) return;
            }
            catch (PlaywrightException) { /* mid-navigation; retry */ }
            await Task.Delay(250);
        }
        Assert.Fail($"Expected picker options [{string.Join(", ", want)}] but got [{string.Join(", ", got)}].");
    }

    // ── Configuration page sections (most start collapsed; Studio coverage starts open) ───────

    /// <summary>Expand a Configuration section by its header testid
    /// (<c>config-section-coverage|storage|appearance|music|format|pipeline|advanced</c>) if it is
    /// currently closed. Studio coverage is a button + body (aria-expanded); other sections are
    /// native <c>&lt;details&gt;</c>.</summary>
    public static async Task OpenConfigSectionAsync(IPage page, string sectionTestId)
    {
        var summary = page.GetByTestId(sectionTestId);
        await Assertions.Expect(summary).ToBeVisibleAsync(new() { Timeout = 20_000 });
        if (await IsConfigSectionOpenAsync(summary))
            return;
        await summary.ClickAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsConfigSectionOpenAsync(summary)) return;
            await Task.Delay(150);
        }
        Assert.Fail($"Configuration section '{sectionTestId}' did not open after clicking its summary.");
    }

    /// <summary>Studio coverage uses aria-expanded; sibling Settings cards use details.open.</summary>
    public static Task<bool> IsConfigSectionOpenAsync(ILocator summary) =>
        summary.EvaluateAsync<bool>(@"el => {
            const expanded = el.getAttribute('aria-expanded');
            if (expanded === 'true') return true;
            if (expanded === 'false') return false;
            return el.parentElement?.open === true;
        }");

    // ── Authed API access from the browser session ──────────────────────────────

    /// <summary>Call the app's own API from inside the page using the browser's admin/dev session
    /// (Bearer token + X-User-Id from sessionStorage), so the request is authed exactly like the UI's.
    /// Returns the response body text; asserts a 2xx status.</summary>
    public static async Task<string> ApiFetchAsync(IPage page, string path, string method = "GET", string? jsonBody = null)
    {
        var result = await page.EvaluateAsync<string>(@"async ([path, method, body]) => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return JSON.stringify({__err:'no session'});
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
            if (body) h['Content-Type'] = 'application/json';
            const res = await fetch(path, {method, headers:h, body: body || undefined});
            const text = await res.text();
            return JSON.stringify({__status: res.status, __text: text});
        }", new object?[] { path, method, jsonBody });
        using var doc = JsonDocument.Parse(result);
        if (doc.RootElement.TryGetProperty("__err", out var err))
            Assert.Fail($"ApiFetch {method} {path}: {err.GetString()}");
        var status = doc.RootElement.GetProperty("__status").GetInt32();
        var text = doc.RootElement.GetProperty("__text").GetString() ?? "";
        Assert.True(status is >= 200 and < 300, $"ApiFetch {method} {path} → {status}: {text[..Math.Min(300, text.Length)]}");
        return text;
    }

    /// <summary>Download a binary API response (e.g. a project export zip) via the browser session
    /// and save it to <paramref name="savePath"/>.</summary>
    public static async Task ApiDownloadAsync(IPage page, string path, string savePath)
    {
        var b64 = await page.EvaluateAsync<string>(@"async (path) => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            if (!raw) return 'ERR:no session';
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
            const res = await fetch(path, {headers:h});
            if (!res.ok) return 'ERR:' + res.status + ' ' + (await res.text()).slice(0,200);
            const buf = new Uint8Array(await res.arrayBuffer());
            let bin = '';
            for (let i = 0; i < buf.length; i += 0x8000)
                bin += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
            return btoa(bin);
        }", path);
        Assert.False(b64.StartsWith("ERR:", StringComparison.Ordinal), $"ApiDownload {path}: {b64}");
        await File.WriteAllBytesAsync(savePath, Convert.FromBase64String(b64));
    }

    /// <summary>The server's active project id for the browser session (GET /api/projects → active.id).</summary>
    public static async Task<string?> ServerActiveProjectIdAsync(IPage page)
    {
        var text = await ApiFetchAsync(page, "/api/projects");
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.Object
            && active.TryGetProperty("id", out var id))
            return id.GetString();
        return null;
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
        .Where(e => !CommonRegex.IsMatch(e, "status of 400", RegexOptions.IgnoreCase)
                    && !e.Contains("ERR_ABORTED")
                    && !e.Contains("Failed to load resource"))
        .ToList();
}
