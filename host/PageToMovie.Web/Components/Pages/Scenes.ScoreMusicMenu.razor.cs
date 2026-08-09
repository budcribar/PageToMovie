using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_ScoreMusicMenu
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public IReadOnlyList<SupportedModelDto> Models { get; set; } = Array.Empty<SupportedModelDto>();
    [Parameter] public string SelectedModelId { get; set; } = "";
    [Parameter] public EventCallback<string> SelectedModelIdChanged { get; set; }
    [Parameter] public bool WantVocal { get; set; }
    [Parameter] public EventCallback<bool> WantVocalChanged { get; set; }
    [Parameter] public bool CanSing { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool Scoring { get; set; }
    [Parameter] public bool GenerateDisabled { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnGenerate { get; set; }

    private Task OnModelChange(ChangeEventArgs e) =>
        SelectedModelIdChanged.InvokeAsync(e.Value?.ToString() ?? SelectedModelId);
}
