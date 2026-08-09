using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_GenerateConfirm
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int EstimatedClips { get; set; }
    [Parameter] public int SelectedCount { get; set; }
    [Parameter] public bool ResolutionLocked { get; set; }
    [Parameter] public string? ResolutionLock { get; set; }
    [Parameter] public string Resolution { get; set; } = "720p";
    [Parameter] public EventCallback<string> ResolutionChanged { get; set; }
    [Parameter] public bool ShowModelPicker { get; set; }
    [Parameter] public IReadOnlyList<SupportedModelDto> VideoModels { get; set; } = Array.Empty<SupportedModelDto>();
    [Parameter] public string SelectedVideoModel { get; set; } = "";
    [Parameter] public EventCallback<string> SelectedVideoModelChanged { get; set; }
    [Parameter] public bool CostReady { get; set; }
    [Parameter] public double EstimatedCostUsd { get; set; }
    [Parameter] public bool AllCredits { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool GenerateDisabled { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnConfirm { get; set; }
    [Parameter] public EventCallback OnResolutionCommitted { get; set; }

    private async Task OnResolutionChange(ChangeEventArgs e)
    {
        var v = e.Value?.ToString() ?? Resolution;
        await ResolutionChanged.InvokeAsync(v);
        await OnResolutionCommitted.InvokeAsync();
    }

    private Task OnModelChange(ChangeEventArgs e) =>
        SelectedVideoModelChanged.InvokeAsync(e.Value?.ToString() ?? "");
}
