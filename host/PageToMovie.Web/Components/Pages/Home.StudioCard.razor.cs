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

public partial class Home_StudioCard : IDisposable
{
    [CascadingParameter] public required Home Host { get; set; }
    [CascadingParameter] public Home.HomeProjects? Projects { get; set; }
    [CascadingParameter] public Home.HomeJobs? Jobs { get; set; }
    [CascadingParameter] public Home.HomeImport? Import { get; set; }
    [CascadingParameter] public Home.HomeCheckpoints? Checkpoints { get; set; }
    [CascadingParameter] public Home.HomeCosts? Costs { get; set; }

    // All cascading values are IsFixed, so this card is not re-rendered when Home is. Handlers
    // that live on Home (the delete confirm modal) mutate the project list / selection this card
    // renders — without this the picker kept a deleted project in its list. Follow Home's renders.
    protected override void OnInitialized()
    {
        base.OnInitialized();
        Host.Rendered += OnHostRendered;
    }

    private void OnHostRendered() => StateHasChanged();

    public void Dispose() => Host.Rendered -= OnHostRendered;

    private ElementReference NameInputRef
    {
        get => Host.Projects._nameInputRef;
        set => Host.Projects._nameInputRef = value;
    }
}
