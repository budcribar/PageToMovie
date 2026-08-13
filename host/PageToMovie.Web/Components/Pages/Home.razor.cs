using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Home : IAsyncDisposable
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


    internal bool? _healthOk;

    internal bool _busy;

    internal string? _error;

    internal string? _message;


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


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        L.CultureChanged += OnCultureChanged;
        Hub.JobUpdated += Jobs.OnJobUpdated;
        Hub.JobLog += Jobs.OnJobLog;
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
        await Projects.LoadAsync();
        await Costs.LoadDemoShowcaseAsync();
        try
        {
            await Hub.StartAsync();
        }
        catch
        {
            // SignalR optional for browse
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


    private void OnCultureChanged(System.Globalization.CultureInfo _culture) => _ = InvokeAsync(StateHasChanged);


    public async ValueTask DisposeAsync()
    {
        L.CultureChanged -= OnCultureChanged;
        Hub.JobUpdated -= Jobs.OnJobUpdated;
        Hub.JobLog -= Jobs.OnJobLog;
        await Hub.DisposeAsync();
    }


}
