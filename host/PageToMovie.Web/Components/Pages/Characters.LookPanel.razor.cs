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

public partial class Characters_LookPanel
{
    [CascadingParameter] public Characters Host { get; set; } = default!;
    [CascadingParameter] public Characters.CharactersListState? List { get; set; }

    [CascadingParameter] public Characters.CharactersJobs? Jobs { get; set; }

    [CascadingParameter] public Characters.CharactersLookPipeline? LookPipe { get; set; }

    [CascadingParameter] public Characters.CharactersLookEditors? LookEdit { get; set; }

    [CascadingParameter] public Characters.CharactersLookBook? LookBook { get; set; }

    [CascadingParameter] public Characters.CharactersVoice? Voice { get; set; }

}
