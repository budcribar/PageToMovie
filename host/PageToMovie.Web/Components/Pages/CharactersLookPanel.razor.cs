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

public partial class CharactersLookPanel : PageSliceComponent
{
    [CascadingParameter] public required Characters Host { get; set; }
    [CascadingParameter] public Characters.CharactersListState List { get; set; } = default!;

    [CascadingParameter] public Characters.CharactersJobs Jobs { get; set; } = default!;

    [CascadingParameter] public Characters.CharactersLookPipeline LookPipe { get; set; } = default!;

    [CascadingParameter] public Characters.CharactersLookEditors LookEdit { get; set; } = default!;

    [CascadingParameter] public Characters.CharactersLookBook LookBook { get; set; } = default!;

    [CascadingParameter] public Characters.CharactersVoice Voice { get; set; } = default!;
}
