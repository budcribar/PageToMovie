using System.Diagnostics;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Shared fixture for the UI regression suite. Ensures a single-process fakes host (Api serves the
/// Blazor WASM UI) is running — reusing an already-running instance on its port or launching one —
/// and owns the Playwright browser. Tests reference PageToMovie.Core/Engine directly so they can
/// compute expected values with the real domain code.
///
/// Port contract (shared with host/scripts/run-ui-tests.sh): the default host honors
/// PLAYWRIGHT_BASE_URL when set, else http://localhost:5088. When this fixture launches the host it
/// binds exactly that port via PAGETOMOVIE_BIND_PORTS. Subclasses fix their own port for a second
/// instance (e.g. capabilities forced off).
/// </summary>
public class AppFixture : IAsyncLifetime
{
    protected virtual int DefaultPort => 5088;
    protected virtual bool HonorEnvBaseUrl => true;
    /// <summary>Deterministic reads by default: the short-TTL server read cache serves stale
    /// scene/clip/cast counts right after a job-driven step (the seed pipeline, generate via the job
    /// API), which made the Scenes page disagree with what a test had just done.</summary>
    protected virtual IReadOnlyDictionary<string, string> ExtraEnv => NoReadCacheEnv;
    /// <summary>Workspace (projects) root the host uses. Hermetic by default: a fresh temp workspace
    /// that <see cref="EnsureReadyProjectAsync"/> seeds with one generated project through the fakes.
    /// The suite used to run against the developer's repo workspace and silently depended on
    /// whatever projects (and per-user active pointer) happened to be there.</summary>
    protected virtual string WorkspaceRoot => _defaultWorkspace ??= CreateTempWorkspace("ptm-ui-");
    private string? _defaultWorkspace;

    protected static string CreateTempWorkspace(string prefix)
    {
        var ws = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(ws, "projects"));
        return ws;
    }
    /// <summary>Public read of <see cref="WorkspaceRoot"/> — lets tests point a real
    /// <c>ProjectStore</c> at the same workspace (including an isolated temp one, e.g.
    /// <see cref="PipelineFixture"/>) the running host is using, to read/verify on-disk project state.</summary>
    public string WorkspaceRootPath => WorkspaceRoot;
    private static readonly IReadOnlyDictionary<string, string> NoReadCacheEnv = new Dictionary<string, string>
    {
        ["PageToMovie__EnableReadCaches"] = "false",
    };

    public string BaseUrl { get; }
    private readonly int _port;
    public IBrowser Browser { get; private set; } = null!;

    private IPlaywright _pw = null!;
    private Process? _api;              // non-null only when WE launched it
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    // Serializes host launches across fixtures so two `dotnet run` don't build PageToMovie.Api at
    // once (concurrent build → file-lock failure). The second launch finds the Api already built.
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);

    public AppFixture()
    {
        var envUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_BASE_URL");
        BaseUrl = (HonorEnvBaseUrl && !string.IsNullOrWhiteSpace(envUrl))
            ? envUrl!.TrimEnd('/')
            : $"http://localhost:{DefaultPort}";
        _port = new Uri(BaseUrl).Port;
    }

    public async Task InitializeAsync()
    {
        if (!await IsHealthyAsync())
            await LaunchApiAsync();

        var exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (exit != 0) throw new InvalidOperationException($"playwright install exited {exit}");

        _pw = await Playwright.CreateAsync();
        Browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        if (SeedReadyProject)
            await EnsureReadyProjectAsync();
    }

    /// <summary>
    /// The shared "ui" collection navigates with <see cref="Ui.GotoAppAsync"/>, which waits for the
    /// /scenes nav link — enabled only when the ACTIVE project has a shot plan (and several tests
    /// need clips). That used to be an unstated precondition on whatever the local workspace held;
    /// on a clean clone every one of those tests timed out. Base fixture only: isolated fixtures
    /// build exactly what they need.
    /// </summary>
    protected virtual bool SeedReadyProject => GetType() == typeof(AppFixture);

    private async Task EnsureReadyProjectAsync()
    {
        var (ctx, page) = await NewPageAsync();
        try
        {
            await page.GotoAsync($"{BaseUrl}/?admin=1");
            await page.GetByTestId("nav-studio").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            await Ui.DismissTermsAsync(page);
            var ready = await page.EvaluateAsync<bool>(@"async () => {
                const raw = sessionStorage.getItem('PageToMovie.admin.session');
                if (!raw) return false;
                const s = JSON.parse(raw);
                const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||'')};
                const pr = await fetch('/api/projects', {headers:h}).then(r=>r.json()).catch(()=>null);
                const id = ((pr||{}).active||{}).id;
                if (!id) return false;
                const st = await fetch('/api/projects/'+encodeURIComponent(id)+'/adaptation', {headers:h}).then(r=>r.json()).catch(()=>null);
                const s2 = (((st||{}).adaptation||{}).stage2)||{};
                return !!(s2.stage2Ready && !s2.stage2Stale && (s2.stage2Clips||0) > 0);
            }");
            if (ready) return;
            await PipelineFlow.RunToGeneratedClipsAsync(page, BaseUrl, "UiSeed_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>A fresh, isolated context+page.</summary>
    public async Task<(IBrowserContext ctx, IPage page)> NewPageAsync()
    {
        var ctx = await Browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new() { Width = 1280, Height = 900 } });
        var page = await ctx.NewPageAsync();
        return (ctx, page);
    }

    /// <summary>Raw HTTP GET against this fixture's host (for endpoint-level assertions).</summary>
    public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync($"{BaseUrl}{path}");

    private async Task<bool> IsHealthyAsync()
    {
        try { return (await _http.GetAsync($"{BaseUrl}/health")).IsSuccessStatusCode; }
        catch { return false; }
    }

    private async Task LaunchApiAsync()
    {
        await LaunchGate.WaitAsync();
        try { await LaunchApiCoreAsync(); }
        finally { LaunchGate.Release(); }
    }

    private async Task LaunchApiCoreAsync()
    {
        var repo = FindRepoRoot();
        var apiProj = Path.Combine(repo, "host", "PageToMovie.Api");
        var psi = new ProcessStartInfo("dotnet",
            $"run --project \"{apiProj}\" --no-launch-profile")
        {
            WorkingDirectory = Path.Combine(repo, "host"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PageToMovie__UseFakes"] = "true";
        psi.Environment["PageToMovie_USE_FAKES"] = "true";
        psi.Environment["PageToMovie__WorkspaceRoot"] = WorkspaceRoot;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        // Bind exactly our port (UseUrls ignores ASPNETCORE_URLS; PAGETOMOVIE_BIND_PORTS is honored).
        psi.Environment["PAGETOMOVIE_BIND_PORTS"] = _port.ToString();
        foreach (var kv in ExtraEnv) psi.Environment[kv.Key] = kv.Value;

        _api = Process.Start(psi) ?? throw new InvalidOperationException("failed to start Api");
        _ = Task.Run(async () => { while (!_api.StandardOutput.EndOfStream) await _api.StandardOutput.ReadLineAsync(); });
        _ = Task.Run(async () => { while (!_api.StandardError.EndOfStream) await _api.StandardError.ReadLineAsync(); });

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync()) return;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"fakes Api did not become healthy at {BaseUrl} within 3 minutes");
    }

    /// <summary>Repo root (workspace root) — the same the running host uses, so tests can drive the
    /// real domain code (e.g. CostReportService) against the same project files.</summary>
    internal static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "host", "PageToMovie.slnx"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (host/PageToMovie.slnx)");
    }

    public virtual async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _pw?.Dispose();
        if (_api is { HasExited: false })
        {
            try { _api.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _api.Dispose();
        }
        _http.Dispose();
        if (_defaultWorkspace is not null)
        {
            try { Directory.Delete(_defaultWorkspace, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// A second fakes host (separate, fixed port; ignores PLAYWRIGHT_BASE_URL) with the gated
/// capabilities forced OFF, so the disabled "Set up →" UI is reachable (fakes otherwise reports
/// everything configured).
/// </summary>
public sealed class CapabilitiesOffFixture : AppFixture
{
    protected override int DefaultPort => 5099;
    protected override bool HonorEnvBaseUrl => false;
    protected override IReadOnlyDictionary<string, string> ExtraEnv => new Dictionary<string, string>
    {
        ["PAGETOMOVIE_FAKE_DISABLED_CAPABILITIES"] = "video,image,review,music,voice",
        ["PageToMovie__EnableReadCaches"] = "false",
    };
}

/// <summary>
/// A host on its own port with a fresh, empty temp workspace — for the end-to-end pipeline test
/// that creates a project from scratch and runs it through the fully-faked pipeline, without
/// touching the demo projects or the other hosts' active-project state.
/// </summary>
public sealed class PipelineFixture : AppFixture
{
    private readonly string _workspace;

    public PipelineFixture()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ptm-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_workspace, "projects"));
    }

    protected override int DefaultPort => 5081;
    protected override bool HonorEnvBaseUrl => false;
    protected override string WorkspaceRoot => _workspace;
    // Deterministic reads: this test drives generation via the job API (not the browser's SignalR
    // flow), so the short-TTL server read cache can serve stale scene/clip counts right after a job.
    // Disable it so the Scenes page always reflects the just-generated state.
    protected override IReadOnlyDictionary<string, string> ExtraEnv => new Dictionary<string, string>
    {
        ["PageToMovie__EnableReadCaches"] = "false",
    };

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }
}

