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

public partial class Scenes_ClipInspector
{
    [CascadingParameter] public Scenes Host { get; set; } = default!;
    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }

    [CascadingParameter] public Scenes.ScenesClipForm? ClipForm { get; set; }

    [CascadingParameter] public Scenes.ScenesClipVersions? ClipVer { get; set; }

    [CascadingParameter] public Scenes.ScenesPlayback? Playback { get; set; }

    [CascadingParameter] public Scenes.ScenesMusic? Music { get; set; }

    [CascadingParameter] public Scenes.ScenesDialogueVerify? Dialogue { get; set; }

    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }


    [CascadingParameter] public Scenes.ScenesClipSelection? ClipSel { get; set; }

    [CascadingParameter] public Scenes.ScenesClipRegen? ClipRegen { get; set; }

    private void DismissTakeReason()
    {
        if (Gen is null) return;
        Gen._pendingTakeReasonScene = null;
        Gen._pendingTakeReasonClip = null;
        Gen._takeReasonSaved = null;
    }

    private async Task SubmitTakeReasonAsync(string reason)
    {
        if (Gen is null || ListState?._detail is null || ClipForm?._clip is null) return;
        var sn = Gen._pendingTakeReasonScene ?? ListState._detail.SceneNumber;
        var cn = Gen._pendingTakeReasonClip ?? ClipForm._clip.ClipNumber;
        try
        {
            await Engine.SetTakeReasonAsync(Host._projectId, sn, cn, reason);
            Gen._takeReasonSaved = $"Thanks — noted as {VideoTakeReasons.Display(reason)}.";
            // Keep chips a moment then clear
            await Task.Delay(900);
            DismissTakeReason();
        }
        catch
        {
            // H9 fail-open
            DismissTakeReason();
        }
    }
}
