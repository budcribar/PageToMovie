using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using System.Text;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;
using PageToMovie.Core.Utils;
using PageToMovie.Cut.Cut;

namespace PageToMovie.Web.Components.Pages;

public partial class Review
{
    /// <summary>Playback domain for the Review page. Owns related UI state and behavior.</summary>
    public sealed class ReviewPlayback
    {
        private readonly Review S;
        public ReviewPlayback(Review host) => S = host;

        internal string? _clientSceneUrl;

        internal string? _clientStitchStatus;

        internal bool _clientStitching;

        internal string? _clientWipUrl;

        internal string? _clientClipUrl;

        internal string? _clipPlayError;

        internal string? _clipServerSrcCached;

        internal long _clipVideoKey;

        internal int? _playSceneAfterRemux;

        internal bool _playWipAfterRemux;

        internal int? _playingClipNum;

        internal int? _playingClipScene;

        internal int? _playingScene;

        internal string? _sceneServerSrcCached;

        internal int? _sceneServerSrcScene;

        internal long _sceneVideoKey;

        internal bool _showClipPlayer;

        internal bool _showScenePlayer;

        internal bool _showWipPlayer;

        internal long _wipBytes;

        internal bool _wipCanBuild;

        internal bool _wipExists;

        internal string? _wipPath;

        internal string? _wipReason;

        internal string? _wipServerSrcCached;

        // CacheBust() stamps the current second, so calling it inline in markup re-evaluates on
            // every render (any SignalR/job-poll re-render elsewhere on the page) and gives the <video>
            // a new src each time, which makes the browser reload the resource and restart playback —
            // looks like looping. Memoized per key below instead of recomputed per call.
            internal string? _wipServerSrcForProject;

        /// <summary>WIP player is showing a saved Finish <c>movie.mp4</c>, not a take stitch.</summary>
        internal bool _playingFinishedCut;

        internal bool _wipStale;

        internal string? _wipUpdatedAt;

        internal long _wipVideoKey;


        internal bool CanPlayMovie =>
            _wipExists || _wipCanBuild || S.MediaFolder.IsConnected || S.MediaFolder.IsSyncing || S.List._scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);


