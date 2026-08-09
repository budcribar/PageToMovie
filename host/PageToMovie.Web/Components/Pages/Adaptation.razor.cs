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

namespace PageToMovie.Web.Components.Pages;

public partial class Adaptation
{
    protected override async Task OnInitializedAsync()
    {
        try
        {
            var projs = await Engine.GetProjectsAsync();
            var projectId = projs?.Active?.Id;
            if (string.IsNullOrEmpty(projectId))
                projectId = projs?.Projects.Select(p => p.Id).FirstOrDefault(id => !string.IsNullOrEmpty(id));

            if (string.IsNullOrEmpty(projectId))
            {
                Nav.NavigateTo("/adaptation/import", replace: true);
                return;
            }

            var dto = await Engine.GetAdaptationAsync(projectId);
            var path = AdaptationPageBase.AdaptationStepUi.SuggestedStepPath(dto?.Adaptation);
            Nav.NavigateTo(path, replace: true);
        }
        catch
        {
            Nav.NavigateTo("/adaptation/import", replace: true);
        }
    }
}
