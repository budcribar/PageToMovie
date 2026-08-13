using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

using PageToMovie.Core.Utils;
namespace PageToMovie.Web.Components.Pages;

public partial class Review
{
    /// <summary>Share domain for the Review page. Owns related UI state and behavior.</summary>
    public sealed class ReviewShare
    {
        private readonly Review S;
        public ReviewShare(Review host) => S = host;

        internal bool _confirmedIncompletePublish;

        internal bool _demoAcceptedGuidelines;

        internal string _demoDescription = "";

        internal bool _demoIsAiSynthetic = true;

        internal bool _demoMadeForKids;

        internal string _demoTitle = "";

        internal DotNetObjectReference<Review>? _dotNetRef;

        internal int _incompleteScenesCount;

        internal bool _isPublishing;

        /// <summary>True after EnsureShareableMovieUrlAsync if the last built cut has no background
            /// music because the local media folder wasn't connected on this tab — MixSceneMusicAsync has
            /// no server fallback for music (unlike clips) and is deliberately best-effort/never-blocking,
            /// so without this flag a tab that never connected its folder silently exports/uploads a
            /// musicless movie with no indication anything was skipped. Callers append a note using it.</summary>
            internal bool _lastExportMissingMusic;

        internal int _missingClipsCount;

        internal int _publishProgressPct;

        internal string _publishProgressStatus = "";

        internal bool _showIncompleteWarning;

        internal string _youTubeDescription = "";

        internal string _youTubePrivacy = "unlisted";

        internal YouTubeStatusDto? _youTubeStatus;

        internal string _youTubeTitle = "";

        internal YouTubeUploadInfo? _youTubeUpload;


        internal void PrepopulateDemoFields()
        {
            if (string.IsNullOrWhiteSpace(_demoTitle))
            {
                _demoTitle = FormatDisplayTitle(S._projectId);
            }
            if (string.IsNullOrWhiteSpace(_demoDescription))
            {
                _demoDescription = BuildSmartDescription(S._projectId, _demoTitle);
            }
        }


        internal static string FormatDisplayTitle(string? rawProjectId)
        {
            if (string.IsNullOrWhiteSpace(rawProjectId))
                return "Untitled Short Film";

            var parts = rawProjectId.Trim().Split(['/', '\\']);
            var name = parts[parts.Length - 1].Trim();

            if (name.StartsWith("TellTaleHeart", StringComparison.OrdinalIgnoreCase))
                return "The Tell-Tale Heart";

            name = CommonRegex.Replace(name, @"V\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!name.Contains(' '))
                name = CommonRegex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");

            return name.Trim();
        }


        internal static string BuildSmartDescription(string? rawProjectId, string title)
        {
            var clean = (rawProjectId ?? "").Trim();
            if (clean.Contains("TellTaleHeart", StringComparison.OrdinalIgnoreCase))
            {
                return "A cinematic short film adaptation of Edgar Allan Poe’s classic Gothic horror story “The Tell-Tale Heart”. Produced with PageToMovie AI Film Studio.";
            }
            return $"A cinematic short film adaptation of “{title}”. Produced with PageToMovie AI Film Studio.";
        }


        public void ReportPublishProgress(int pct, string status)
        {
            _publishProgressPct = Math.Clamp(pct, 0, 100);
            _publishProgressStatus = status;
            S.StateHasChanged();
        }


        /// <summary>Can publish when browser stitch or fresh on-disk movie is available, or scenes can be stitched.</summary>
        internal bool CanShareMovie =>
            !string.IsNullOrEmpty(S.Playback._clientWipUrl)
            || (S.Playback._wipExists && !S.Playback._wipStale)
            || S.MediaFolder.IsConnected
            || S.MediaFolder.IsSyncing
            || S.List._scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);


        internal async Task RefreshYouTubeStatusAsync()
        {
            try
            {
                _youTubeStatus = await S.Engine.GetYouTubeStatusAsync();
                _youTubeUpload = await S.Engine.GetYouTubeUploadInfoAsync(S._projectId);
            }
            catch { /* optional feature — ignore */ }
        }


        internal void HandleYouTubeOAuthRedirect()
        {
            var uri = new Uri(S.Nav.Uri);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (!query.TryGetValue("youtube", out var status))
                return;
            if (status == "connected")
                S._message = "YouTube channel connected";
            else if (status == "error")
                S._error = query.TryGetValue("message", out var msg) ? msg.ToString() : "YouTube connect failed";
            // Drop the one-shot query params so a page refresh doesn't re-show the toast.
            S.Nav.NavigateTo(uri.GetLeftPart(UriPartial.Path), replace: true);
        }


