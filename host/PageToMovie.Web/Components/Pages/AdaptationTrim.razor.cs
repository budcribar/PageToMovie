using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationTrim
{

    public override string StepKey => "trim";

    private double? _estimateUsd;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        await RefreshEstimateAsync();
    }

    private async Task OnLengthChangedAsync()
    {
        try { await SoftLoadAsync(); } catch { /* ignore */ }
        await RefreshEstimateAsync();
    }

    private async Task RefreshEstimateAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        try
        {
            var dto = await Engine.GetCostAsync(ProjectId);
            _estimateUsd = dto?.Cost?.Summary?.FullFilmAllDraftUsd;
        }
        catch
        {
            _estimateUsd = null;
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task TrimAsync()
    {
        Busy = true;
        BusyMessage = "Fitting the screenplay to length…";
        Error = null;
        Message = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await Engine.TrimScreenplayAsync(ProjectId);
            Message = result?.Message ?? "Done.";
            await SoftLoadAsync();
            await RefreshEstimateAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
        }
    }
}