        /// <summary>
        /// True only once real video actually exists — a browser or server cut, a scene composite, or clips
        /// on disk. Unlike <see cref="CanPlayMovie"/> this does NOT count a merely-connected media folder, so
        /// clip-dependent actions (Play, Share, AI review) stay disabled until clips exist.
        /// </summary>
        internal bool HasGeneratedClips =>
            _wipExists
            || !string.IsNullOrEmpty(_clientWipUrl)
            || S.List._scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);

        /// <summary>
        /// Scene-row Play on Review: every planned clip must be a real MP4.
        /// Per-clip Play uses <see cref="ReviewListState.ClipIsPlayable"/> instead.
        /// </summary>
        internal (bool CanPlay, string? Reason) DecideScenePlay(int scene)
        {
            if (scene <= 0)
                return (false, "Select a scene first");

            var syncing = S.MediaFolder.IsSyncingProject(S._projectId);
            var syncReason = syncing
                ? ScenePlayGate.MediaStillDownloadingReason(
                    S.MediaFolder.SyncCurrent, S.MediaFolder.SyncTotal, S.MediaFolder.LastStatus)
                : null;

            if (S.List._selectedDetail is { SceneNumber: var dsn, Clips: { Count: > 0 } } detail
                && dsn == scene)
            {
                var missing = detail.Clips
                    .Where(c => !ScenePlayGate.HasServerVideo(c.SizeBytes))
                    .Select(c => c.ClipNumber)
                    .ToList();
                return ScenePlayGate.DecideScenePlay(
                    scene, detail.ClipCount, missing, hasLocalVideo: null,
                    detail.CompositeExists, syncing, syncReason);
            }

            var summary = S.List._scenes.FirstOrDefault(s => s.SceneNumber == scene);
            if (summary is null)
                return (false, $"S{scene:D2} has no clips yet");
            return ScenePlayGate.DecideScenePlay(
                scene,
                summary.ClipCount,
                summary.ClipsMissingServerVideo,
                hasLocalVideo: null,
                summary.CompositeExists,
                syncing,
                syncReason);
        }

        internal bool CanPlayScene(int scene) => DecideScenePlay(scene).CanPlay;

        internal string ScenePlayTitle(int scene)
        {
            var decided = DecideScenePlay(scene);
            return decided.CanPlay
                ? "Play scene (browser stitch from clips)"
                : decided.Reason ?? "Scene is not ready to play";
        }


        internal string WipPlayTitle
        {
            get
            {
                if (!_wipCanBuild && !_wipExists && string.IsNullOrEmpty(_clientWipUrl)
                    && !S.MediaFolder.IsConnected && !S.MediaFolder.IsSyncing)
                    return "No scene videos were found";
                if (_wipStale || !_wipExists)
                    return "Play full movie (combine scenes in browser)";
                return "Play full movie (up to date)";
            }
        }


        internal async Task RefreshWipMetaAsync()
        {
            try
            {
                var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
                _wipExists = meta?.Exists ?? false;
                _wipStale = (meta?.Stale ?? false) || !_wipExists;
                _wipCanBuild = meta?.CanBuild ?? false;
                _wipReason = meta?.Reason;
                _wipPath = meta?.Path;
                _wipUpdatedAt = meta?.UpdatedAt;
                _wipBytes = meta?.Bytes ?? 0;
                if (!_wipExists)
                    _showWipPlayer = false;
            }
            catch
            {
                _wipExists = false;
                _wipStale = true;
                _wipCanBuild = false;
            }
        }


        /// <summary>
        /// Play full movie: a fresh Finish <c>movie.mp4</c> when present, else
        /// stream on-disk WIP when current, else stitch composites/clips in the browser.
        /// </summary>
        internal async Task PlayWipAsync()
        {
            // S._busy flips true synchronously, before the first await — see PlaySceneAsync's comment
            // for why (a fast second click otherwise slips past this guard and races the first over
            // shared local blob caches).
            if (S._busy || _clientStitching) return;
            S._busy = true;
            try
            {
                _clipPlayError = null;
                S.List._activeTab = ReviewTab.Play;
                _showWipPlayer = true;
                await RefreshWipMetaAsync();

                if (await TryPlayFinishedCutAsync())
                    return;

                if (HasFreshClientWip())
                {
                    S._message = "Playing WIP";
                    return;
                }

                await RefreshWipMetaAsync();
                if (HasFreshServerWip())
                {
                    ShowServerWip();
                    return;
                }

                var sceneNums = CollectPlayableSceneNumbers();
                if (sceneNums.Count == 0)
                {
                    S._error = MissingWipScenesError();
                    return;
                }

                await StitchWipInBrowserAsync(sceneNums);
            }
            finally
            {
                S._busy = false;
            }
        }

        private bool HasFreshClientWip() =>
            !string.IsNullOrEmpty(_clientWipUrl) && !_wipStale && !_playingFinishedCut;

        /// <summary>
        /// Full-movie Play: use a saved Finish <c>movie.mp4</c> when
        /// <c>cut.project.json</c> still matches that merge. Clip/scene Play
        /// does not call this.
        /// </summary>
        private async Task<bool> TryPlayFinishedCutAsync()
        {
            var url = await TryResolveFinishedCutUrlAsync();
            if (string.IsNullOrWhiteSpace(url))
            {
                if (_playingFinishedCut)
                {
                    _clientWipUrl = null;
                    _playingFinishedCut = false;
                }

                return false;
            }

            _showClipPlayer = false;
            _showScenePlayer = false;
            _clientSceneUrl = null;
            _showWipPlayer = true;
            _clientWipUrl = url;
            _playingFinishedCut = true;
            _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = "Playing finished movie";
            return true;
        }

        /// <summary>
        /// Fresh Finish <c>movie.mp4</c> blob when <see cref="CutFinishedMovie.ShouldPlay"/>
        /// is true. Play and Share both call this — do not fork.
        /// </summary>
        internal async Task<string?> TryResolveFinishedCutUrlAsync()
        {
            if (!S.MediaFolder.IsConnected || string.IsNullOrWhiteSpace(S._projectId))
                return null;
            try
            {
                var (found, size) = await S.MediaFolder.StatLocalFileAsync(
                    S._projectId, CutPlayMerge.MovieFileName);
                var jsonBytes = await S.MediaFolder.ReadLocalBytesAsync(
                    $"{S._projectId}/{CutClipNaming.ProjectFileName}", minBytes: 2);
                var json = jsonBytes is { Length: > 0 } ? Encoding.UTF8.GetString(jsonBytes) : null;
                if (!CutFinishedMovie.ShouldPlay(json, found && size > 0))
                    return null;
                return await S.MediaFolder.GetLocalBlobUrlAsync(
                    S._projectId, CutPlayMerge.MovieFileName, forceRefresh: true);
            }
            catch
            {
                return null;
            }
        }

        private bool HasFreshServerWip() =>
            _wipExists && !_wipStale;

        private void ShowServerWip()
        {
            _clientWipUrl = null;
            _playingFinishedCut = false;
            _showWipPlayer = true;
            _showScenePlayer = false;
            _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = "Playing WIP (up to date)";
        }

        private List<int> CollectPlayableSceneNumbers() =>
            S.List._scenes
                .Where(s => s.CompositeExists || s.ClipsOnDisk > 0 || S.MediaFolder.IsConnected || S.MediaFolder.IsSyncing)
                .OrderBy(s => s.SceneNumber)
                .Select(s => s.SceneNumber)
                .ToList();

        private string MissingWipScenesError() =>
            S.MediaFolder.IsConnected
                ? "No scene videos were found in your local media folder or on the server."
                : "Connect your local media folder to rebuild this movie from your synced clips.";

        private async Task StitchWipInBrowserAsync(List<int> sceneNums)
        {
            _clientStitching = true;
            S._error = null;
            _clientStitchStatus = "Collecting scenes…";
            _showClipPlayer = false;
            _showScenePlayer = false;
            _clientSceneUrl = null;
            _showWipPlayer = true;
            _clientWipUrl = null;
            _playingFinishedCut = false;
            try
            {
                // Revoke the OLD preview before collecting new segments — see the comment in
                // EnsureShareableMovieUrlAsync for why revoking after collection can destroy a
                // blob the segments list still needs.
                await S.Stitch.RevokePreviewUrlAsync();
                var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
                var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
                var segs = await S.Stitch.CollectAndMixSceneSegmentInfosAsync(S._projectId, sceneNums, S.List._scenes, stale);
                if (segs.Count == 0)
                {
                    S._error = "No scene videos were found";
                    _showWipPlayer = false;
                    return;
                }

                _clientStitchStatus = segs.Count == 1
                    ? "Loading…"
                    : $"Combining {segs.Count} file(s)…";
                var result = await S.Stitch.ConcatAsync(segs.Select(s => s.Url).ToList());
                if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                {
                    S._error = result.Error ?? "Browser stitch failed";
                    _showWipPlayer = false;
                    return;
                }

                _clientWipUrl = result.Url;
                _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                S._message = $"Preview ready — {segs.Count} scene(s)";
                await TryRegisterFilmBuildAsync(segs, result);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                _showWipPlayer = false;
                _clientWipUrl = null;
            }
            finally
            {
                _clientStitching = false;
                _clientStitchStatus = null;
            }
        }

        private async Task TryRegisterFilmBuildAsync(IReadOnlyList<ClientWipSegment> segs, ClientStitchResult result)
        {
            // film_build.v1: full segment EDL + studio.sha256 (non-fatal)
            try
            {
                _clientStitchStatus = "Saving cut timeline…";
                var reg = await S.Stitch.RegisterFilmBuildAfterWipStitchAsync(S._projectId, segs, result);
                if (reg.Ok && !string.IsNullOrWhiteSpace(reg.FilmId))
                    S._message = $"Preview ready — {segs.Count} scene(s) · film {reg.FilmId}";
            }
            catch
            {
                /* provenance must not block playback */
            }
        }


        /// <summary>Connect a local media folder from the WIP player's "needs rebuild" prompt, then
        /// immediately retry the rebuild — a single click instead of connect-then-hunt-for-play-again.</summary>
        internal async Task ConnectFolderForWipAsync()
        {
            await S.MediaFolder.EnsureHubHookAsync();
            var connected = await S.MediaFolder.ConnectFolderAsync();
            if (connected)
                await PlayWipAsync();
        }


        internal async Task PlaySceneAsync(int scene)
        {
            // S._busy must flip true synchronously, before the first await — otherwise a fast second
            // click (e.g. double-click) can slip past this guard during the window between it and
            // wherever the flag used to get set further down, running a second concurrent stitch that
            // races the first over the same locally-cached clip blob URLs (one call's blob gets
            // revoked-and-replaced out from under the other's in-flight ffmpeg fetch — "Failed to
            // fetch"). Same fix applied to every other Play*/stitch entry point in this file and
            // Scenes.razor.
            if (S._busy || _clientStitching) return;
            var gate = DecideScenePlay(scene);
            if (!gate.CanPlay)
            {
                S._error = gate.Reason;
                return;
            }
            S._busy = true;
            try
            {
                _clipPlayError = null;
                _showClipPlayer = false;
                S.List._activeTab = ReviewTab.Play;
                await RefreshWipMetaAsync();
                var summary = S.List._scenes.FirstOrDefault(s => s.SceneNumber == scene);
                var stale = (await S.Engine.GetWipMovieMetaAsync(S._projectId))?.StaleScenes?.Contains(scene) ?? false;
                var compositeOk = summary?.CompositeExists == true;
                var needsStitch = !compositeOk || stale;

                if (!needsStitch)
                {
                    var localComposite = await TryLocalSceneCompositeUrlAsync(scene);
                    _clientSceneUrl = localComposite;
                    _showWipPlayer = false;
                    _playingScene = scene;
                    _showScenePlayer = true;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S._message = $"Playing S{scene:D2} composite";
                    return;
                }

                var clipsOnDisk = summary?.ClipsOnDisk ?? 0;
                if (clipsOnDisk <= 0 && !compositeOk)
                {
                    S._error = $"No clips for S{scene:D2} — generate clips first";
                    return;
                }

                _clientStitching = true;
                S._error = null;
                _clientStitchStatus = "Collecting clips…";
                _showWipPlayer = false;
                _clientWipUrl = null;
                _playingFinishedCut = false;
                _playingScene = scene;
                _showScenePlayer = true;
                _clientSceneUrl = null;
                try
                {
                    SceneDetail? detail = S.List._selectedDetail is { SceneNumber: var dsn } && dsn == scene
                        ? S.List._selectedDetail
                        : null;
                    var urls = await S.Stitch.CollectClipUrlsAsync(
                        S._projectId, scene, detail, requireAllPlannedClips: true);
                    if (urls.Count == 0 && compositeOk)
                    {
                        _clientSceneUrl = await TryLocalSceneCompositeUrlAsync(scene);
                        _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        S._message = $"Playing S{scene:D2} composite (may be stale)";
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(S.Stitch.LastCollectError))
                    {
                        FailScenePlayer(S.Stitch.LastCollectError);
                        return;
                    }

                    var stitched = await S.Stitch.TryConcatSceneClipsAsync(
                        urls,
                        FriendlyMissingClipsError(scene, S.Stitch.LastCollectError, S.MediaFolder.IsConnected),
                        status => _clientStitchStatus = status,
                        err => FailScenePlayer(FriendlyStitchError(scene, err)));
                    if (stitched is null)
                        return;

                    _clientSceneUrl = stitched;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S._message = urls.Count == 1
                        ? $"Playing S{scene:D2} (single clip)"
                        : $"Playing S{scene:D2} — {urls.Count} clips stitched in browser";
                }
                catch (Exception ex)
                {
                    FailScenePlayer(FriendlyStitchError(scene, ex.Message));
                }
                finally
                {
                    _clientStitching = false;
                    _clientStitchStatus = null;
                }
            }
            finally
            {
                S._busy = false;
            }
        }


        private void FailScenePlayer(string error)
        {
            S._error = error;
            _showScenePlayer = false;
            _playingScene = null;
            _clientSceneUrl = null;
        }

        private async Task<string?> TryLocalSceneCompositeUrlAsync(int scene)
        {
            if (!S.MediaFolder.IsConnected)
                return null;
            try
            {
                var local = await S.MediaFolder.GetLocalBlobUrlAsync(
                    S._projectId, $"assets/video/scene_{scene:D2}.mp4");
                return string.IsNullOrWhiteSpace(local) ? null : local;
            }
            catch
            {
                return null;
            }
        }

        internal static string FriendlyMissingClipsError(int scene, string? collectError, bool mediaFolderConnected)
        {
            if (!string.IsNullOrWhiteSpace(collectError))
                return collectError;
            return mediaFolderConnected
                ? $"No playable clips for S{scene:D2}."
                : $"No playable clips for S{scene:D2}. Connect your local media folder if the clips are on this computer, or generate them again.";
        }

        internal static string FriendlyStitchError(int scene, string? stitchError)
        {
            if (!LooksLikeHttpMissing(stitchError))
                return stitchError ?? "Could not combine clips";
            if (stitchError is { Length: > 0 } named
                && named.Contains(" C", StringComparison.Ordinal))
                return named;
            return $"S{scene:D2} clip video is missing. Connect your local media folder if the clips are on this computer, or generate them again.";
        }

        internal static bool LooksLikeHttpMissing(string? error) =>
            error is not null
            && (error.Contains("404", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("clip video not found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase));

        internal async Task HideScenePlayerAsync()
        {
            _showScenePlayer = false;
            _playingScene = null;
            if (!string.IsNullOrEmpty(_clientSceneUrl))
            {
                _clientSceneUrl = null;
                await S.Stitch.RevokePreviewUrlAsync();
            }
        }


        internal async Task HideWipPlayerAsync()
        {
            _showWipPlayer = false;
            _playingFinishedCut = false;
            if (!string.IsNullOrEmpty(_clientWipUrl))
            {
                _clientWipUrl = null;
                await S.Stitch.RevokePreviewUrlAsync();
            }
        }


        /// <summary>
        /// Play one clip using the same local-first resolution as scene Play:
        /// media-folder blob, else a reachable server URL. Never points the
        /// player at a 404 <c>ClipVideoUrl</c>.
        /// </summary>
        internal async Task PlayClipAsync(int scene, int clip)
        {
            // Per-clip Play: only this clip. Do not consult scene completeness.
            if (S._busy || _clientStitching) return;
            S._busy = true;
            try
            {
                _clipPlayError = null;
                S._error = null;
                SceneDetail? detail = S.List._selectedDetail is { SceneNumber: var dsn } && dsn == scene
                    ? S.List._selectedDetail
                    : null;
                var urls = await S.Stitch.CollectClipUrlsAsync(
                    S._projectId, scene, detail, clipNumbers: new[] { clip });
                var decided = DecideClipPlay(
                    urls, S.Stitch.LastCollectError, scene, clip, S.MediaFolder.IsConnected);
                if (decided.Src is null)
                {
                    FailClipPlayer(decided.Error);
                    return;
                }

                _clientClipUrl = decided.Src;
                _playingClipScene = scene;
                _playingClipNum = clip;
                _showWipPlayer = false;
                _showScenePlayer = false;
                _showClipPlayer = true;
                _clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                S._message = $"Playing S{scene:D2}C{clip:D2}";
            }
            catch (Exception ex)
            {
                FailClipPlayer(FriendlyClipPlayError(scene, clip, ex.Message, S.MediaFolder.IsConnected));
            }
            finally
            {
                S._busy = false;
            }
        }

        /// <summary>
        /// Local-first collect result → playable src, or a friendly in-card error and no src.
        /// A 404 server URL is never returned: <see cref="ClientVideoStitchService.CollectClipUrlsAsync"/>
        /// only yields a local blob or a reachable server URL.
        /// </summary>
        internal static (string? Src, string? Error) DecideClipPlay(
            IReadOnlyList<string> urls,
            string? collectError,
            int scene,
            int clip,
            bool mediaFolderConnected)
        {
            var src = urls.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (!string.IsNullOrWhiteSpace(src))
                return (src, null);
            return (null, FriendlyClipPlayError(scene, clip, collectError, mediaFolderConnected));
        }

        internal static string FriendlyClipPlayError(
            int scene, int clip, string? collectError, bool mediaFolderConnected)
        {
            if (LooksLikeHttpMissing(collectError))
            {
                return $"{ScenePlayGate.FormatClipLabel(scene, clip)} clip video is missing. Connect your local media folder if the clips are on this computer, or generate them again.";
            }

            if (!string.IsNullOrWhiteSpace(collectError))
                return collectError;
            return ClientVideoStitchService.FormatMissingClipPlayError(
                new[] { ScenePlayGate.FormatClipLabel(scene, clip) }, mediaFolderConnected);
        }

        private void FailClipPlayer(string? error)
        {
            _clipPlayError = string.IsNullOrWhiteSpace(error)
                ? FriendlyClipPlayError(_playingClipScene ?? 0, _playingClipNum ?? 0, null, S.MediaFolder.IsConnected)
                : error;
            _showClipPlayer = false;
            _playingClipScene = null;
            _playingClipNum = null;
            _clientClipUrl = null;
        }

        internal void ClearClipPlayError() => _clipPlayError = null;


        internal void HideClipPlayer()
        {
            _showClipPlayer = false;
            _playingClipScene = null;
            _playingClipNum = null;
            _clientClipUrl = null;
            _clipPlayError = null;
        }


        internal static string CacheBust(string url) => KeyFormatting.CacheBust(url);


        internal string? WipServerSrc()
        {
            if (_wipServerSrcForProject != S._projectId)
            {
                _wipServerSrcForProject = S._projectId;
                _wipServerSrcCached = CacheBust(S.Engine.WipMovieUrl(S._projectId));
            }
            return _wipServerSrcCached;
        }


        internal string? SceneServerSrc(int sn)
        {
            if (_sceneServerSrcScene != sn)
            {
                _sceneServerSrcScene = sn;
                _sceneServerSrcCached = CacheBust(S.Engine.CompositeVideoUrl(S._projectId, sn));
            }
            return _sceneServerSrcCached;
        }


        internal string? ClipServerSrc(int scene, int clip)
        {
            if (S._clipServerSrcKey != (scene, clip))
            {
                S._clipServerSrcKey = (scene, clip);
                _clipServerSrcCached = CacheBust(S.Engine.ClipVideoUrl(S._projectId, scene, clip));
            }
            return _clipServerSrcCached;
        }

    }
}