        internal void CheckIncompleteMovieState()
        {
            _missingClipsCount = S.List._scenes
                .Where(s => !s.CompositeExists)
                .Sum(s => Math.Max(0, s.ClipCount - s.ClipsOnDisk));
            _incompleteScenesCount = S.List._scenes
                .Count(s => !s.CompositeExists && s.ClipsOnDisk < s.ClipCount);
        }


        internal async Task ConfirmIncompleteAndPublishAsync()
        {
            _confirmedIncompletePublish = true;
            _showIncompleteWarning = false;
            await PublishDemoAsync();
        }


        internal void CancelIncompleteWarning()
        {
            _showIncompleteWarning = false;
        }


        /// <summary>
        /// Build cut + publish to /api/demos → YouTube upload. Gallery lists the film once YouTube id is set.
        /// </summary>
        internal async Task PublishDemoAsync()
        {
            if (!_demoAcceptedGuidelines)
            {
                S._error = "Accept the gallery guidelines before submitting.";
                return;
            }

            S._busy = true;
            _isPublishing = true;
            _publishProgressPct = 5;
            _publishProgressStatus = "Preparing movie cut for upload...";
            _dotNetRef ??= DotNetObjectReference.Create(S);
            S._error = null;
            S._message = null;
            try
            {
                var mediaUrl = await EnsureShareableMovieUrlAsync();
                if (string.IsNullOrWhiteSpace(mediaUrl))
                {
                    S._error = "Could not build a movie to share — generate clips first.";
                    return;
                }

                var uploadPath = "/api/demos";
                var token = S.Session.Token;
                var title = string.IsNullOrWhiteSpace(_demoTitle) ? S._projectId : _demoTitle.Trim();
                var description = string.IsNullOrWhiteSpace(_demoDescription) ? "" : _demoDescription.Trim();

                // Register stitched export hash so server can auto-public trusted demos
                if (mediaUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                {
                    var expPath = $"assets/exports/{S._projectId}_demo.mp4";
                    await S.MediaFolder.RegisterBlobAsExportAsync(S._projectId, mediaUrl, expPath);
                }

                var res = await S.JS.InvokeAsync<System.Text.Json.JsonElement>(
                    "PageToMovieExport.uploadDemoMovieAsync",
                    mediaUrl,
                    uploadPath,
                    token,
                    new
                    {
                        title,
                        description,
                        projectId = S._projectId,
                        fileName = $"{S._projectId}_demo.mp4",
                        acceptedGuidelines = true,
                        madeForKids = _demoMadeForKids,
                        isAiSynthetic = _demoIsAiSynthetic,
                    },
                    _dotNetRef);

                if (!res.TryGetProperty("success", out var ok) || !ok.GetBoolean())
                {
                    if (S.Playback._wipExists && !S.Playback._wipStale && string.IsNullOrEmpty(S.Playback._clientWipUrl))
                    {
                        var pub = await S.Engine.PublishDemoFromWipAsync(
                            S._projectId,
                            title,
                            string.IsNullOrWhiteSpace(description) ? null : description,
                            acceptedGuidelines: true,
                            madeForKids: _demoMadeForKids,
                            isAiSynthetic: _demoIsAiSynthetic);
                        if (pub?.Ok is true)
                        {
                            S.List._activeTab = ReviewTab.Review;
                            S._message = (pub.Message ?? $"“{pub.Demo?.Title ?? title}” sent to YouTube — it appears in the gallery when the upload finishes.") +
                                (_lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
                            return;
                        }
                        S._error = pub?.Error ?? "Demo submit failed";
                        return;
                    }

                    var err = res.TryGetProperty("error", out var e) ? e.GetString() : "upload failed";
                    S._error = err ?? "Demo upload failed";
                    return;
                }

                var publishedTitle = title;
                if (res.TryGetProperty("demo", out var demoEl) && demoEl.ValueKind == System.Text.Json.JsonValueKind.Object
                    && demoEl.TryGetProperty("title", out var tEl))
                {
                    publishedTitle = tEl.GetString() ?? publishedTitle;
                }

                var msg = res.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
                S.List._activeTab = ReviewTab.Review;
                S._message = (msg ?? $"“{publishedTitle}” sent to YouTube — it appears in the gallery when the upload finishes.") +
                    (_lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
                _isPublishing = false;
            }
        }


        /// <summary>Return a browser-fetchable URL for the full cut (blob or authenticated WIP).</summary>
        internal async Task<string?> EnsureShareableMovieUrlAsync()
        {
            await S.Playback.RefreshWipMetaAsync();

            // Best-effort, no dialog: the common case is the folder was already connected on another
            // tab and this one just never got the silent no-gesture reconnect — try once before
            // building, so music generated elsewhere still gets picked up here instead of being
            // silently dropped. TryReconnectAsync only succeeds if the browser already has persisted
            // permission (no folder picker popup); it's a no-op otherwise, unlike ConnectFolderAsync
            // which would intrusively prompt every export for users who never connected a folder.
            if (!S.MediaFolder.IsConnected)
                await S.MediaFolder.TryReconnectAsync();
            _lastExportMissingMusic = !S.MediaFolder.IsConnected;

            // Stitch fresh in browser to ensure all newly generated clips are included with zero duplicates
            var sceneNums = S.List._scenes
                .Where(s => s.CompositeExists || s.ClipsOnDisk > 0)
                .OrderBy(s => s.SceneNumber)
                .Select(s => s.SceneNumber)
                .ToList();
            if (sceneNums.Count == 0)
                return null;

            S.Playback._clientStitching = true;
            S.Playback._clientStitchStatus = "Preparing cut for upload…";
            try
            {
                // Revoke the OLD preview before collecting new segments, not after — CollectAndMix-
                // SceneSegmentsAsync's internal per-scene concatVideosAsync calls reuse the JS side's
                // single shared blob-tracking slot, so a scene segment with no music to mix can end up
                // being exactly the URL RevokePreviewUrlAsync() would revoke; calling it here (before
                // any new segment exists) means it can only ever revoke a blob from a prior, separate
                // operation, never one this call just built. Revoking after collection blew up the
                // final combine's fetch of that segment with "Failed to fetch" the moment that
                // coincidence happened — reproducible on a single, non-double-clicked Share & Export.
                await S.Stitch.RevokePreviewUrlAsync();
                var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
                var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
                var segs = await S.Stitch.CollectAndMixSceneSegmentInfosAsync(S._projectId, sceneNums, S.List._scenes, stale);
                if (segs.Count == 0) return null;
                var result = await S.Stitch.ConcatAsync(segs.Select(s => s.Url).ToList());
                if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                    throw new InvalidOperationException(result.Error ?? "Browser stitch failed");
                S.Playback._clientWipUrl = result.Url;
                S.Playback._showWipPlayer = true;
                S.Playback._wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try
                {
                    await S.Stitch.RegisterFilmBuildAfterWipStitchAsync(S._projectId, segs, result);
                }
                catch { /* non-fatal */ }
                return S.Playback._clientWipUrl;
            }
            finally
            {
                S.Playback._clientStitching = false;
                S.Playback._clientStitchStatus = null;
            }
        }


        internal async Task ConnectYouTubeAsync()
        {
            S._busy = true;
            S._error = null;
            try
            {
                var url = await S.Engine.GetYouTubeConnectUrlAsync();
                S.Nav.NavigateTo(url, forceLoad: true);
            }
            catch (Exception ex) { S._error = ex.Message; S._busy = false; }
        }


        internal async Task DisconnectYouTubeAsync()
        {
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.DisconnectYouTubeAsync();
                S._message = "YouTube channel disconnected";
                await RefreshYouTubeStatusAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task StartYouTubeUploadAsync()
        {
            S._busy = true;
            S._error = null;
            S._message = "Preparing movie cut for YouTube upload…";
            S.StateHasChanged();
            try
            {
                var mediaUrl = await EnsureShareableMovieUrlAsync();
                if (string.IsNullOrWhiteSpace(mediaUrl))
                {
                    S._error = "Could not build a movie to upload — generate scene clips first.";
                    return;
                }

                await S.Jobs.EnsureHubAsync();
                _dotNetRef ??= DotNetObjectReference.Create(S);

                if (mediaUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                {
                    var res = await S.JS.InvokeAsync<System.Text.Json.JsonElement>(
                        "PageToMovieExport.uploadDemoMovieAsync",
                        mediaUrl,
                        "/api/jobs/youtube-upload",
                        S.Session.Token,
                        new
                        {
                            projectId = S._projectId,
                            title = _youTubeTitle,
                            description = _youTubeDescription,
                            privacyStatus = _youTubePrivacy,
                            fileName = $"{S._projectId}_wip.mp4",
                        },
                        _dotNetRef);

                    if (!res.TryGetProperty("success", out var ok) || !ok.GetBoolean())
                    {
                        S._error = res.TryGetProperty("error", out var err) ? err.GetString() : "Failed to upload movie cut to server";
                        return;
                    }
                }
                else
                {
                    await S.Engine.StartYouTubeUploadAsync(new StartYouTubeUploadRequest
                    {
                        ProjectId = S._projectId,
                        Title = _youTubeTitle,
                        Description = _youTubeDescription,
                        PrivacyStatus = _youTubePrivacy,
                    });
                }

                S.List._activeTab = ReviewTab.Review;
                S._message = "Uploading to YouTube…" +
                    (_lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs._job = jobs?.Job;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }

    }
}
