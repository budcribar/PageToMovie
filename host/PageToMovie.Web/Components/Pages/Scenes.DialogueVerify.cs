using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: ScenesDialogueVerify → Host.*
public partial class Scenes
{
    internal void SelectMismatchedClips() => Dialogue.SelectMismatchedClips();

    internal static MarkupString RenderDiffHtml(string? expected, string? heard) => ScenesDialogueVerify.RenderDiffHtml(expected, heard);

    internal Task VerifyClipDialogueManualAsync(ClipSummary clip) => Dialogue.VerifyClipDialogueManualAsync(clip);

    internal Task VerifySelectedScenesDialogueAsync() => Dialogue.VerifySelectedScenesDialogueAsync();



    internal bool SelectedScenesHaveClipsToVerify => Dialogue.SelectedScenesHaveClipsToVerify;

}
