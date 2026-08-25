using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Home : IAsyncDisposable, IPageSliceHost
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private HomeJobs? _jobs;
    internal HomeJobs Jobs => _jobs ??= new HomeJobs(this);
    private HomeImport? _import;
    internal HomeImport Import => _import ??= new HomeImport(this);
    private HomeCheckpoints? _checkpointsDomain;
    internal HomeCheckpoints Checkpoints => _checkpointsDomain ??= new HomeCheckpoints(this);
    private HomeCosts? _costs;
    internal HomeCosts Costs => _costs ??= new HomeCosts(this);
    private HomeProjects? _projectsDomain;
    internal HomeProjects Projects => _projectsDomain ??= new HomeProjects(this);

    internal void EnsureDomains()
    {
        _ = Projects; _ = Import; _ = Checkpoints; _ = Jobs; _ = Costs;
    }


    /// <summary>Highlight next step on the plan spine: Book → Estimate → Film.</summary>
    internal string HomeActiveStep
    {
        get
        {
            if (ActiveProject.Status is { XaiConfigured: false })
                return "setup";
            if (!ActiveProject.HasProject) return "book";
            // Need screenplay before estimate (CanEstimate)
            if (!ActiveProject.CanEstimate) return "book";
            // Shot plan / gen not started — Decision card is next
            if (!ActiveProject.CanScenes) return "estimate";
            return "film";
        }
    }


    /// <summary>
    /// True when <c>GET /api/projects/forkable</c> has at least one timing-complete title.
    /// False until that list loads so Easy Start cards do not flash then vanish.
    /// </summary>
    internal bool _easyStartAvailable;

    internal bool _busy;

    internal string? _error;

    internal string? _message;

    private Action<System.Globalization.CultureInfo>? _onCultureChanged;


    internal static string ShortHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "";
        var h = hash.Trim();
        return h.Length <= 7 ? h : h[..7];
    }


    internal static string FormatRelativeUtc(DateTime utc)
    {
        var t = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : utc.ToUniversalTime();
        var span = DateTime.UtcNow - t;
        if (span.TotalSeconds < 45) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 36) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 14) return $"{(int)span.TotalDays}d ago";
        return t.ToString("MMM d");
    }


    /// <summary>Slice host (see <see cref="IPageSliceHost"/>): the card children are slices.</summary>
    public event Action? Rendered;

    public void RenderRequestedBySlice() => StateHasChanged();

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        Rendered?.Invoke();
    }


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        _onCultureChanged = _ => InvokeAsync(StateHasChanged);
        L.CultureChanged += _onCultureChanged;
        Hub.JobUpdated += Jobs.OnJobUpdated;
        Hub.JobLog += Jobs.OnJobLog;
        Health.Recovered += OnServerRecoveredAsync;
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
        await Projects.LoadAsync();
        await Task.WhenAll(Costs.LoadDemoShowcaseAsync(), LoadEasyStartAvailabilityAsync());
        try
        {
            await Hub.StartAsync();
        }
        catch
        {
            // SignalR optional for browse
        }
    }


    /// <summary>
    /// Server back after an outage: reload everything this page fetched at init (projects, active
    /// project, jobs, easy-start list) so nothing stays at the "No project selected" it fell to
    /// while calls were failing, and make sure the hub is live again.
    /// </summary>
    private async Task OnServerRecoveredAsync()
    {
        await InvokeAsync(async () =>
        {
            await Projects.LoadAsync();
            await Task.WhenAll(Costs.LoadDemoShowcaseAsync(), LoadEasyStartAvailabilityAsync());
            await Hub.EnsureStartedAsync();
            StateHasChanged();
        });
    }


    internal async Task LoadEasyStartAvailabilityAsync()
    {
        try
        {
            _easyStartAvailable = await Engine.HasEasyStartStoriesAsync();
        }
        catch
        {
            _easyStartAvailable = false;
        }
    }


    private static string? FirstNonEmpty(params string?[] parts)
    {
        return parts.Select(p => p?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }


    private static string TrimOneLine(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        if (s.Length > max) return s[..(max - 1)] + "…";
        return s;
    }


    public async ValueTask DisposeAsync()
    {
        if (_onCultureChanged is not null)
            L.CultureChanged -= _onCultureChanged;
        Hub.JobUpdated -= Jobs.OnJobUpdated;
        Hub.JobLog -= Jobs.OnJobLog;
        Health.Recovered -= OnServerRecoveredAsync;
        // Do NOT Hub.DisposeAsync() here — see the same note on Admin.razor.cs. JobHubClient is an
        // app-wide singleton owned by DI, and disposing it latches _disposed, after which every
        // StartAsync/EnsureStartedAsync returns immediately and the connection never comes back.
        // ClientMediaFolderService.EnsureHubHookAsync latches _hubHooked on its first call and
        // never retries, so from then on no job's generated media reaches the local folder — the
        // API host drops its own copy once ClientMediaUrl is published, so those clips are simply
        // lost. Home is the landing page, so navigating away from it killed live updates for the
        // rest of the session on every visit (Mary19 S02C02 take 09, 2026-08-25). Unsubscribing
        // above is all a page should ever do.
        await Task.CompletedTask;
    }


}