[CollectionDefinition("ui")]
public sealed class UiCollection : ICollectionFixture<AppFixture> { }

[CollectionDefinition("ui-caps-off")]
public sealed class CapsOffCollection : ICollectionFixture<CapabilitiesOffFixture> { }

[CollectionDefinition("ui-pipeline")]
public sealed class PipelineCollection : ICollectionFixture<PipelineFixture> { }

/// <summary>
/// A pipeline host (own port, own temp workspace) with the fake vision style gate forced to REJECT
/// (PAGETOMOVIE_FAKE_STYLE_REJECT) — so the "Use this look anyway" override path is reachable, which
/// the always-passing default fakes host can't exercise.
/// </summary>
public sealed class StyleRejectFixture : AppFixture
{
    private readonly string _workspace;

    public StyleRejectFixture()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ptm-reject-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_workspace, "projects"));
    }

    protected override int DefaultPort => 5082;
    protected override bool HonorEnvBaseUrl => false;
    protected override string WorkspaceRoot => _workspace;
    protected override IReadOnlyDictionary<string, string> ExtraEnv => new Dictionary<string, string>
    {
        ["PAGETOMOVIE_FAKE_STYLE_REJECT"] = "1",
        ["PageToMovie__EnableReadCaches"] = "false",
    };

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }
}

