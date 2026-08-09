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

// Forwarders: ScenesPlayback → Host.*
public partial class Scenes
{
    internal Task LoadClipVideoAndTakesCountAsync(int scene, int clip) => Playback.LoadClipVideoAndTakesCountAsync(scene, clip);

    internal Task PlaySceneCompositeAsync(int sn) => Playback.PlaySceneCompositeAsync(sn);

    internal Task HideScenePlayer() => Playback.HideScenePlayer();

    internal string? ScenePlayerSrc(int sn) => Playback.ScenePlayerSrc(sn);

    internal Task HidePreviewPlayerAsync() => Playback.HidePreviewPlayerAsync();

    internal bool CanPlaySelected => Playback.CanPlaySelected;

    internal Task PlaySelectedAsync() => Playback.PlaySelectedAsync();

    internal string? CompareVideoUrl(ClipVersionItem v) => Playback.CompareVideoUrl(v);

    internal Task RefreshCompareVideoUrlsAsync() => Playback.RefreshCompareVideoUrlsAsync();



}
