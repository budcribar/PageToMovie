using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

using static PageToMovie.Web.Components.CostFormatting;
namespace PageToMovie.Web.Components.Pages;

public partial class AccountCosts
{
    internal bool _loading = true;
    internal string? _error;
    internal string _activeProjectId = "";
    private EngineApiClient.UserSpendSummaryDto? _mySpend;

    private sealed record Slice(string Label, double Usd, string Color);

    // Categorical palette (distinct hues; reused across both pies).
    private static readonly string[] Palette =
    {
        "#38bdf8", "#a78bfa", "#34d399", "#fbbf24", "#f472b6",
        "#fb923c", "#60a5fa", "#f87171", "#4ade80", "#c084fc", "#94a3b8",
    };

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            try
            {
                var projs = await Engine.GetProjectsAsync();
                _activeProjectId = projs?.Active?.Id ?? "";
            }
            catch { _activeProjectId = ""; }

            _mySpend = await Engine.GetMySpendAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _mySpend = null;
        }
        finally { _loading = false; }
    }

    /// <summary>Top projects by charge, remainder grouped as "Other" so the pie stays readable.</summary>
    private List<Slice> BuildProjectSlices()
    {
        if (_mySpend is null) return new();
        var rows = _mySpend.ByProject
            .Where(p => p.ChargeUsd > 0)
            .OrderByDescending(p => p.ChargeUsd)
            .ToList();
        return ToSlices(rows.Select(p => (p.ProjectId, p.ChargeUsd)));
    }

    private List<Slice> BuildVendorSlices()
    {
        if (_mySpend is null) return new();
        var rows = _mySpend.ByProvider
            .Select(kv => (Label: ProviderLabel(kv.Key),
                           Usd: kv.Value.TotalChargeUsd > 0 ? kv.Value.TotalChargeUsd : kv.Value.TotalUsd))
            .Where(x => x.Usd > 0)
            .OrderByDescending(x => x.Usd)
            .ToList();
        return ToSlices(rows.Select(x => (x.Label, x.Usd)));
    }

    private static List<Slice> ToSlices(IEnumerable<(string Label, double Usd)> rows)
    {
        const int maxSlices = 8;
        var list = rows.ToList();
        var slices = new List<Slice>();
        for (var i = 0; i < list.Count && i < maxSlices; i++)
            slices.Add(new Slice(list[i].Label, Math.Round(list[i].Usd, 2), Palette[i % Palette.Length]));
        if (list.Count > maxSlices)
        {
            var otherSum = list.Skip(maxSlices).Sum(x => x.Usd);
            if (otherSum > 0)
                slices.Add(new Slice("Other", Math.Round(otherSum, 2), Palette[maxSlices % Palette.Length]));
        }
        return slices;
    }
}
