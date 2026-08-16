using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;
using static PageToMovie.Web.Components.CostFormatting;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ProjectCosts : IPageSliceHost
{
    /// <summary>Slice host (see <see cref="IPageSliceHost"/>): the page-local sections are slices.</summary>
    public event Action? Rendered;

    public void RenderRequestedBySlice() => StateHasChanged();

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        Rendered?.Invoke();
    }

    internal bool _busy;
    internal string? _error;
    internal string? _message;
    private string _projectId = "";
    internal CostReport? _report;
    internal string _draftRes = "480p";
    internal string _heroRes = "720p";
    internal double _retries = 0.5;
    internal string? _openCategory;
    internal bool _showAdvanced;
    internal EngineApiClient.ApiCostByProviderStatsDto? _byProvider;

    private static readonly (string Id, string Label, string Color)[] Categories =
    {
        (CostCategories.Screenplay, CostCategories.Label(CostCategories.Screenplay), "#38bdf8"),
        (CostCategories.Characters, CostCategories.Label(CostCategories.Characters), "#a78bfa"),
        (CostCategories.Video, CostCategories.Label(CostCategories.Video), "#34d399"),
        (CostCategories.Voice, CostCategories.Label(CostCategories.Voice), "#fbbf24"),
        (CostCategories.Music, CostCategories.Label(CostCategories.Music), "#f472b6"),
        (CostCategories.Review, CostCategories.Label(CostCategories.Review), "#fb923c"),
        (CostCategories.Other, CostCategories.Label(CostCategories.Other), "#94a3b8"),
    };

    internal sealed record CostSlice(string Id, string Label, double Usd, string Color);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _projectId = await CostFormatting.ResolveActiveProjectIdAsync(Engine);
            if (string.IsNullOrEmpty(_projectId)) return;

            (_draftRes, _retries) = await CostFormatting.ReadResolutionAndRetriesAsync(Engine, _projectId, _draftRes, _retries);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    internal Task OnAdvancedDraftResAsync(ChangeEventArgs e) =>
        SetDraftResolutionAsync(e.Value?.ToString() ?? _draftRes);

    private async Task SetDraftResolutionAsync(string res)
    {
        var applied = await CostFormatting.TrySetDraftResolutionAsync(Engine, _projectId, res, _draftRes, _report is not null);
        if (applied is null) return;
        _draftRes = applied;
        await LoadAsync();
    }

    internal async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var dto = await Engine.GetCostAsync(_projectId, _draftRes, _heroRes, _retries);
            _report = dto?.Cost;
            if (_report is not null && !string.IsNullOrWhiteSpace(_report.DraftResolution))
                _draftRes = _report.DraftResolution;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _report = null;
        }
        finally { _busy = false; }

        try
        {
            var byProviderDto = await Engine.GetCostByProviderAsync(_projectId);
            _byProvider = byProviderDto?.Stats;
        }
        catch { _byProvider = null; }
    }

    internal async Task BackfillAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var result = await Engine.BackfillCostAsync(_projectId);
            var b = result?.Backfill;
            _message = b is null ? "Done" : $"Updated spend history ({b.Added} new, {b.Skipped} skipped).";
            await LoadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal void ToggleCategory(string id) =>
        _openCategory = string.Equals(_openCategory, id, StringComparison.OrdinalIgnoreCase) ? null : id;

    internal List<CostSlice> BuildSpentSlices()
    {
        var totals = Categories.ToDictionary(c => c.Id, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        if (_report?.RecentEvents is { Count: > 0 } events)
        {
            foreach (var e in events)
            {
                var cat = MapCategory(e.Kind, mode: null, category: e.Category);
                totals[cat] = totals.GetValueOrDefault(cat) + e.Usd;
            }
        }
        else if (_report?.Actual?.ByKind is { Count: > 0 } byKind)
        {
            foreach (var kv in byKind)
            {
                var cat = MapCategory(kv.Key);
                totals[cat] = totals.GetValueOrDefault(cat) + kv.Value;
            }
        }

        var sliceSum = totals.Values.Sum();
        var actual = _report?.Summary.ActualUsd ?? 0;
        if (actual > sliceSum + 0.02 && _report?.Actual?.ByKind is { Count: > 0 } bk)
        {
            totals = Categories.ToDictionary(c => c.Id, _ => 0.0, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in bk)
            {
                var cat = MapCategory(kv.Key);
                totals[cat] = totals.GetValueOrDefault(cat) + kv.Value;
            }
        }

        return Categories
            .Select(c => new CostSlice(c.Id, c.Label, Math.Round(totals.GetValueOrDefault(c.Id), 2), c.Color))
            .ToList();
    }

    internal List<CostEvent> EventsForCategory(string categoryId)
    {
        if (_report?.RecentEvents is not { Count: > 0 } events)
            return new List<CostEvent>();
        return events
            .Where(e => string.Equals(MapCategory(e.Kind, category: e.Category), categoryId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Ts ?? "")
            .ToList();
    }

    internal List<CostSlice> BuildEstimateSlices()
    {
        var by = _report?.EstimateByCategory;
        return Categories
            .Select(c =>
            {
                var usd = 0.0;
                if (by is not null)
                {
                    if (by.TryGetValue(c.Id, out var v)) usd = v;
                    else
                        usd = by.Where(kv => string.Equals(kv.Key, c.Id, StringComparison.OrdinalIgnoreCase))
                            .Select(kv => kv.Value)
                            .FirstOrDefault();
                }
                return new CostSlice(c.Id, c.Label, Math.Round(usd, 2), c.Color);
            })
            .ToList();
    }

    internal static string MapCategory(string? kind, string? mode = null, string? category = null) =>
        CostCategories.Resolve(kind, mode, category);

    internal static string EventDetail(CostEvent e)
    {
        var bits = new List<string>();
        if (e.Scene is int sn)
            bits.Add(e.Clip is int cn ? $"Scene {sn} · clip {cn}" : $"Scene {sn}");
        if (!string.IsNullOrWhiteSpace(e.Character))
            bits.Add(KeyFormatting.ShortChar(e.Character));
        if (!string.IsNullOrWhiteSpace(e.Resolution))
            bits.Add(e.Resolution);
        if (e.DurationSec is double d && d > 0)
            bits.Add($"{d:0.#}s");
        if (!string.IsNullOrWhiteSpace(e.Model))
            bits.Add(e.Model);
        if (bits.Count == 0)
            bits.Add(string.IsNullOrWhiteSpace(e.Kind) ? "Work item" : e.Kind);
        return string.Join(" · ", bits);
    }

    internal static string FormatTs(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "—";
        if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToLocalTime().ToString("MMM d · HH:mm");
        return ts.Length <= 16 ? ts : ts[..16];
    }

    internal static int ProgressPct(CostReportSummary s) =>
        (int)Math.Round(100.0 * s.ClipsOnDisk / Math.Max(1, s.ClipsTotal));
}
