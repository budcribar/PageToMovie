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

public partial class ScenesSceneList : PageSliceComponent
{
    [CascadingParameter] public required Scenes Host { get; set; }
    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }

    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }

    [CascadingParameter] public Scenes.ScenesMusic? Music { get; set; }

    [CascadingParameter] public Scenes.ScenesPlayback? Playback { get; set; }

    [CascadingParameter] public Scenes.ScenesDialogueVerify? Dialogue { get; set; }


    [CascadingParameter] public Scenes.ScenesHistory? History { get; set; }

    [CascadingParameter] public Scenes.ScenesClipSelection? ClipSel { get; set; }

    private string PlaySelectedTitle
    {
        get
        {
            if (Playback.CanPlaySelected)
                return "Stitch selected scenes in the browser (composites or clips)";
            if (Host.List._selected.Count == 0)
                return "Select one or more scenes first";
            return "Selected scenes have no clips or composites to play yet";
        }
    }

    private string VerifyDialogueTitle
    {
        get
        {
            if (Dialogue.SelectedScenesHaveClipsToVerify)
                return "Check the spoken words in each finished clip against the screenplay";
            if (Host.List._selected.Count == 0)
                return "Select one or more scenes with finished clips first";
            return "Selected scenes have no finished clips to check yet";
        }
    }
}
