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

// Forwarders: ScenesMusic → Host.*
public partial class Scenes
{
    internal void OpenScoreMenu(int sceneNum) => Music.OpenScoreMenu(sceneNum);

    internal void CloseScoreMenu() => Music.CloseScoreMenu();

    internal Task ScoreFromMenuAsync(int sceneNum) => Music.ScoreFromMenuAsync(sceneNum);

    internal Task LoadAudioModelsAsync() => Music.LoadAudioModelsAsync();

    internal Task ScoreSceneBackgroundMusicAsync(int sceneNum) => Music.ScoreSceneBackgroundMusicAsync(sceneNum);

    internal Task CompleteSceneMusicDownloadAsync(string jobId, int sceneNum) => Music.CompleteSceneMusicDownloadAsync(jobId, sceneNum);

    internal Task OpenMusicCompareAsync(int sceneNumber) => Music.OpenMusicCompareAsync(sceneNumber);

    internal void CloseMusicCompare() => Music.CloseMusicCompare();

    internal Task RefreshMusicCompareUrlsAsync() => Music.RefreshMusicCompareUrlsAsync();

    internal Task PromoteMusicVersionAsync(int sceneNumber, string takeId) => Music.PromoteMusicVersionAsync(sceneNumber, takeId);

    internal Task SoftDeleteMusicVersionAsync(int sceneNumber, string takeId) => Music.SoftDeleteMusicVersionAsync(sceneNumber, takeId);

    internal Task RestoreMusicVersionAsync(int sceneNumber, string takeId) => Music.RestoreMusicVersionAsync(sceneNumber, takeId);



    internal bool SelectedAudioModelCanSing => Music.SelectedAudioModelCanSing;

}
