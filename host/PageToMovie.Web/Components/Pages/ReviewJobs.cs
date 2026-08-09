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
    internal sealed class ReviewJobs
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
            if (snap.Status is "done" or "partial" or "error" or "cancelled")
            {
                _ = S.InvokeAsync(async () =>
                {
                    await S.SoftLoadAsync();
                    if (snap.Status == "done" &&
                        string.Equals(snap.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) &&
                        snap.Scene is int rs && snap.Clip is int rc)
                    {
                        try
                        {
                            var d = await S.Engine.GetClipAutoReviewDraftAsync(S._projectId, rs, rc);
                            if (d is not null)
                                S._drafts[$"S{rs:D2}C{rc:D2}"] = d;
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
                    else if (snap.Status == "done" &&
                             string.Equals(snap.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            // Refresh per-clip drafts for selected scene after batch
                            if (S._selectedScene is int sel)
                            {
                                for (var c = 1; c <= S.ClipCountFor(sel); c++)
                                {
                                    var d = await S.Engine.GetClipAutoReviewDraftAsync(S._projectId, sel, c);
                                    if (d is not null)
                                        S._drafts[ReviewAutoReview.ClipKey(sel, c)] = d;
                                }
                            }
                            S._message = snap.Message ?? "Batch auto-review finished";
                            S._error = null;
                        }
                        catch (Exception ex)
                        {
                            S._error = ex.Message;
                        }
                    }
                    else if (snap.Status == "error" &&
                             (string.Equals(snap.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(snap.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase)))
                    {
                        S._error = S.Session.IsAdmin
                            ? (snap.Error ?? snap.Message ?? "Auto-review failed")
                            : "Auto-review failed. Try again.";
                        S._message = null;
                    }
                    else if (snap.Status == "done" &&
                        string.Equals(snap.Kind, "remux", StringComparison.OrdinalIgnoreCase))
                    {
                        await S.SoftLoadAsync();
                        await S.RefreshWipMetaAsync();
                        if (S._playWipAfterRemux)
                        {
                            S._playWipAfterRemux = false;
                            if (S._wipExists)
                            {
                                S._showWipPlayer = true;
                                S._wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                S._message = S._wipStale
                                    ? "WIP rebuilt but still marked stale — check clips"
                                    : "WIP ready — player below";
                            }
                        }
                        else if (S._playSceneAfterRemux is int playSn)
                        {
                            S._playSceneAfterRemux = null;
                            S._playingScene = playSn;
                            S._showScenePlayer = true;
                            S._sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            S._message = $"Scene S{playSn:D2} ready — playing";
                        }
                        else if (snap.Scene is int sn && sn > 0)
                        {
                            S._playingScene = sn;
                            S._showScenePlayer = true;
                            S._sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            S._message = $"Scene S{sn:D2} composite rebuilt — player below";
                        }
                    }
                    else if (snap.Status == "done" &&
                             string.Equals(snap.Kind, "scene", StringComparison.OrdinalIgnoreCase) &&
                             snap.Scene is int genSn &&
                             snap.Clip is int genCn)
                    {
                        S._message = $"Clip S{genSn:D2}C{genCn:D2} gen finished — Play scene when you want the updated composite";
                        if (S._selectedScene == genSn)
                            await S.LoadSelectedDetailAsync(genSn);
                    }
                    else if (string.Equals(snap.Kind, "youtube_upload", StringComparison.OrdinalIgnoreCase))
                    {
                        if (snap.Status == "done")
                        {
                            await S.RefreshYouTubeStatusAsync();
                            S._message = snap.Message ?? "Uploaded to YouTube";
                            S._error = null;
                        }
                        else if (snap.Status == "error")
                        {
                            S._error = snap.Error ?? snap.Message ?? "YouTube upload failed";
                            S._message = null;
                        }
                    }
                    S.StateHasChanged();
                });
            }
            else _ = S.InvokeAsync(S.StateHasChanged);
        }


        internal void OnJobLog(string line)
        {
            if (_job is not null)
                _job.Message = line;
            _ = S.InvokeAsync(S.StateHasChanged);
        }


        internal async Task CancelAsync()
        {
            try
            {
                await S.Engine.CancelJobAsync();
                S._message = "Cancel requested";
                var jobs = await S.Engine.GetJobAsync();
                _job = jobs?.Job;
            }
            catch (Exception ex) { S._error = ex.Message; }
        }


        internal Task EnsureHubAsync() => S.Hub.EnsureStartedAsync();

    }
}
