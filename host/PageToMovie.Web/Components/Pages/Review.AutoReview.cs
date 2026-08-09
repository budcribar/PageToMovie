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

// Forwarders: ReviewAutoReview → Host.*
public partial class Review
{
    internal IEnumerable<SceneSummary> SortedReviewScenes => AutoReview.SortedReviewScenes;

    internal Task ReviewAsync(int scene, int clip, string status) => AutoReview.ReviewAsync(scene, clip, status);

    internal static string ClipKey(int scene, int clip) => ReviewAutoReview.ClipKey(scene, clip);

    internal bool IsAutoReviewRunning(int scene, int clip) => AutoReview.IsAutoReviewRunning(scene, clip);

    internal ClipAutoReviewDraft? GetLocalDraft(int scene, int clip) => AutoReview.GetLocalDraft(scene, clip);

    internal bool HasIncludedEdits() => AutoReview.HasIncludedEdits();

    internal Task StartAutoReviewAsync(int scene, int clip) => AutoReview.StartAutoReviewAsync(scene, clip);

    internal Task<IReadOnlyList<string>> ResolveSceneUrlsForReviewAsync(int scene) => AutoReview.ResolveSceneUrlsForReviewAsync(scene);

    internal Task StartFullMovieReviewAsync() => AutoReview.StartFullMovieReviewAsync();

    internal Task StartBatchAutoReviewAsync() => AutoReview.StartBatchAutoReviewAsync();

    internal void OpenApplyPanel(int scene, int clip) => AutoReview.OpenApplyPanel(scene, clip);

    internal void CloseApplyPanel() => AutoReview.CloseApplyPanel();

    internal Task ApplyAndRegenAsync(int scene, int clip) => AutoReview.ApplyAndRegenAsync(scene, clip);

}
