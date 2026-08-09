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

// Forwarders: ScenesHistory → Host.*
public partial class Scenes
{
    internal Task OpenSceneHistoryAsync(int sceneNumber) => History.OpenSceneHistoryAsync(sceneNumber);

    internal void CloseSceneHistory() => History.CloseSceneHistory();

    internal void HideSceneHistory() => History.HideSceneHistory();

    internal Task OnSceneHistoryRestored() => History.OnSceneHistoryRestored();

    internal Task RevertSceneToVersionAsync(int sceneNumber, string commitHash) => History.RevertSceneToVersionAsync(sceneNumber, commitHash);

}
