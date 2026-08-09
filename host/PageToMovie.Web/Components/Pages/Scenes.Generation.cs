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

// Forwarders: ScenesGeneration → Host.*
public partial class Scenes
{
    internal bool IsSceneGenBusy(int sceneNumber) => Gen.IsSceneGenBusy(sceneNumber);

    internal static bool IsScenesWorkflowJob(string? kind) => ScenesGeneration.IsScenesWorkflowJob(kind);

    internal static string LiveGenStatusLabel(JobSnapshot job) => ScenesGeneration.LiveGenStatusLabel(job);

    internal int LiveGenProgressPercent(JobSnapshot job) => Gen.LiveGenProgressPercent(job);

    internal void OnJobUpdated(JobSnapshot snap) => Gen.OnJobUpdated(snap);

    internal bool ShouldRefreshSceneListWhileRunning(JobSnapshot snap) => Gen.ShouldRefreshSceneListWhileRunning(snap);

    internal Task SoftReloadListLiveAsync() => Gen.SoftReloadListLiveAsync();

    internal void OnJobLog(string line) => Gen.OnJobLog(line);

    internal Task SoftReloadAsync() => Gen.SoftReloadAsync();

    internal Task RefreshMyJobsAsync() => Gen.RefreshMyJobsAsync();

    internal Task GenOneSceneAsync(int sn) => Gen.GenOneSceneAsync(sn);

    internal Task StartBatchAsync() => Gen.StartBatchAsync();

    internal bool IsCreditsSceneNum(int sn) => Gen.IsCreditsSceneNum(sn);

    internal Task GenerateCreditsEntryAsync(int sn) => Gen.GenerateCreditsEntryAsync(sn);

    internal Task RenderCreditsSceneClientSideAsync(int sn) => Gen.RenderCreditsSceneClientSideAsync(sn);

    internal Task RenderOneCreditsClipAsync(int sn, int clip, double durationSeconds, int width, int height) => Gen.RenderOneCreditsClipAsync(sn, clip, durationSeconds, width, height);

    internal Task CancelAsync() => Gen.CancelAsync();

    internal Task EnsureHubAsync() => Gen.EnsureHubAsync();

    internal Task OpenGenerateConfirmAsync() => Gen.OpenGenerateConfirmAsync();

    internal void CloseGenerateConfirm() => Gen.CloseGenerateConfirm();

    internal Task ConfirmGenerateAsync() => Gen.ConfirmGenerateAsync();

    internal Task LoadVideoModelsAsync() => Gen.LoadVideoModelsAsync();



    internal bool HasCreditsScene => Gen.HasCreditsScene;
    internal bool JobRunning => Gen.JobRunning;
    internal bool ShowLiveGenProgress => Gen.ShowLiveGenProgress;
    internal bool ShowOperatorGenError => Gen.ShowOperatorGenError;
    internal bool ShowOperatorGenPartial => Gen.ShowOperatorGenPartial;

}
