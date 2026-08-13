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

public partial class Review
{
    /// <summary>Jobs domain for the Review page. Owns related UI state and behavior.</summary>
    public sealed class ReviewJobs
    {
        private readonly Review S;
        public ReviewJobs(Review host) => S = host;

        internal JobSnapshot? _job;


        internal bool JobRunning =>
            string.Equals(_job?.Status, "running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_job?.Status, "queued", StringComparison.OrdinalIgnoreCase);


        internal void OnJobUpdated(JobSnapshot snap)
        {
            _job = snap;
            if (IsTerminalJobStatus(snap.Status))
                _ = S.InvokeAsync(() => HandleTerminalJobAsync(snap));
            else
                _ = S.InvokeAsync(S.StateHasChanged);
        }

        private static bool IsTerminalJobStatus(string? status) =>
            status is "done" or "partial" or "error" or "cancelled";

        private async Task HandleTerminalJobAsync(JobSnapshot snap)
        {
            await S.List.SoftLoadAsync();
            if (IsClipAutoReviewDone(snap, out var rs, out var rc))
                await ApplyClipAutoReviewDoneAsync(rs, rc);
            else if (IsClipAutoReviewBatchDone(snap))
                await ApplyClipAutoReviewBatchDoneAsync(snap);
            else if (IsClipAutoReviewError(snap))
                ApplyClipAutoReviewError(snap);
            else if (IsRemuxDone(snap))
                await ApplyRemuxDoneAsync(snap);
            else if (IsSceneGenDone(snap, out var genSn, out var genCn))
                await ApplySceneGenDoneAsync(genSn, genCn);
            else if (IsYouTubeUpload(snap))
                await ApplyYouTubeUploadAsync(snap);
            S.StateHasChanged();
        }

        private static bool IsClipAutoReviewDone(JobSnapshot snap, out int rs, out int rc)
        {
            if (snap.Status == "done" &&
                string.Equals(snap.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) &&
                snap.Scene is int scene && snap.Clip is int clip)
            {
                rs = scene;
                rc = clip;
                return true;
            }

            rs = 0;
            rc = 0;
            return false;
        }

        private static bool IsClipAutoReviewBatchDone(JobSnapshot snap) =>
            snap.Status == "done" &&
            string.Equals(snap.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase);

        private static bool IsClipAutoReviewError(JobSnapshot snap) =>
            snap.Status == "error" &&
            (string.Equals(snap.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(snap.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase));

        private static bool IsRemuxDone(JobSnapshot snap) =>
            snap.Status == "done" &&
            string.Equals(snap.Kind, "remux", StringComparison.OrdinalIgnoreCase);

        private static bool IsSceneGenDone(JobSnapshot snap, out int genSn, out int genCn)
        {
            if (snap.Status == "done" &&
                string.Equals(snap.Kind, "scene", StringComparison.OrdinalIgnoreCase) &&
                snap.Scene is int scene &&
                snap.Clip is int clip)
            {
                genSn = scene;
                genCn = clip;
                return true;
            }

            genSn = 0;
            genCn = 0;
            return false;
        }

        private static bool IsYouTubeUpload(JobSnapshot snap) =>
            string.Equals(snap.Kind, "youtube_upload", StringComparison.OrdinalIgnoreCase);

        private async Task ApplyClipAutoReviewDoneAsync(int rs, int rc)
        {
            try
            {
                var d = await S.Engine.GetClipAutoReviewDraftAsync(S._projectId, rs, rc);
                if (d is not null)
                    S.AutoReview._drafts[$"S{rs:D2}C{rc:D2}"] = d;
                S._message = d is null
                    ? $"Review finished S{rs:D2}C{rc:D2}"
                    : $"Review ready S{rs:D2}C{rc:D2}: {d.Suggestion} — Apply suggestions or Pass/Fail";
                S._error = null;
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
        }

        private async Task ApplyClipAutoReviewBatchDoneAsync(JobSnapshot snap)
        {
            try
            {
                await RefreshSelectedSceneDraftsAsync();
                S._message = snap.Message ?? "Batch auto-review finished";
                S._error = null;
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
        }

        private async Task RefreshSelectedSceneDraftsAsync()
        {
            // Refresh per-clip drafts for selected scene after batch
            if (S.List._selectedScene is not int sel)
                return;
            for (var c = 1; c <= S.List.ClipCountFor(sel); c++)
            {
                var d = await S.Engine.GetClipAutoReviewDraftAsync(S._projectId, sel, c);
                if (d is not null)
                    S.AutoReview._drafts[ReviewAutoReview.ClipKey(sel, c)] = d;
            }
        }

        private void ApplyClipAutoReviewError(JobSnapshot snap)
        {
            S._error = S.Session.IsAdmin
                ? (snap.Error ?? snap.Message ?? "Auto-review failed")
                : "Auto-review failed. Try again.";
            S._message = null;
        }

        private async Task ApplyRemuxDoneAsync(JobSnapshot snap)
        {
            await S.List.SoftLoadAsync();
            await S.Playback.RefreshWipMetaAsync();
            if (S.Playback._playWipAfterRemux)
                ApplyRemuxPlayWip();
            else if (S.Playback._playSceneAfterRemux is int playSn)
                ApplyRemuxPlayScene(playSn);
            else if (snap.Scene is int sn && sn > 0)
                ApplyRemuxPlayComposite(sn);
        }

        private void ApplyRemuxPlayWip()
        {
            S.Playback._playWipAfterRemux = false;
            if (!S.Playback._wipExists)
                return;
            S.Playback._showWipPlayer = true;
            S.Playback._wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = S.Playback._wipStale
                ? "WIP rebuilt but still marked stale — check clips"
                : "WIP ready — player below";
        }

        private void ApplyRemuxPlayScene(int playSn)
        {
            S.Playback._playSceneAfterRemux = null;
            S.Playback._playingScene = playSn;
            S.Playback._showScenePlayer = true;
            S.Playback._sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = $"Scene S{playSn:D2} ready — playing";
        }

        private void ApplyRemuxPlayComposite(int sn)
        {
            S.Playback._playingScene = sn;
            S.Playback._showScenePlayer = true;
            S.Playback._sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = $"Scene S{sn:D2} composite rebuilt — player below";
        }

        private async Task ApplySceneGenDoneAsync(int genSn, int genCn)
        {
            S._message = $"Clip S{genSn:D2}C{genCn:D2} gen finished — Play scene when you want the updated composite";
            if (S.List._selectedScene == genSn)
                await S.List.LoadSelectedDetailAsync(genSn);
        }

        private async Task ApplyYouTubeUploadAsync(JobSnapshot snap)
        {
            if (snap.Status == "done")
            {
                await S.Share.RefreshYouTubeStatusAsync();
                S._message = snap.Message ?? "Uploaded to YouTube";
                S._error = null;
            }
            else if (snap.Status == "error")
            {
                S._error = snap.Error ?? snap.Message ?? "YouTube upload failed";
                S._message = null;
            }
        }


        internal void OnJobLog(string line)
        {
            if (_job is not null)
                _job.Message = line;
            _ = S.InvokeAsync(S.StateHasChanged);
        }


        internal async Task CancelAsync()
        {
            _ = await S.Engine.TryCancelJobAsync();
            if (_job is not null)
            {
                _job.Status = "cancelled";
                _job.Message = "Cancelled";
                _job.FinishedAt = DateTimeOffset.UtcNow;
            }
            S._busy = false;
            S._error = null;
            S._message = "Cancelled. You can try again when ready.";
            S.StateHasChanged();
        }


        internal Task EnsureHubAsync() => S.Hub.EnsureStartedAsync();

    }
}
