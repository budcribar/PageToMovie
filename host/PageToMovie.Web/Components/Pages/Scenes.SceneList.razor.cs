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

public partial class Scenes_SceneList
{
    [CascadingParameter] public required Scenes Host { get; set; }
    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }

    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }

    [CascadingParameter] public Scenes.ScenesMusic? Music { get; set; }

    [CascadingParameter] public Scenes.ScenesPlayback? Playback { get; set; }

    [CascadingParameter] public Scenes.ScenesDialogueVerify? Dialogue { get; set; }


    [CascadingParameter] public Scenes.ScenesHistory? History { get; set; }

    [CascadingParameter] public Scenes.ScenesClipSelection? ClipSel { get; set; }
}
