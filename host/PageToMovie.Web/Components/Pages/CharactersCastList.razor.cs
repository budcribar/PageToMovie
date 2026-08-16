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

public partial class CharactersCastList : PageSliceComponent
{
    [CascadingParameter] public required Characters Host { get; set; }
    [CascadingParameter] public Characters.CharactersListState? List { get; set; }

    [CascadingParameter] public Characters.CharactersLookPipeline? LookPipe { get; set; }

    private string ItemTitle(CharacterSummary c)
    {
        if (List.CastListLocked) return "Wait until the current action finishes";
        if (!c.UsedInPlan) return "Not in current shot plan — kept for later";
        return c.DisplayName;
    }
}
