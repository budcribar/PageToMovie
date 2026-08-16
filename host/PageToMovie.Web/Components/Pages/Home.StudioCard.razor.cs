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

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Home_StudioCard : PageSliceComponent
{
    [CascadingParameter] public required Home Host { get; set; }
    [CascadingParameter] public Home.HomeProjects? Projects { get; set; }
    [CascadingParameter] public Home.HomeJobs? Jobs { get; set; }
    [CascadingParameter] public Home.HomeImport? Import { get; set; }
    [CascadingParameter] public Home.HomeCheckpoints? Checkpoints { get; set; }
    [CascadingParameter] public Home.HomeCosts? Costs { get; set; }


    private ElementReference NameInputRef
    {
        get => Host.Projects._nameInputRef;
        set => Host.Projects._nameInputRef = value;
    }
}
