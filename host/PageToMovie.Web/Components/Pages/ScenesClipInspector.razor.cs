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

public partial class ScenesClipInspector : PageSliceComponent
{
    [CascadingParameter] public Scenes Host { get; set; } = default;
    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }

    [CascadingParameter] public Scenes.ScenesClipForm? ClipForm { get; set; }

    [CascadingParameter] public Scenes.ScenesClipVersions? ClipVer { get; set; }

    [CascadingParameter] public Scenes.ScenesPlayback? Playback { get; set; }

    [CascadingParameter] public Scenes.ScenesMusic? Music { get; set; }

    [CascadingParameter] public Scenes.ScenesDialogueVerify? Dialogue { get; set; }

    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }


    [CascadingParameter] public Scenes.ScenesClipSelection? ClipSel { get; set; }

    [CascadingParameter] public Scenes.ScenesClipRegen? ClipRegen { get; set; }

    /// <summary>Admin Details fold (visual prompt / negative / plan meta), collapsed by default.</summary>
    private bool _showDetails;

    private string VideoEditTitle
    {
        get
        {
            var clip = ClipForm._clip;
            if (!clip.OnDisk) return "Generate this clip first";
            if (Scenes.ScenesClipRegen.ClipExceedsEditDurationCap(clip))
                return $"Clip is {(clip.ActualDurationSeconds ?? clip.DurationSeconds):0.#}s — Grok can only edit clips up to {Scenes.MaxVideoEditInputSeconds:0.#}s";
            return "Edit this clip with an AI text prompt (new take, spends provider credit)";
        }
    }

    private string? GenerateThisClipTitle
    {
        get
        {
            if (!ListState.CastReady) return ListState.CastBlockedTitle;
            if (ClipSel.PreviousClipMissing(ClipForm._clip.ClipNumber))
                return $"Need C{(ClipForm._clip.ClipNumber - 1):D2} on disk";
            return null;
        }
    }

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