[CollectionDefinition("ui-style-reject")]
public sealed class StyleRejectCollection : ICollectionFixture<StyleRejectFixture> { }

/// <summary>
/// A pipeline host (own port, own temp workspace) with <c>Auth:RequireLogin</c> forced off — so
/// synthetic per-test identities sent only via <c>X-User-Id</c> (no real signup/email-confirm) are
/// treated as distinct, non-admin users. Matches the same bypass the API test suite already relies on
/// (see <c>FilmStudioApiFactory</c>) rather than routing test users through the shared host's real
/// login/terms gate, which would require either real signup+email-confirmation per user or the
/// dev/admin bypass — and the latter would make every synthetic user an admin, defeating the point of
/// a multi-user ownership/lease isolation test.
/// </summary>
public sealed class MultiUserLeaseFixture : AppFixture
{
    private readonly string _workspace;

    public MultiUserLeaseFixture()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ptm-lease-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_workspace, "projects"));
    }

    protected override int DefaultPort => 5083;
    protected override bool HonorEnvBaseUrl => false;
    protected override string WorkspaceRoot => _workspace;
    protected override IReadOnlyDictionary<string, string> ExtraEnv => new Dictionary<string, string>
    {
        ["PageToMovie__Auth__RequireLogin"] = "false",
    };

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }
}

[CollectionDefinition("ui-multiuser-lease")]
public sealed class MultiUserLeaseCollection : ICollectionFixture<MultiUserLeaseFixture> { }

/// <summary>
/// A host on its own port with a fresh, empty temp workspace dedicated to the Home project-management
/// suite (create / pick / rename / delete / visibility / import). Kept separate from the pipeline
/// fixture so those tests' create/delete churn can't disturb the pipeline suite's active project —
/// and so the "last project deleted → empty state" case is reachable without emptying another
/// suite's workspace.
/// </summary>
public sealed class HomeFixture : AppFixture
{
    private readonly string _workspace;

    public HomeFixture()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ptm-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_workspace, "projects"));
    }

    protected override int DefaultPort => 5084;
    protected override bool HonorEnvBaseUrl => false;
    protected override string WorkspaceRoot => _workspace;
    protected override IReadOnlyDictionary<string, string> ExtraEnv => new Dictionary<string, string>
    {
        ["PageToMovie__EnableReadCaches"] = "false",
    };

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }
}

[CollectionDefinition("ui-home")]
public sealed class HomeCollection : ICollectionFixture<HomeFixture> { }
