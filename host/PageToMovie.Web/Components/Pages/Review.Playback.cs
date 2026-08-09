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

// Forwarders: ReviewPlayback → Host.*
public partial class Review
{
    internal Task LoadPreferredVideoEditorAsync() => Playback.LoadPreferredVideoEditorAsync();

    internal Task DubInMyVoiceAsync() => Playback.DubInMyVoiceAsync();

    internal Task OpenInExternalEditorAsync() => Playback.OpenInExternalEditorAsync();

    internal Task RefreshWipMetaAsync() => Playback.RefreshWipMetaAsync();

    internal Task PlayWipAsync() => Playback.PlayWipAsync();

    internal Task ConnectFolderForWipAsync() => Playback.ConnectFolderForWipAsync();

    internal Task PlaySceneAsync(int scene) => Playback.PlaySceneAsync(scene);

    internal Task HideScenePlayerAsync() => Playback.HideScenePlayerAsync();

    internal Task HideWipPlayerAsync() => Playback.HideWipPlayerAsync();

    internal void PlayClip(int scene, int clip) => Playback.PlayClip(scene, clip);

    internal void HideClipPlayer() => Playback.HideClipPlayer();

    internal static string CacheBust(string url) => ReviewPlayback.CacheBust(url);

    internal string? WipServerSrc() => Playback.WipServerSrc();

    internal string? SceneServerSrc(int sn) => Playback.SceneServerSrc(sn);

    internal string? ClipServerSrc(int scene, int clip) => Playback.ClipServerSrc(scene, clip);


    internal bool CanPlayMovie => Playback.CanPlayMovie;
    internal bool HasGeneratedClips => Playback.HasGeneratedClips;
    internal string WipPlayTitle => Playback.WipPlayTitle;
}
